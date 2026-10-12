"""GapFader — GF-4 (user 2026-10-12): gaps on LARGE premarket volume — is there MOMENTUM, and does the market matter?
The FOLLOW trade = the gap's direction from the official open: buy a gap-up / short a gap-down, out at the close
(follow_oc = −fade_oc) or the next open (follow_on = −fade_on). By rvol_pm band × gap size, then by SPY's gap relative
to the stock's. Equal weight, no costs. Universe as GF-1's headline.
Run from research/:  python -u scripts/equity/gapfader_momentum.py > data/equity/gapfader/momentum.md; echo exit $?
"""
import numpy as np, pandas as pd, duckdb
S = duckdb.sql("""WITH d AS (SELECT date, open, close, n, cum_div, lag(close) OVER w pcr, lag(n) OVER w np_, lag(cum_div) OVER w cdp
   FROM 'data/v2/daily_adjusted.parquet' WHERE ticker='SPY' WINDOW w AS (ORDER BY date))
   SELECT date::VARCHAR date, open/(pcr*np_/n-(cum_div-cdp)/n)-1 spy_gap FROM d""").df()
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna()].merge(S, on="date")
C["follow_oc"] = -C.fade_oc; C["follow_on"] = -C.fade_on
C["rel"] = C.spy_gap * np.sign(C.gap); C["yr"] = C.date.str[:4]; C["ag"] = C.gap.abs(); C["rv"] = C.rvol_pm.fillna(0)
def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum(); return np.inf if l == 0 else g / l
def fm(x, d=2): return "inf" if np.isinf(x) else ("—" if np.isnan(x) else f"{x:.{d}f}")
RV = [(0, 0.02, "< 0.02"), (0.02, 0.1, "0.02–0.1"), (0.1, 0.25, "0.1–0.25"), (0.25, 0.5, "0.25–0.5"), (0.5, 1, "0.5–1"), (1, 2, "1–2"), (2, 1e9, "≥ 2")]
GP = [(0.02, 0.06, "2–6 %"), (0.06, 0.15, "6–15 %"), (0.15, 0.30, "15–30 %"), (0.30, 9, "≥ 30 %")]
print("# GF-4 — gaps on LARGE premarket volume: the FOLLOW trade (momentum)\n")
for up, name in ((True, "gap UP (follow = BUY the open)"), (False, "gap DOWN (follow = SHORT the open)")):
    D = C[(C.gap > 0) == up]
    print(f"## {name} — follow PF open→close / open→next open (n)\n")
    print("| rvol_pm \\ gap | " + " | ".join(g for _, _, g in GP) + " |\n|---|" + "---|" * len(GP))
    for lo, hi, rl in RV:
        cells = []
        for glo, ghi, _ in GP:
            T = D[(D.rv >= lo) & (D.rv < hi) & (D.ag >= glo) & (D.ag < ghi)]
            cells.append("·" if len(T) < 30 else f"{fm(pf(T.follow_oc.values))} / {fm(pf(T.follow_on.values))} ({len(T):,})")
        print(f"| {rl} | " + " | ".join(cells) + " |")
    print(f"\n### {name}, rvol_pm ≥ 0.5 — by SPY's gap relative to the stock (follow open→close)\n")
    print("| SPY vs the stock | gap | n | days | mean % | win % | PF | PF 2016–20 | PF 2021–26 | open→next open PF |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    H = D[D.rv >= 0.5]
    for lo, hi, lab in ((-1, -0.005, "against ≥ 0.5 %"), (-0.005, 0.005, "quiet"), (0.005, 1, "with ≥ 0.5 %")):
        for glo, ghi, gl in ((0.02, 0.15, "2–15 %"), (0.15, 9, "≥ 15 %")):
            T = H[(H.rel >= lo) & (H.rel < hi) & (H.ag >= glo) & (H.ag < ghi)]
            if len(T) < 20: continue
            r = T.follow_oc.values
            print(f"| {lab} | {gl} | {len(T):,} | {T.date.nunique():,} | {r.mean()*100:+.2f} | {(r > 0).mean()*100:.0f} | {fm(pf(r))} | "
                  f"{fm(pf(T[T.yr <= '2020'].follow_oc.values))} | {fm(pf(T[T.yr >= '2021'].follow_oc.values))} | {fm(pf(T.follow_on.values))} |")
    print()
