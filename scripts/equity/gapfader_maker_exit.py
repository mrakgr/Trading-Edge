"""GapFader GF-21 (user 2026-10-17): the weak-premarket gap-down long with MAKER exits. The entry is the LOO in the opening
cross (commission 0.2c + Nasdaq's opening-cross fee 0.15c); the exit a resting sell limit at f of the gap until 10:00
(GF-14's targets: filled on a trade-through, at the target), else the maker exit from 10:00 — modelled as the first 1s
vwap at/after 10:00 (x_36000; neutral: neither the half spread earned nor the chase of a falling stock). Exit fee:
commission 0.2c − the NSDQ add rebate 0.15c. Round trip 0.40c (GF-14's taker model: 0.70c). By gap band, with the
old exit (50 % to 10:15, taker, 0.70c) for comparison.
Run from research/:  python -u scripts/equity/gapfader_maker_exit.py
"""
import numpy as np, pandas as pd
C = pd.read_parquet("data/equity/gapfader/census.parquet")
C = C[(C.open >= 1) & (C.dv20_prior >= 1e6) & ~C.early_close & C.close.notna() & (C.rvol_pm.fillna(0) < 0.02) & (C.gap <= -0.04)].copy()
C = C.merge(pd.read_parquet("data/equity/gapfader/targets.parquet"), on=["ticker", "date"])
C = C[C.x_36000.notna() & C.x_36900.notna()]
C["yr"] = C.date.str[:4]
ENTRY, EXIT_MAKE = 0.0035, 0.0005
def pf(v):
    g, l = v[v > 0].sum(), -v[v < 0].sum(); return np.inf if l == 0 else g / l
def ret(f, stop, fee):
    tgt = C.open + f * (C.pc_total - C.open)
    x = np.where(C[f"tf_{int(f * 100)}_{stop}"].notna(), tgt, C[f"x_{stop}"])
    return x / C.open - 1 - fee / C.open
R = {"old: 50 % to 10:15, taker 0.70c": ret(0.5, 36900, 0.007),
     "50 % to 10:00, maker 0.40c": ret(0.5, 36000, ENTRY + EXIT_MAKE),
     "75 % to 10:00, maker 0.40c": ret(0.75, 36000, ENTRY + EXIT_MAKE),
     "100 % to 10:00, maker 0.40c": ret(1.0, 36000, ENTRY + EXIT_MAKE)}
yrs = C.yr.nunique()
print(f"{len(C):,} trades 2016–2026 ({len(C) / yrs:,.0f} a year)\n")
print("| exit | avg % | PF raw | PF clip ±50 % | PF−1 | target hit | losing years |\n|---|---|---|---|---|---|---|")
for k, r in R.items():
    f = 0.5 if k.startswith(("old", "50")) else 0.75 if k.startswith("75") else 1.0
    st = 36900 if k.startswith("old") else 36000
    hit = C[f"tf_{int(f * 100)}_{st}"].notna().mean()
    ly = sum(pf(r[C.yr == y]) < 1 for y in C.yr.unique())
    print(f"| {k} | {r.mean():+.2%} | {pf(r):.2f} | {pf(np.clip(r, -.5, .5)):.2f} | {pf(r) - 1:.2f} | {hit:.0%} | {ly}/{yrs} |")
bands = [(-0.06, -0.04), (-0.08, -0.06), (-0.10, -0.08), (-0.15, -0.10), (-0.20, -0.15), (-0.30, -0.20), (-9, -0.30)]
print("\n### PF−1 by gap band\n")
print("| gap | trades / yr | " + " | ".join(R) + " |\n|---|---|" + "---|" * len(R))
for lo, hi in bands:
    m = (C.gap > lo) & (C.gap <= hi)
    print(f"| {-hi:.0%} … {-lo:.0%} | {m.sum() / yrs:,.0f} | " + " | ".join(f"{pf(r[m]) - 1:.2f} ({r[m].mean():+.2%})" for r in R.values()) + " |")
