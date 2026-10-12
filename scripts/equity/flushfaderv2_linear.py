"""FlushFaderV2 — V2-3: extend the A3 sizing model with the non-gate features, one at a time (2026-10-12).

The broad book's gates stay gates (user 2026-10-12): lows300, eff10, v10r, z20, lows180, crf, coil5lo, the gap door,
rule140, cascade / WAIT are NOT candidates. The candidates are the rest of the §S49d efficiency list and the VOTE's
voices, each a plain feature:

    s20 speed accel rngf rflow d1m eff20 s5 K dlv ssf e9 | d20a dslo vexp vcrush ac1 esf dsu haltband

Model = A3's structure (flushfader_sizing_model.py, S49ae): three IRLS fits on the components —
    logit p = x·β_p (win rate) · log W = x·β_W (mean win) · log L = x·β_L (mean |loss|)
— with x = the A3 design (gap 0 / 1–3 / 4–39 dummies, volat 5 bands, rate600 fast flag, S-tier flag) + the selected
candidates. multiplier = fitted PF−1 / its mean on the fit set, clip [0.25, 4]; the sim normalises it to mean 1 on the
apply set (equal exposure), as in S49ad.

A continuous candidate enters LINEAR after winsorising at the fit set's 1 % / 99 % quantiles and standardising on the
fit set (NaN → the fit-set median). Two step features enter as FLAGS at the V2-2 octile edges (thresholds read off the
full sample — a mild in-sample choice, said so in the doc): K ≥ 77; dslo ≥ 4 % and dslo ∈ [0.5 %, 3 %) (V2-4's fine
bands: three regimes, the threshold plateau 3–5 %); haltband is the voice's own flag.

Forward selection: at each step every remaining candidate is added and scored by the MEAN of the sized PF−1 on the two
year-block holdouts (fit 2020–23 → apply 2024–26, and the mirror). The best one is kept. The curve is printed to the
end (no stopping rule), with A3's steepness controls (A3 ^ 1.25 / ^ 1.5) as the bar a gain has to clear.

Run from research/:
    python -u scripts/equity/flushfaderv2_linear.py > data/flushfaderv2/linear.log 2>&1; echo exit $?
"""
import argparse, os, time
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("--book", default="data/flushfaderv2/book_features.parquet")
ap.add_argument("--credit", type=float, default=0.001)
ap.add_argument("--steps", type=int, default=12)
ap.add_argument("--out", default="data/flushfaderv2/linear.md")
ap.add_argument("--tag", default="", help="suffix for this script's and its importers' outputs (e.g. v2 for the gated book)")
args = ap.parse_args()
if args.tag and args.out == "data/flushfaderv2/linear.md": args.out = f"data/flushfaderv2/linear_{args.tag}.md"
T0 = time.time()
def log(*a): print(f"[{time.time()-T0:6.1f}s]", *a, flush=True)

B = pd.read_parquet(args.book).sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
n = len(B)
R = (B.ret_exit.values + 2 * args.credit / B.raw_px.values) * 100          # % per trade, net of the credit
YR = B.trade_date.str[:4].astype(int).values; YEARS = sorted(set(YR))
early, late, all_m = YR <= 2023, YR > 2023, np.ones(n, bool)

# ---------------------------------------------------------------- the A3 design (the book's own band columns)
gb, vb = B.gap_band.values, B.volat_band.values
A3 = [np.ones(n), (gb == 1).astype(float), (gb == 2).astype(float)] + [(vb == k).astype(float) for k in range(1, 5)] \
     + [B.rate600_fast.values.astype(float), B.s_tier.values.astype(float)]
A3N = ["1", "gap 1-3", "gap 4-39", "volat 60-90", "volat 90-140", "volat 140-250", "volat 250+", "rate600 fast", "S tier"]

# ---------------------------------------------------------------- candidates: name -> (raw values, kind)
CAND = {
    "s20": ("s20", "lin"), "speed": ("speed", "lin"), "accel": ("accel", "lin"), "rngf": ("rngf", "lin"),
    "rflow": ("rflow", "lin"), "d1m": ("d1m", "lin"), "eff20": ("eff20a", "lin"), "s5": ("s5", "lin"),
    "K": ("k20", "lin"), "K>=77": ("k20", ("ge", 77)), "dlv": ("dlv", "lin"), "ssf": ("ssf", "lin"), "e9": ("e9", "lin"),
    "d20a": ("d20a", "lin"), "dslo": ("dslo", "lin"), "dslo>=4%": ("dslo", ("ge", 0.04)), "dslo in [0.5%,3%)": ("dslo", ("in", 0.005, 0.03)), "vexp": ("vexp", "lin"),
    "vcrush": ("vcrush", "lin"), "ac1": ("ac1", "lin"), "esf": ("esf", "lin"), "dsu": ("dsu", "lin"),
    "haltband": ("ssh", ("band", 1200, 4800)),
}
RAW = {k: B[c].values.astype(float) for k, (c, _) in CAND.items()}

def column(name, fit):
    x = RAW[name]; kind = CAND[name][1]
    if kind == "lin":
        f = x[fit & ~np.isnan(x)]
        lo, hi = np.quantile(f, [0.01, 0.99]); med = np.median(f)
        z = np.clip(np.where(np.isnan(x), med, x), lo, hi)
        mu, sd = z[fit].mean(), z[fit].std()
        return (z - mu) / sd
    if kind[0] == "ge": return np.where(np.isnan(x), 0.0, (x >= kind[1]).astype(float))
    if kind[0] == "in": return np.where(np.isnan(x), 0.0, ((x >= kind[1]) & (x < kind[2])).astype(float))
    if kind[0] == "band":
        ht = B.ht.values
        return ((ht >= 1) & (x >= kind[1]) & (x < kind[2])).astype(float)

def design(names, fit):
    return np.column_stack(A3 + [column(k, fit) for k in names])

# ---------------------------------------------------------------- IRLS (verbatim from flushfader_sizing_model.py)
def fit_logistic(X, y, it=50):
    b = np.zeros(X.shape[1])
    for _ in range(it):
        eta = X @ b; mu = 1 / (1 + np.exp(-eta)); W_ = mu * (1 - mu) + 1e-12
        z = eta + (y - mu) / (mu * (1 - mu) + 1e-12)
        b_new = np.linalg.solve((X * W_[:, None]).T @ X, (X * W_[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def fit_loglink(X, y, it=50):
    b = np.zeros(X.shape[1]); b[0] = np.log(y.mean())
    for _ in range(it):
        eta = X @ b; mu = np.exp(eta); z = eta + (y - mu) / mu
        b_new = np.linalg.solve((X * mu[:, None]).T @ X, (X * mu[:, None]).T @ z)
        if np.max(np.abs(b_new - b)) < 1e-9: b = b_new; break
        b = b_new
    return b
def fit_model(X, fit):
    win = R > 0
    return fit_logistic(X[fit], win[fit].astype(float)), fit_loglink(X[fit & win], R[fit & win]), fit_loglink(X[fit & ~win], -R[fit & ~win])
def predict(X, b):
    bp, bw, bl = b
    p = 1 / (1 + np.exp(-(X @ bp))); W = np.exp(X @ bw); L = np.exp(X @ bl)
    return p, W, L, p / ((1 - p) * (L / W)) - 1

# ---------------------------------------------------------------- scoring
def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def maxdd(x):
    c = np.cumsum(x); return float((np.maximum.accumulate(c) - c).max())
def weights(names, fit, power=1.0):
    X = design(names, fit); b = fit_model(X, fit); m = predict(X, b)[3]
    return np.clip(np.maximum(m / m[fit].mean(), 0.0) ** power, 0.25, 4.0)
def sized(w, app):
    w = w[app] / w[app].mean(); return w * R[app], w
def score(names, power=1.0):
    """(fwd sized PF−1, mirror sized PF−1, in-sample sized PF−1) + the sized series for the report"""
    wf = weights(names, early, power); wm = weights(names, late, power); wi = weights(names, all_m, power)
    sf, _ = sized(wf, late); sm, _ = sized(wm, early); si, _ = sized(wi, all_m)
    return pf1(sf), pf1(sm), pf1(si), (sf, sm, si, wi)
def f(x, d=3): return "inf" if np.isinf(x) else ("nan" if np.isnan(x) else f"{x:.{d}f}")

def main():
    out = [f"# V2-3 — A3 + candidates, forward selection on the year-block holdouts ({n:,} trades, credit ${args.credit}/sh/side)\n"]

    # ---- 1. correlations (Spearman, all years) among the candidates + the A3 continuous axes
    cont = [k for k, (_, kind) in CAND.items() if kind == "lin"]
    M = pd.DataFrame({k: RAW[k] for k in cont})
    M["volat"] = B.volat.values.astype(float); M["gap_60"] = B.gap_60.values.astype(float); M["rate600"] = B.rate600.values.astype(float)
    C = M.rank().corr()
    pairs = [(a, b, C.loc[a, b]) for i, a in enumerate(C.columns) for b in C.columns[i + 1:] if abs(C.loc[a, b]) >= 0.5]
    pairs.sort(key=lambda t: -abs(t[2]))
    out += ["## 1. Spearman |ρ| ≥ 0.5 between candidates (and the A3 axes volat / gap_60 / rate600)\n", "| a | b | ρ |", "|---|---|---|"]
    out += [f"| {a} | {b} | {r:+.2f} |" for a, b, r in pairs] + [""]
    out += ["Full matrix:\n", "| | " + " | ".join(C.columns) + " |", "|---|" + "---|" * len(C.columns)]
    out += [f"| {a} | " + " | ".join(f"{C.loc[a, b]:+.2f}" for b in C.columns) + " |" for a in C.columns] + [""]
    log(f"correlations done, {len(pairs)} pairs ≥ 0.5")

    # ---- 2. the baselines: flat, A3, A3 steeper
    flat = lambda app: pf1(R[app])
    a3f, a3m, a3i, a3s = score([])
    out += ["## 2. Baselines (sized PF−1; fwd = fit 2020–23 → 2024–26, mirror = fit 2024–26 → 2020–23)\n",
            "| model | fwd | mirror | mean | in-sample |", "|---|---|---|---|---|",
            f"| flat | {f(flat(late))} | {f(flat(early))} | {f((flat(late) + flat(early)) / 2)} | {f(flat(all_m))} |",
            f"| A3 | {f(a3f)} | {f(a3m)} | {f((a3f + a3m) / 2)} | {f(a3i)} |"]
    for pw in (1.25, 1.5):
        sf, sm, si, _ = score([], pw)
        out.append(f"| A3 ^ {pw} (steepness control) | {f(sf)} | {f(sm)} | {f((sf + sm) / 2)} | {f(si)} |")
    out.append("")

    # ---- 3. single additions (every candidate alone on top of A3)
    rows = []
    for k in CAND:
        sf, sm, si, _ = score([k]); rows.append((k, sf, sm, (sf + sm) / 2, si))
    rows.sort(key=lambda t: -t[3])
    out += ["## 3. Each candidate ALONE on top of A3 (Δ vs A3's mean holdout)\n", "| + candidate | fwd | mirror | mean | Δ mean | in-sample | both holdouts up? |", "|---|---|---|---|---|---|---|"]
    for k, sf, sm, mn, si in rows:
        out.append(f"| {k} | {f(sf)} | {f(sm)} | {f(mn)} | {mn - (a3f + a3m) / 2:+.3f} | {f(si)} | {'yes' if (sf > a3f and sm > a3m) else 'no'} |")
    out.append(""); log("single additions done")

    # ---- 4. forward selection
    sel, cur = [], (a3f + a3m) / 2
    out += ["## 4. Forward selection (best mean holdout sized PF−1 at each step; the curve runs to the end)\n",
            "| step | + candidate | fwd | mirror | mean | Δ mean | in-sample | sized net (in-sample) | sized maxDD | sized PF by year (in-sample) |",
            "|---|---|---|---|---|---|---|---|---|---|"]
    _, _, _, (_, _, si0, _) = score([])
    out.append(f"| 0 | (A3) | {f(a3f)} | {f(a3m)} | {f(cur)} | — | {f(a3i)} | {si0.sum():,.0f} | {maxdd(si0):,.0f} | "
               + " ".join(f(pf1(si0[YR == y]) + 1, 2) for y in YEARS) + " |")
    for step in range(1, args.steps + 1):
        best = None
        for k in CAND:
            if k in sel: continue
            sf, sm, si, ser = score(sel + [k]); mn = (sf + sm) / 2
            if best is None or mn > best[3]: best = (k, sf, sm, mn, si, ser)
        if best is None: break
        k, sf, sm, mn, si, (_, _, sis, _) = best; sel.append(k)
        out.append(f"| {step} | {k} | {f(sf)} | {f(sm)} | {f(mn)} | {mn - cur:+.3f} | {f(si)} | {sis.sum():,.0f} | {maxdd(sis):,.0f} | "
                   + " ".join(f(pf1(sis[YR == y]) + 1, 2) for y in YEARS) + " |")
        cur = mn; log(f"step {step}: + {k} -> mean holdout {mn:.3f}")
    out += ["", f"years: {' '.join(map(str, YEARS))}", ""]

    txt = "\n".join(out); open(args.out, "w").write(txt); print(txt); log(f"wrote {args.out}")

if __name__ == "__main__":
    main()
