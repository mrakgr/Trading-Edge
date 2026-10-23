"""FlushFaderV2 — V2-19: the DRAWDOWN FROM THE 1-YEAR HIGH as a sizing term, on the year-block holdouts (user 2026-10-22:
"it might not be bad to introduce a variable that factors the drop from 52w high into account"; V2-18 the bands).

The book is V2-18's (data/flushfaderv2/dd52_book.parquet = the production reference book + dd, hist_days):
dd = 1 − signal_vwap / max(high·n over D's episode's rows in [D − 365 d, D)) ÷ n(D), causal. A window shorter than a
year uses the history it has (the Oms would do the same); no prior row -> dd 0.
The method is V2-16's (flushfaderv2_refit_holdout.py): fit the three components (win odds, win size, loss size) on the
barnum >= 22 trades of one year block (2020-23 or 2024-26), apply to the other; young trades × 0.8. Scored in
PRODUCTION units: weight = clip(PF−1, 0.125, 2) (Sizer.fs), not normalised; max DD of the daily cumulative, % of a unit.
⭐ Control: the baseline (the production design) must reproduce V2-16's adopted row (0.721 / 0.908, net 17,000).
Run from research/:
  python -u scripts/equity/flushfaderv2_dd52_holdout.py > logs/flushfaderv2_dd52_holdout.log 2>&1; echo exit $?
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

B = pd.read_parquet("data/flushfaderv2/dd52_book.parquet")
B = B.sort_values(["trade_date", "symbol", "signal_sec"]).reset_index(drop=True)
R = (B.ret_exit.values + 2 * CREDIT / B.entry_px.values) * 100; WIN = R > 0
YR = B.trade_date.str[:4].astype(int).values; young = B.young.values; dates = B.trade_date.values
dd = np.nan_to_num(B.dd.values, nan=0.0)
lnratio = np.log(np.clip(1 - dd, 0.02, 1.0))            # ln(price / 1y high), clipped at 2 % of the high (dd 98 %)
gb = B.gap_band.values
base = [np.ones(len(B)), gb == 1, gb == 2, np.log(np.clip(B.volat.values, 40.0, 250.0)), B.rate600_fast.values,
        B.s_tier.values, B.dslo_flag.values, B.s20_z.values]
MODELS = {
    "production (control)": [],
    "dd >= 95 %": [dd >= 0.95],
    "dd >= 90 %": [dd >= 0.90],
    "dd 90-95 % + dd >= 95 %": [(dd >= 0.90) & (dd < 0.95), dd >= 0.95],
    "ln(px / 1y high), clip 2 %": [lnratio],
}

rows, betas = [], []
for name, extra in MODELS.items():
    Xm = np.column_stack(base + extra).astype(float)
    res = {}
    for h, fit_m in [("fwd", YR <= 2023), ("mirror", YR > 2023)]:
        app = ~fit_m; f = fit_m & ~young
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        p = 1 / (1 + np.exp(-(Xm @ bp))); lam = np.exp(Xm @ bl) / np.exp(Xm @ bw); m = p / ((1 - p) * lam) - 1
        m = np.where(young, YOUNG_SCALE * m, m)
        u = np.clip(m, 0.125, 2.0)[app]; ru = u * R[app]
        res[h] = (pf1(ru), ru.sum(), maxdd(dates[app], ru), u.sum())
    f, m = res["fwd"], res["mirror"]
    rows.append(f"| {name} | {f[0]:.3f} | {m[0]:.3f} | **{(f[0] + m[0]) / 2:.3f}** | {f[1]:,.0f} | {m[1]:,.0f} | **{f[1] + m[1]:,.0f}** | "
                f"{f[3] + m[3]:,.0f} | {(f[1] + m[1]) / (f[3] + m[3]):.3f} | {f[2]:,.0f} | {m[2]:,.0f} |")
    if extra:   # the full-period fit's coefficients of the new term(s): what the term does
        f = ~young
        bp = fit_logistic(Xm[f], WIN[f] * 1.0); bw = fit_loglink(Xm[f & WIN], R[f & WIN]); bl = fit_loglink(Xm[f & ~WIN], -R[f & ~WIN])
        for j in range(len(base), Xm.shape[1]):
            betas.append(f"| {name} | col {j - len(base) + 1} | {bp[j]:+.4f} ({np.exp(bp[j]):.3f}) | {bw[j]:+.4f} ({np.exp(bw[j]):.3f}) | "
                         f"{bl[j]:+.4f} ({np.exp(bl[j]):.3f}) |")

print("# V2-19 — the drawdown from the 1-year high as a sizing term, on the year-block holdouts\n")
print(f"book {len(B):,} (V2-18's); fwd = fit 2020–23 → apply 2024–26; mirror = fit 2024–26 → apply 2020–23. "
      "PRODUCTION units: weight = clip(PF−1, 0.125, 2); net and max DD in % of one unit.\n")
print(f"dd >= 90 %: {(dd >= 0.9).mean():.1%} of the book; >= 95 %: {(dd >= 0.95).mean():.1%}.\n")
print("| model | sized PF−1 fwd | mirror | **mean** | net fwd | mirror | **sum** | unit-trades | net per unit-trade | max DD fwd | mirror |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
print("\n".join(rows))
print("\n## The new terms, fit on the full period (barnum >= 22)\n")
print("| model | term | β_p (odds ×) | β_W (×) | β_L (×) |")
print("|---|---|---|---|---|")
print("\n".join(betas))
