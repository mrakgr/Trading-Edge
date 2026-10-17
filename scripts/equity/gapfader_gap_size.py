"""GapFader GF-20 (user 2026-10-17: "size based on the size of the gap, larger gaps are more profitable"). The weak-premarket
gap-down long (GF-14: bought at the open, target 50 % of the gap or out at 10:15, net 0.7c a share) by the gap band.
Run from research/:  python -u scripts/equity/gapfader_gap_size.py
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/targets.parquet")[["ticker", "date", "tf_50_36900", "x_36900"]], on=["ticker", "date"])
C = C[C.x_36900.notna()]
x = np.where(C.tf_50_36900.notna(), C.open + 0.5 * (C.pc_total - C.open), C.x_36900)
C["r"] = x / C.open - 1 - 0.007 / C.open
C["yr"] = C.date.str[:4]
def pf(v):
    g, l = v[v > 0].sum(), -v[v < 0].sum(); return np.inf if l == 0 else g / l
yrs = C.yr.nunique()
bands = [(-0.06, -0.04), (-0.08, -0.06), (-0.10, -0.08), (-0.15, -0.10), (-0.20, -0.15), (-0.30, -0.20), (-9, -0.30)]
print(f"{len(C):,} trades, {yrs} years ({C.yr.min()}–{C.yr.max()})\n")
print("| gap | trades / yr | avg % | median % | PF raw | PF clip ±50 % | PF−1 | losing years |")
print("|---|---|---|---|---|---|---|---|")
for lo, hi in bands:
    m = (C.gap > lo) & (C.gap <= hi); v = C.r[m]
    ly = sum(pf(C.r[m & (C.yr == y)]) < 1 for y in sorted(C.yr.unique()) if (m & (C.yr == y)).sum() >= 20)
    ny = sum((m & (C.yr == y)).sum() >= 20 for y in C.yr.unique())
    p = pf(v)
    print(f"| {-hi:.0%} … {-lo:.0%} | {m.sum() / yrs:,.0f} | {v.mean():+.2%} | {v.median():+.2%} | {p:.2f} | {pf(v.clip(-.5, .5)):.2f} | {p - 1:.2f} | {ly}/{ny} |")
p = pf(C.r); print(f"| all ≥ 4 % | {len(C) / yrs:,.0f} | {C.r.mean():+.2%} | {C.r.median():+.2%} | {p:.2f} | {pf(C.r.clip(-.5, .5)):.2f} | {p - 1:.2f} | |")
