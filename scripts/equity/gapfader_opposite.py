"""GapFader — GF-3 (user 2026-10-12): the stock gaps AGAINST the market. Weak-premarket gaps (rvol_pm < 0.02) by SPY's
gap relative to the stock's direction: opposite / quiet / same, in bands. Open → close fade, equal weight, no costs.
Run from research/:  python -u scripts/equity/gapfader_opposite.py > data/equity/gapfader/opposite.md; echo exit $?
"""
import numpy as np, pandas as pd, duckdb
S = duckdb.sql("""WITH d AS (SELECT date, open, close, n, cum_div, lag(close) OVER w pcr, lag(n) OVER w np_, lag(cum_div) OVER w cdp
   FROM 'data/v2/daily_adjusted.parquet' WHERE ticker='SPY' WINDOW w AS (ORDER BY date))
   SELECT date::VARCHAR date, open/(pcr*np_/n-(cum_div-cdp)/n)-1 spy_gap, close/open-1 spy_oc FROM d""").df()
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02)].merge(S, on="date")
C["rel"] = C.spy_gap * np.sign(C.gap)          # SPY's gap in the stock's direction: < 0 = against the stock
C["yr"] = C.date.str[:4]
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
BANDS = [(-1, -0.01, "SPY against ≥ 1 %"), (-0.01, -0.005, "SPY against 0.5–1 %"), (-0.005, 0.005, "quiet (< 0.5 %)"),
         (0.005, 0.01, "SPY with 0.5–1 %"), (0.01, 1, "SPY with ≥ 1 %")]
print("# GF-3 — weak-premarket gaps (rvol_pm < 0.02) by SPY's gap relative to the stock's\n")
for up, name in ((True, "stock gaps UP (fade = short)"), (False, "stock gaps DOWN (fade = long)")):
    print(f"## {name}\n")
    print("| SPY's gap vs the stock | gap | n | days | mean % | win % | PF | SPY open→close in the fade's favour, mean % | PF 2016–20 | PF 2021–26 |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    D = C[(C.gap > 0) == up]
    for lo, hi, lab in BANDS:
        for glo, ghi, gl in ((0.04, 0.15, "4–15 %"), (0.15, 9, "≥ 15 %")):
            T = D[(D.rel >= lo) & (D.rel < hi) & (D.gap.abs() >= glo) & (D.gap.abs() < ghi)]
            if len(T) == 0: continue
            r = T.fade_oc.values; spy_fav = (-np.sign(T.gap) * T.spy_oc).mean() * 100
            print(f"| {lab} | {gl} | {len(T):,} | {T.date.nunique():,} | {r.mean()*100:+.2f} | {(r > 0).mean()*100:.0f} | {pf(r):.2f} | {spy_fav:+.2f} | "
                  f"{pf(T[T.yr <= '2020'].fade_oc.values):.2f} | {pf(T[T.yr >= '2021'].fade_oc.values):.2f} |")
    print()
