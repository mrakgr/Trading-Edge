"""V2-53: the 20m z score (z20) as a SIZING term on top of its gate (user 2026-11-11: "We have it set so that it acts as a
gate <= -1.5. I wonder if going a step further and turning it as a sizing feature for values below 1.5 would give us a
benefit?").
z20 = (ln signal_vwap − the 1,200-bar volume-weighted mean of ln price) / that window's vw sd — the reference book's
column (every trade has z20 < −1.5: the gate). The gate stays; the question is whether z20 adds anything to the
PRODUCTION sizing model (V2-49: A3 common terms + s20 (60s grid) + CUSUM + the volume rate).
Harness = V2-42 / V2-48: three component fits (logit p, log W, log L), each era (2020–23 / 2024–26) predicted by a fit on
the other, units clip(PF−1, .125, 2), young × 0.8; Δ sized PF−1 vs production with the paired day bootstrap (2,000).
Shapes: linear z; the top-octile flag (z ≥ −1.88, the V2-2 table's weak band); a hinge at the median (−2.27).
Both cut points come from the full-book octile table (in-sample) — read them as an upper bound.
Run from research/:  python3 -u scripts/equity/flushfaderv2_z20.py > logs/z20_sizing.log 2>&1
"""
import json, duckdb, numpy as np
BOOK = 'data/flushfaderv2/flushfaderv2_reference_book.parquet'
COEF = 'models/flushfaderv2/v2_coefficients.json'
CREDIT, YOUNG_SCALE = 0.001, 0.8

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
def pf1(r): g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def zs(x):
    lo, hi = np.nanquantile(x, [0.01, 0.99]); c = np.clip(x, lo, hi); return np.nan_to_num((c - np.nanmean(c)) / np.nanstd(c))

con = duckdb.connect(); C = json.load(open(COEF))
B = con.execute(f"SELECT * FROM '{BOOK}' ORDER BY trade_date, symbol, signal_sec").df()
R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
YR = B.trade_date.astype(str).str[:4].astype(int).values; young = B.young.values.astype(bool); z = B.z20.values
print(f"{len(B):,} trades; z20 max {z.max():.4f} (the gate: < −1.5), p10/50/90 {np.quantile(z, .1):.2f} / {np.median(z):.2f} / {np.quantile(z, .9):.2f}")
print("Spearman z20 ~ " + ", ".join(f"{c} {B.z20.corr(B[c], method='spearman'):+.3f}" for c in
      ['s20', 'pgrid60', 'cusr_dn_k025', 'vrate', 'volat', 'dslo', 'crf', 'gap_60', 'rate600', 'eff10a']) + "\n")

# the realized table by z20 octile, with the production model's prediction (so a gap = something the model misses)
oc = np.quantile(z, np.linspace(0, 1, 9)); band = np.clip(np.searchsorted(oc, z, side='right') - 1, 0, 7)
print("| z20 octile | range | n | realized PF−1 | 2020–23 | 2024–26 | production predicted PF−1 (mean) | mean multiplier |\n|---|---|---|---|---|---|---|---|")
for k in range(8):
    m = band == k
    print(f"| {k + 1} | [{oc[k]:.2f}, {oc[k + 1]:.2f}) | {m.sum():,} | {pf1(R[m]):.3f} | {pf1(R[m & (YR <= 2023)]):.3f} | {pf1(R[m & (YR > 2023)]):.3f} "
          f"| {B.pf1_model.values[m].mean():.3f} | {B.multiplier.values[m].mean():.3f} |")
print()

gb = B.gap_band.values
common = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, *C['volat_clip_bp'])), B.rate600_fast.values,
          B.s_tier.values, B.dslo_flag.values, B.s20_z.values, B.cus_z.values, B.vrate_z.values]
Z = {'z20 linear': zs(z), 'z20 top octile flag (≥ −1.88)': (z >= -1.88) * 1.0, 'z20 hinge above −2.27': np.maximum(z + 2.27, 0) / 0.39}
def predict(Xm, f):
    bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
    p = 1 / (1 + np.exp(-(Xm @ bp))); return p * np.exp(Xm @ bw) / ((1 - p) * np.exp(Xm @ bl)) - 1, (bp, bw, bl)
def run(extra):
    Xm = np.column_stack(common + [Z[t] for t in extra]).astype(float); x = np.zeros(len(B)); bb = []
    for fm in (YR <= 2023, YR > 2023):
        app = ~fm; mm, b = predict(Xm, fm & ~young); mm = np.where(young, YOUNG_SCALE * mm, mm); bb.append(b)
        x[np.flatnonzero(app)] = (np.clip(mm, .125, 2.0) * R)[app]
    return x, bb
MODELS = {'production (V2-49)': []} | {f'production + {k}': [k] for k in Z}
X, BB = {}, {}
for n, t in MODELS.items(): X[n], BB[n] = run(t)
years = sorted(set(YR)); P = X['production (V2-49)']
ud, inv = np.unique(B.trade_date.values, return_inverse=True); rng = np.random.default_rng(20261111)
picks = np.stack([np.bincount(rng.integers(0, len(ud), len(ud)), minlength=len(ud))[inv] for _ in range(2000)]).astype(float)
def pfw(x): g = picks @ np.where(x > 0, x, 0); l = -(picks @ np.where(x < 0, x, 0)); return g / l - 1
pP = pfw(P)
print("**Out-of-sample sized PF−1** (each era predicted by the other's fit); Δ vs production, paired day bootstrap; the z20 "
      "term's p / W / L × per unit (fit on 2020–23; fit on 2024–26)\n")
print("| model | all | " + " | ".join(map(str, years)) + " | Δ vs production [90 %] · share > 0 | Δ net units·% | z20 term p / W / L |\n|---|---|" + "---|" * (len(years) + 3))
for n, x in X.items():
    d = pfw(x) - pP
    bt = '; '.join(' / '.join(f"{np.exp(b[-1]):.3f}" for b in bb) for bb in BB[n]) if MODELS[n] else ''
    print(f"| {n} | {pf1(x):.4f} | " + " | ".join(f"{pf1(x[YR == y]):.3f}" for y in years)
          + f" | {pf1(x) - pf1(P):+.4f} [{np.quantile(d, .05):+.4f}, {np.quantile(d, .95):+.4f}] · {100 * (d > 0).mean():.0f} % | {(x - P).sum():+,.0f} | {bt} |", flush=True)
