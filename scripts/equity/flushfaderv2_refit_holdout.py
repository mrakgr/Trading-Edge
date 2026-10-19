"""FlushFaderV2 — V2-16: the refit candidates on the year-block holdouts (user 2026-10-19).

Models: the V2 volatility BANDS vs ONE term ln(clip(volat, 40, 250 bp)) (V2-14), each on the near-low gate (0, 2 %]
(production) and (0, 3 %] (V2-15). The books are flushfaderv2_reference.py's (data/flushfaderv2/dslo3, tags g2 / g3:
the replayed membership, mc=1 refills included). V2's method: fit the three components on the barnum >= 22 trades of
one year block (2020-23 or 2024-26), apply to the other; weight = clip(PF−1 / the fit set's mean, 0.25, 4), × 0.8 for a
young trade (barnum 11-21, the production rule), normalised to mean 1 on the apply set (so net is per unit of capital).
Max DD = the deepest fall of the daily (trade-date) cumulative sized return, in % of one unit.
Run from research/:  python -u scripts/equity/flushfaderv2_refit_holdout.py > data/flushfaderv2/dslo3/refit_holdout.md; echo exit $?
"""
import numpy as np, pandas as pd
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
def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def maxdd(dates, r):
    d = pd.Series(r).groupby(dates).sum().sort_index().cumsum(); return (d.cummax() - d).max()

def run(tag):
    B = pd.read_parquet(f"data/flushfaderv2/dslo3/flushfaderv2_reference_book_{tag}.parquet")
    B = B.sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
    R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
    YR = B.trade_date.str[:4].astype(int).values; young = B.young.values
    gb, vb = B.gap_band.values, B.volat_band.values
    common = [np.ones(len(B)), gb == 1, gb == 2, B.rate600_fast.values, B.s_tier.values, B.dslo_flag.values, B.s20_z.values]
    X = {"bands": np.column_stack(common + [vb == k for k in range(1, 5)]).astype(float),
         "log": np.column_stack(common + [np.log(np.clip(B.volat.values, 40.0, 250.0))]).astype(float)}
    out = {}
    for model, Xm in X.items():
        for h, fit_m in [("fwd", YR <= 2023), ("mirror", YR > 2023)]:
            app = ~fit_m; f = fit_m & ~young
            bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
            p = 1 / (1 + np.exp(-(Xm @ bp))); lam = np.exp(Xm @ bl) / np.exp(Xm @ bw); m = p / ((1 - p) * lam) - 1
            m = np.where(young, YOUNG_SCALE * m, m)
            w = np.clip(m / m[f].mean(), 0.25, 4.0)[app]; w = w / w.mean()
            r = w * R[app]
            u = np.clip(m, 0.125, 2.0)[app]; ru = u * R[app]          # PRODUCTION units: 1 unit = 1 PF−1 (Sizer.fs), no rescaling
            out[(model, h)] = (int(app.sum()), pf1(r), r.sum(), maxdd(B.trade_date.values[app], r), pf1(R[app]), R[app].sum(),
                               pf1(ru), ru.sum(), maxdd(B.trade_date.values[app], ru), u.sum())
    return out

YRS_NOTE = "fwd = fit 2020–23 → apply 2024–26; mirror = fit 2024–26 → apply 2020–23"
print("# V2-16 — the refit candidates on the year-block holdouts\n")
print(YRS_NOTE + "; weights normalised to mean 1 on the apply set.\n")
O = {t: run(t) for t in ["g2", "g3"]}
print("| gate | volatility | n (fwd / mirror) | flat PF−1 (fwd / mirror) | sized PF−1 fwd | mirror | **mean** | sized net fwd | mirror | **sum** | max DD fwd | mirror |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
for tag, gn in [("g2", "(0, 2 %] — production"), ("g3", "(0, 3 %]")]:
    o = O[tag]
    for model in ["bands", "log"]:
        f, m = o[(model, "fwd")], o[(model, "mirror")]
        print(f"| {gn} | {'4 band dummies' if model == 'bands' else 'ln(clip(volat, 40, 250))'} | {f[0]:,} / {m[0]:,} | {f[4]:.3f} / {m[4]:.3f} | "
              f"{f[1]:.3f} | {m[1]:.3f} | **{(f[1] + m[1]) / 2:.3f}** | {f[2]:,.0f} | {m[2]:,.0f} | **{f[2] + m[2]:,.0f}** | {f[3]:,.0f} | {m[3]:,.0f} |")

print("\n## In PRODUCTION units: weight = clip(PF−1, 0.125, 2) (Sizer.fs: 1 unit = 1 PF−1 = 15 % of equity), not normalised\n")
print("Net and max DD in % of one unit; unit-trades = Σ weight (the capital used, before the book's caps).\n")
print("| gate | volatility | sized PF−1 fwd | mirror | **mean** | net fwd | mirror | **sum** | unit-trades | **net per unit-trade** | max DD fwd | mirror |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
for tag, gn in [("g2", "(0, 2 %] — production"), ("g3", "(0, 3 %]")]:
    for model in ["bands", "log"]:
        f, m = O[tag][(model, "fwd")], O[tag][(model, "mirror")]
        print(f"| {gn} | {'4 band dummies' if model == 'bands' else 'ln(clip(volat, 40, 250))'} | {f[6]:.3f} | {m[6]:.3f} | **{(f[6] + m[6]) / 2:.3f}** | "
              f"{f[7]:,.0f} | {m[7]:,.0f} | **{f[7] + m[7]:,.0f}** | {f[9] + m[9]:,.0f} | **{(f[7] + m[7]) / (f[9] + m[9]):.3f}** | {f[8]:,.0f} | {m[8]:,.0f} |")
