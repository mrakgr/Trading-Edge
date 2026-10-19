"""FlushFaderV2 — V2-15: raise the near-low gate from (0, 2 %] to (0, 3 %]? (user 2026-10-19)

The books are REPLAYED by flushfaderv2_reference.py --near-low-hi H --coef data/flushfaderv2/v2_coefficients.json
(out-dir data/flushfaderv2/dslo3, tags g2 = production / g25 / g3): the gate changes the engine-level set, mc=1 refills
the freed slots, the production coefficients size every book. Flat and sized, net of the $0.001/sh/side credit.
Run from research/:  python -u scripts/equity/flushfaderv2_dslo3.py > data/flushfaderv2/dslo3/dslo3.md; echo exit $?
"""
import numpy as np, pandas as pd
CREDIT = 0.001
def load(tag):
    B = pd.read_parquet(f"data/flushfaderv2/dslo3/flushfaderv2_reference_book_{tag}.parquet")
    B["R"] = (B.ret_exit + 2 * CREDIT / B.entry_px) * 100; B["S"] = B.R * B.multiplier; B["yr"] = B.trade_date.str[:4]
    return B
def pf1(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum(); return np.inf if l == 0 else g / l - 1
def maxdd(B, col):
    d = B.groupby("trade_date")[col].sum().sort_index().cumsum(); return (d.cummax() - d).max()
YRS = [str(y) for y in range(2020, 2027)]
BK = {t: load(t) for t in ["g2", "g25", "g3"]}
b = BK["g2"]

print("# V2-15 — the near-low gate at 2 % vs 3 %\n")
print("## The (2 %, 3 %] trades inside the production book (gate (0, 2 %]), and their neighbours\n")
print("| dslo | n | flat PF−1 | avg % | win % | mean mult | sized PF−1 | sized net | " + " | ".join(YRS) + " |")
print("|---|---|---|---|---|---|---|---|" + "---|" * len(YRS))
bands = [("= 0", b.dslo == 0), ("(2 %, 2.5 %]", (b.dslo > .02) & (b.dslo <= .025)), ("(2.5 %, 3 %]", (b.dslo > .025) & (b.dslo <= .03)),
         ("**(2 %, 3 %]**", (b.dslo > .02) & (b.dslo <= .03)), ("(3 %, 4 %)", (b.dslo > .03) & (b.dslo < .04)), ("≥ 4 % (flagged)", b.dslo >= .04)]
for nm, m in bands:
    x = b[m]
    print(f"| {nm} | {len(x):,} | {pf1(x.R):.3f} | {x.R.mean():.3f} | {100*(x.R>0).mean():.1f} | {x.multiplier.mean():.2f} | {pf1(x.S):.3f} | {x.S.sum():,.0f} | "
          + " | ".join(f"{pf1(x[x.yr==y].R):.2f}" for y in YRS) + " |")
print("\n(year columns: flat PF−1)\n")

print("## The gate variants, replayed\n")
print("| gate drops dslo ∈ | n | flat PF−1 | flat net | sized PF−1 | sized net | Δ n | Δ flat net | Δ sized net | sized max DD | " + " | ".join(YRS) + " |")
print("|---|---|---|---|---|---|---|---|---|---|" + "---|" * len(YRS))
for t, nm in [("g2", "**(0, 2 %] — production**"), ("g25", "(0, 2.5 %]"), ("g3", "(0, 3 %]")]:
    x = BK[t]
    print(f"| {nm} | {len(x):,} | {pf1(x.R):.3f} | {x.R.sum():,.0f} | {pf1(x.S):.3f} | {x.S.sum():,.0f} | {len(x)-len(b):+,} | {x.R.sum()-b.R.sum():+,.0f} | {x.S.sum()-b.S.sum():+,.0f} | "
          f"{maxdd(x, 'S'):,.0f} | " + " | ".join(f"{pf1(x[x.yr==y].S):.3f}" for y in YRS) + " |")
print("\n(year columns: sized PF−1)\n")
print("## Δ sized net by year, (0, 3 %] − production\n")
print("| | " + " | ".join(YRS) + " |"); print("|---|" + "---|" * len(YRS))
g3 = BK["g3"]
print("| Δ sized net | " + " | ".join(f"{g3[g3.yr==y].S.sum() - b[b.yr==y].S.sum():+,.0f}" for y in YRS) + " |")
print("| Δ flat net | " + " | ".join(f"{g3[g3.yr==y].R.sum() - b[b.yr==y].R.sum():+,.0f}" for y in YRS) + " |")
# the refill: trades in g3 not in g2
k = ["symbol", "trade_date", "signal_sec"]
new = g3.merge(b[k], on=k, how="left", indicator=True); new = new[new._merge == "left_only"]
gone = b.merge(g3[k], on=k, how="left", indicator=True); gone = gone[gone._merge == "left_only"]
print(f"\nRefill: (0, 3 %] removes {len(gone):,} production trades (sized net {gone.S.sum():+,.0f}, flat PF−1 {pf1(gone.R):.3f}) "
      f"and adds {len(new):,} later trips of the same ticker-days (sized net {new.S.sum():+,.0f}, flat PF−1 {pf1(new.R):.3f}).")
