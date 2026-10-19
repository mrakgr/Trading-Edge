"""GapFader GF-31 (user 2026-10-19): a CONTINUOUS PF−1 model for the gap-up INTRADAY SWING (GF-10/11/29/30's trade: gap-up
>= 4 %, rvol_pm >= 0.2, held the open at 10:00, bought then, out at the close; net 0.7c / share), FlushFaderV2's machinery:
logit p, log W, log L by IRLS, PF−1 = p / ((1−p)·L/W) − 1. Every term standardised on the FIT set (winsorised 1/99 %):
  lvol  ln volat_clk (GF-11)                     ltr  ln(1 + trades 09:30–10:00)
  lvp   ln volume persistence (09:45–10:00 / 09:30–09:45)     lap  ln activity persistence (active 1s bars, same windows)
  slope the OLS slope of ln vwap over the 09:30–10:00 1s bars, bp/min (gapfader_slope30.py)
  ret30 open → the last vwap before 10:00 (≥ 0: the trade holds the open)
  lgap  ln gap      lrvol ln rvol_pm      ldv  ln dv20_prior
Nested models, scored on the two HALF holdouts (fit 2016–20 → apply 2021–26, and the mirror):
  sized PF−1   weights = clip(PF−1 / its fit-set mean, 0.25, 4), mean 1 on the apply set (V2's method)
  top third / top half   net PF of the trades with the highest predicted PF−1 (the cut = the FIT set's quantile)
Run from research/:  python -u scripts/equity/gapfader_swing_model.py > data/equity/gapfader/swing_model.md; echo exit $?
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm >= 0.2) & (C.gap >= 0.04)].copy()
for f, cols in [("tape_persistence", None), ("volat30", ["ticker", "date", "volat_clk"]), ("slope30", ["ticker", "date", "slope30", "n_slope"])]:
    X = pd.read_parquet(f"data/equity/gapfader/{f}.parquet"); C = C.merge(X[cols] if cols else X, on=["ticker", "date"], how="left")
for c in ["tc_a", "tc_b", "vol_a", "vol_b", "act_a", "act_b"]: C[c] = C[c].fillna(0)   # a window with no trade: SQL sum() is NULL, i.e. 0
C = C[(C.px_30 >= C.open) & np.isfinite(C.entry_30) & C.volat_clk.notna() & C.slope30.notna() & (C.act_a > 0) & (C.vol_a > 0)].copy()
C = C.sort_values(["date", "ticker"]).reset_index(drop=True)
R = ((C.close / C.entry_30 - 1) - 0.007 / C.entry_30).values * 100          # net %, 0.7c / share round trip
WIN = R > 0; YR = C.date.str[:4].astype(int).values
RAW = {"lvol": np.log(C.volat_clk.clip(lower=1)), "ltr": np.log1p(C.tc_a + C.tc_b),
       "lvp": np.log((C.vol_b / C.vol_a).clip(lower=1e-3)), "lap": np.log((C.act_b / C.act_a).clip(lower=1e-3)),
       "slope": C.slope30, "ret30": C.px_30 / C.open - 1,
       "lgap": np.log(C.gap), "lrvol": np.log(C.rvol_pm), "ldv": np.log(C.dv20_prior)}
RAW = {k: np.asarray(v, float) for k, v in RAW.items()}
assert all(np.isfinite(v).all() for v in RAW.values()), {k: int((~np.isfinite(v)).sum()) for k, v in RAW.items()}
def design(terms, fit):
    cols = [np.ones(len(C))]
    for t in terms:
        x = RAW[t]; lo, hi = np.quantile(x[fit], [0.01, 0.99]); x = np.clip(x, lo, hi)
        cols.append((x - x[fit].mean()) / x[fit].std())
    return np.column_stack(cols)
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
def pf1_model(terms, fit):
    X = design(terms, fit)
    bp = fit_logistic(X[fit], WIN[fit] * 1.0); bw = fit_loglink(X[fit & WIN], R[fit & WIN]); bl = fit_loglink(X[fit & ~WIN], -R[fit & ~WIN])
    p = 1 / (1 + np.exp(-(X @ bp))); return p / ((1 - p) * np.exp(X @ bl) / np.exp(X @ bw)) - 1, (bp, bw, bl)
def pf(r):
    r = np.asarray(r); g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l
MODELS = [("none (flat)", []), ("volatility", ["lvol"]), ("+ trades", ["lvol", "ltr"]),
          ("+ volume / activity persistence", ["lvol", "ltr", "lvp", "lap"]),
          ("+ slope, ret30", ["lvol", "ltr", "lvp", "lap", "slope", "ret30"]),
          ("+ gap, rvol_pm, dv20 (all)", ["lvol", "ltr", "lvp", "lap", "slope", "ret30", "lgap", "lrvol", "ldv"]),
          ("slope alone", ["slope"]), ("slope + ret30", ["slope", "ret30"]),
          ("persistence alone", ["lvp", "lap"]), ("all but the slope terms", ["lvol", "ltr", "lvp", "lap", "lgap", "lrvol", "ldv"])]
HOLD = [("fwd", YR <= 2020, YR > 2020), ("mirror", YR > 2020, YR <= 2020)]
print(f"# GF-31 — the gap-up intraday swing: a continuous PF−1 model ({len(C):,} trades; net 0.7c)\n")
print(f"Flat net PF: all {pf(R):.3f}; 2016–20 {pf(R[YR <= 2020]):.3f} ({(YR <= 2020).sum():,}); 2021–26 {pf(R[YR > 2020]):.3f} ({(YR > 2020).sum():,})\n")
print("| model | sized PF−1 fwd / mirror (**mean**) | top third: net PF fwd / mirror (n) | top half: net PF fwd / mirror (n) | bottom third: net PF fwd / mirror |")
print("|---|---|---|---|---|")
for nm, terms in MODELS:
    cells = {}
    for h, fit, app in HOLD:
        m, _ = pf1_model(terms, fit)
        w = np.clip(m / m[fit].mean(), 0.25, 4.0)[app]; w = w / w.mean()
        q3, q2, q1 = np.quantile(m[fit], [2/3, 1/2, 1/3])
        a = m[app]; r = R[app]
        cells[h] = (pf(w * r) - 1, pf(r[a >= q3]), (a >= q3).sum(), pf(r[a >= q2]), (a >= q2).sum(), pf(r[a < q1]))
    f, mi = cells["fwd"], cells["mirror"]
    print(f"| {nm} | {f[0]:.3f} / {mi[0]:.3f} (**{(f[0] + mi[0]) / 2:.3f}**) | {f[1]:.2f} / {mi[1]:.2f} ({f[2]:,} / {mi[2]:,}) | "
          f"{f[3]:.2f} / {mi[3]:.2f} ({f[4]:,} / {mi[4]:,}) | {f[5]:.2f} / {mi[5]:.2f} |")
terms = MODELS[5][1]; allm = np.ones(len(C), bool); _, (bp, bw, bl) = pf1_model(terms, allm)
print("\n## The full model fitted on all years (per SD of each term)\n\n| term | odds × (p) | W × | L × | λ = L/W × |\n|---|---|---|---|---|")
for j, t in enumerate(["intercept"] + terms):
    print(f"| {t} | {np.exp(bp[j]):.3f} | {np.exp(bw[j]):.3f} | {np.exp(bl[j]):.3f} | {np.exp(bl[j] - bw[j]):.3f} |")
print("\nρ (Spearman) of the slope with: " + ", ".join(f"{t} {pd.Series(RAW['slope']).corr(pd.Series(RAW[t]), method='spearman'):.2f}" for t in ["lvol", "ltr", "ret30", "lgap", "lvp", "lap"]))
