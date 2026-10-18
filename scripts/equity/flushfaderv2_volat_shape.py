"""FlushFaderV2 — V2-14: does ONE continuous volatility term beat the four volat band dummies? (user 2026-10-18)

The V2 model (A3 + dslo >= 4 % + s20) carries volatility as 4 dummies per component (60-90 / 90-140 / 140-250 / 250+ bp,
the 40-60 band the base). The candidates replace them with ONE term, winsorised at the upper end (the engine's gate is
the lower one, 40 bp), standardised on the fit set:
    lin   = clip(volat, 40, cap)          sqrt = sqrt(clip(volat, 40, cap))          log = ln(clip(volat, 40, cap))
for caps at fixed bp and at the fit set's own 95 / 99 / 99.5 % quantiles. Everything else is V2's design.

Book = the production reference (flushfaderv2_reference_book.parquet), barnum >= 22 (the fit set of V2, ATR5-gated).
Scores, V2's method (docs/flushfaderv2_results.md V2-3 / V2-5):
  * sized PF−1 on the two year-block holdouts (fit 2020–23 -> apply 2024–26, and the mirror), weights = fitted PF−1 /
    its fit-set mean, clip [0.25, 4], mean 1 on the apply set;
  * the MATCHED-STEEPNESS credit vs the V2 bands raised to the power p at which their weights' spread equals the model's;
  * the out-of-sample fit of each component: the logistic log-loss of p, and the quasi-Poisson deviance of W and L
    (the magnitude models, where volatility does most of its work).
Run from research/:  python -u scripts/equity/flushfaderv2_volat_shape.py > data/flushfaderv2/volat_shape.md 2> data/flushfaderv2/volat_shape.log; echo exit $?
"""
import sys, time
import numpy as np, pandas as pd
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, file=sys.stderr, flush=True)
CREDIT = 0.001
B = pd.read_parquet("data/flushfaderv2/flushfaderv2_reference_book.parquet")
B = B[~B.young].sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
n = len(B)
R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100
WIN = R > 0
YR = B.trade_date.astype(str).str[:4].astype(int).values
early, late, allm = YR <= 2023, YR > 2023, np.ones(n, bool)
V = B.volat.values.astype(float) if "volat" in B else B.volat_20m.values * 1e4
log(f"{n:,} trades; volat bp: min {V.min():.1f}, median {np.median(V):.1f}, p95 {np.quantile(V, .95):.1f}, p99 {np.quantile(V, .99):.1f}, max {V.max():.1f}")

gb, vb = B.gap_band.values, B.volat_band.values
COMMON = [np.ones(n), (gb == 1) * 1.0, (gb == 2) * 1.0, B.rate600_fast.values * 1.0, B.s_tier.values * 1.0,
          B.dslo_flag.values * 1.0, B.s20_z.values]                     # s20_z: production's standardisation (constants)
BANDS = [(vb == k) * 1.0 for k in range(1, 5)]

def volat_col(shape, cap, fit):
    c = cap if not isinstance(cap, str) else np.quantile(V[fit], float(cap[1:]) / 100)
    x = np.clip(V, 40.0, c)
    x = {"lin": x, "sqrt": np.sqrt(x), "log": np.log(x)}[shape]
    return [(x - x[fit].mean()) / x[fit].std()]

def design(model, fit):
    if model == "bands": return np.column_stack(COMMON + BANDS)
    if model == "none": return np.column_stack(COMMON)
    shape, cap = model
    return np.column_stack(COMMON + volat_col(shape, cap, fit))

def fit_logistic(X, y, it=60):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); w = mu * (1 - mu) + 1e-12
        bn = np.linalg.solve((X * w[:, None]).T @ X, (X * w[:, None]).T @ (eta + (y - mu) / w))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def fit_loglink(X, y, it=60):
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta)
        bn = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ (eta + (y - mu) / mu))
        if np.max(np.abs(bn - b)) < 1e-10: return bn
        b = bn
    return b
def fit(model, m):
    X = design(model, m)
    return X, (fit_logistic(X[m], WIN[m] * 1.0), fit_loglink(X[m & WIN], R[m & WIN]), fit_loglink(X[m & ~WIN], -R[m & ~WIN]))
def predict(X, b):
    p = 1 / (1 + np.exp(-(X @ b[0]))); W = np.exp(X @ b[1]); L = np.exp(X @ b[2])
    return p, W, L, p / ((1 - p) * (L / W)) - 1
def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def raw_mult(model, m):
    X, b = fit(model, m); pf = predict(X, b)[3]
    return np.maximum(pf / pf[m].mean(), 0.0), X, b
def final_w(mult, app, p=1.0):
    w = np.clip(mult ** p, 0.25, 4.0)[app]; return w / w.mean()
def cv(w): return w.std() / w.mean()
def matched(mb, app, target):
    lo, hi = 0.0, 6.0
    for _ in range(60):
        mid = (lo + hi) / 2
        if cv(final_w(mb, app, mid)) < target: lo = mid
        else: hi = mid
    return (lo + hi) / 2
def oos(X, b, app):
    """out-of-sample fit on the apply set: logistic log-loss of p; quasi-Poisson deviance (per obs) of W and L"""
    p, W, L, _ = predict(X, b)
    pp = np.clip(p[app], 1e-9, 1 - 1e-9); y = WIN[app]
    ll = -np.mean(np.where(y, np.log(pp), np.log(1 - pp)))
    def dev(y_, mu):
        return 2 * np.mean(y_ * np.log(y_ / mu) - (y_ - mu))
    return ll, dev(R[app & WIN], W[app & WIN]), dev(-R[app & ~WIN], L[app & ~WIN])

HOLD = [("fwd", early, late), ("mirror", late, early)]
BASEM = {h: raw_mult("bands", f)[0] for h, f, _ in HOLD}
MODELS = ["bands", "none"] + [(s, c) for s in ["lin", "sqrt", "log"] for c in [150.0, 250.0, 400.0, 600.0, "q95", "q99", "q99.5", 1e9]]
def name(m):
    if isinstance(m, str): return {"bands": "**V2: 4 band dummies**", "none": "no volatility term"}[m]
    s, c = m
    return f"{s}, cap {'none' if c == 1e9 else (c if isinstance(c, str) else f'{c:.0f} bp')}"

rows = []
for m in MODELS:
    r = {}
    for h, f, app in HOLD:
        mult, X, b = raw_mult(m, f)
        wm = final_w(mult, app); c = cv(wm)
        p = matched(BASEM[h], app, c); wb = final_w(BASEM[h], app, p)
        r[h] = (pf1(wm * R[app]), pf1(wb * R[app]), p, c) + oos(X, b, app)
    ins = raw_mult(m, allm)[0]; wi = final_w(ins, allm)
    rows.append((m, r, pf1(wi * R)))
    log(name(m), f"fwd {r['fwd'][0]:.3f} mirror {r['mirror'][0]:.3f}")

print(f"# V2-14 — volatility: 4 band dummies vs ONE winsorised continuous term ({n:,} trades, barnum >= 22, ATR5-gated)\n")
print(f"volat (bp) on the book: median {np.median(V):.0f}, p95 {np.quantile(V, .95):.0f}, p99 {np.quantile(V, .99):.0f}, p99.5 {np.quantile(V, .995):.0f}, max {V.max():.0f}\n")
print("Sized PF−1 on the holdouts; credit = model − V2-bands^p at the model's spread (p < 1: the model is FLATTER than the bands). "
      "Fit = out-of-sample per-observation loss (lower = better): p log-loss, W and L deviance — shown as Δ vs the bands.\n")
print("| volatility term | params/comp | fwd | mirror | **mean** | credit fwd | credit mirror | **mean credit** | Δ p log-loss ×1e4 (fwd / mirror) | Δ W dev ×1e3 | Δ L dev ×1e3 | in-sample sized PF−1 |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
base = {h: rows[0][1][h] for h in ("fwd", "mirror")}
for m, r, ins in rows:
    k = 4 if m == "bands" else (0 if m == "none" else 1)
    cf, cm = r["fwd"][0] - r["fwd"][1], r["mirror"][0] - r["mirror"][1]
    dl = lambda i, s: f"{s * (r['fwd'][i] - base['fwd'][i]):+.1f} / {s * (r['mirror'][i] - base['mirror'][i]):+.1f}"
    print(f"| {name(m)} | {k} | {r['fwd'][0]:.3f} | {r['mirror'][0]:.3f} | **{(r['fwd'][0] + r['mirror'][0]) / 2:.3f}** | {cf:+.3f} | {cm:+.3f} | "
          f"**{(cf + cm) / 2:+.3f}**{' ✓' if cf > 0 and cm > 0 else ''} | {dl(4, 1e4)} | {dl(5, 1e3)} | {dl(6, 1e3)} | {ins:.3f} |")

# the calibration of the magnitude models by volatility decile (all years): bands vs the best of each shape
print("\n## Mean win W and mean loss L by volatility decile: actual vs fitted (all-years fits)\n")
best = {}
for s in ["lin", "sqrt", "log"]:
    cand = [(m, r) for m, r, _ in rows if not isinstance(m, str) and m[0] == s]
    best[s] = max(cand, key=lambda t: t[1]["fwd"][0] + t[1]["mirror"][0])[0]
fits = {"bands": predict(*fit("bands", allm))}
for s, m in best.items(): fits[name(m)] = predict(*fit(m, allm))
q = np.quantile(V, np.linspace(0, 1, 11)); dec = np.clip(np.digitize(V, q[1:-1]), 0, 9)
print("| volat decile (bp) | n | win % | fitted p (bands) | W actual | W bands | " + " | ".join(f"W {k}" for k in list(fits)[1:]) +
      " | L actual | L bands | " + " | ".join(f"L {k}" for k in list(fits)[1:]) + " | PF−1 actual | PF−1 bands | " + " | ".join(f"PF−1 {k}" for k in list(fits)[1:]) + " |")
print("|---|" + "---|" * (6 + 2 * (len(fits) - 1) + 2 + (len(fits) - 1) + 2 + (len(fits) - 1) - 1))
for d in range(10):
    m = dec == d; w = m & WIN; l = m & ~WIN
    cells = [f"{q[d]:.0f}–{q[d + 1]:.0f}", f"{m.sum():,}", f"{100 * WIN[m].mean():.1f}", f"{100 * fits['bands'][0][m].mean():.1f}",
             f"{R[w].mean():.2f}", f"{fits['bands'][1][w].mean():.2f}"] + [f"{fits[k][1][w].mean():.2f}" for k in list(fits)[1:]] + \
            [f"{-R[l].mean():.2f}", f"{fits['bands'][2][l].mean():.2f}"] + [f"{fits[k][2][l].mean():.2f}" for k in list(fits)[1:]] + \
            [f"{pf1(R[m]):.2f}", f"{fits['bands'][3][m].mean():.2f}"] + [f"{fits[k][3][m].mean():.2f}" for k in list(fits)[1:]]
    print("| " + " | ".join(cells) + " |")
log("done")
