"""S43cv — what does the ABSOLUTE GAP COUNT do inside the quiet cell?

⭐ USER (2026-08-16): "So intensity and persistence didn't make much of a difference,
but what about the gap count?"

⭐ USER'S THEORY, worth testing rather than just quoting: *"if the number of gaps is
already significant, the increase in sparsity doesn't make a strong signal. A slow stock
becoming slower maybe isn't that big of a deal. It's acceleration that would get the
trader's attention."*

That predicts ABSOLUTE sparsity carries the signal while CHANGE in sparsity does not —
which is exactly the §S43cu pattern (`gaps` 8/8 contexts and n-weighted +1.551,
`bar_over_*` 3/8 and −0.796). This script tests the other half of it: if absolute
sparsity is the real variable, it should still be graded INSIDE the quiet cell, and the
`>= 1000` floor should not be the best cut by accident.

## Design

⚠ The `gaps >= 1000` floor is REMOVED here so the full range is visible. Testing gaps
only above its own floor would be the §S43cf mistake again — measuring a feature inside
a cell already selected on it.

⚠ Bands are ABSOLUTE seconds, not quantiles: `gaps` is a raw count out of 3540 and a
live spec needs a fixed threshold.

⚠⚠ THE 2020 CONTROL IS BUILT IN, not bolted on. §S43ct found a 100.0-percentile result
collapse to 69.2 once 2020 was removed, because the filter was selecting the year rather
than the signal. 2020 is ~39% of the quiet cell. Every band therefore reports its 2020
share and its ex-2020 PF side by side.

Usage:  python scripts/equity/snoozer_quiet_gaps.py [--chg 0.08]
"""
import argparse

import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--shape", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--volat", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--chg", type=float, default=0.08)
ap.add_argument("--vmax", type=float, default=40.0)
ap.add_argument("--boot", type=int, default=2000)
ap.add_argument("--seed", type=int, default=20260816)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, -(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.chg60k59 AS chg, s.close_d, s.dv_lh,
       v.volat_open30 AS volat
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE s.chg60k59 > {args.chg} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
A = con.execute("SELECT * FROM S").fetchdf()
r = A.r.values
yrs = sorted(A.yr.unique())
rng = np.random.default_rng(args.seed)

# ⭐ the quiet cell WITHOUT the gaps floor
Q = A.volat.values * 1e4 < args.vmax
g = A.gaps.values.astype(float)
rQ = r[Q]
nQ = int(Q.sum())
y20 = A.yr.values == 2020


def pf(x):
    g_, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g_ / l


def line(m, lbl):
    x, k = r[m], int(m.sum())
    mx = m & ~y20
    xx = r[mx]
    neg = [y for y in yrs
           if (m & (A.yr.values == y)).sum() >= 5
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    d = {"band": lbl, "n": k, "PF": round(pf(x), 3),
         "mean%": round(x.mean()*100, 2), "med%": round(np.median(x)*100, 2),
         "win%": round((x > 0).mean()*100),
         "p5%": round(np.percentile(x, 5)*100, 1),
         "worst5%": round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
         "WORST%": round(x.min()*100), "loss%": round((x < 0).mean()*100),
         "yrs<1": len(neg),
         "2020%": f"{100*(m & y20).sum()/k:.0f}%",
         "n ex20": int(mx.sum()),
         "PF ex20": round(pf(xx), 3) if mx.sum() >= 15 else np.nan,
         "WORST ex20": round(xx.min()*100) if mx.sum() >= 15 else np.nan}
    return d


print(f"QUIET CELL WITHOUT THE GAPS FLOOR:  chg > {args.chg*100:g}%  x  "
      f"volat_open30 < {args.vmax:g}bp   ->  n = {nQ:,}   PF {pf(rQ):.3f}")
print(f"  (for reference the FULL chg>{args.chg*100:g}% population is n = {len(A):,}, "
      f"PF {pf(r):.3f}; 2020 is {100*(Q & y20).sum()/nQ:.0f}% of the quiet cell)")

print(f"\n{'='*215}\n⭐ §1 ABSOLUTE GAP BANDS inside the quiet cell — the floor is OFF, "
      f"so the full range is visible\n{'='*215}")
EDG = [0, 500, 1000, 1500, 2000, 2500, 3000, 3541]
rows = []
for lo, hi in zip(EDG[:-1], EDG[1:]):
    m = Q & (g >= lo) & (g < hi)
    if m.sum() < 15:
        continue
    rows.append(line(m, f"[{lo}, {hi}) s"))
print(pd.DataFrame(rows).fillna("").to_string(index=False))
print("  gaps = seconds of (15:00,15:59] that did NOT trade, out of 3540.")
print("  ⚠ 'PF ex20' is the same band with 2020 removed — the number that has to hold.")

print(f"\n{'='*215}\n⭐⭐ §2 CUMULATIVE FLOOR SWEEP — is `>= 1000` the right cut, or an "
      f"inherited one?\n{'='*215}")
rows = []
for T in (0, 500, 750, 1000, 1250, 1500, 1750, 2000, 2500):
    m = Q & (g >= T)
    if m.sum() < 25:
        continue
    d = line(m, f"gaps >= {T}")
    k = int(m.sum())
    if k < nQ:
        null = np.array([pf(rQ[rng.choice(nQ, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        d["pctile"] = round(float((null < pf(r[m])).mean()*100), 1)
    rows.append(d)
print(pd.DataFrame(rows).fillna("").to_string(index=False))
print("  pctile vs random same-n subsets of the quiet cell (floor OFF).")

print(f"\n{'='*215}\n§3 THE USER'S THEORY — absolute sparsity vs CHANGE in sparsity, "
      f"head to head inside the quiet cell\n{'='*215}")
con.execute(f"""CREATE OR REPLACE TEMP TABLE T AS
SELECT s.ticker, s.date, s.bar_over_open30, s.dv_over_open30
FROM read_parquet('{args.shape}') s
WHERE s.chg60k59 > {args.chg} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
B = con.execute("SELECT * FROM T").fetchdf()
A2 = A.merge(B, on=["ticker", "date"], how="left")
rows = []
for col, op, lab in ((None, None, "the quiet cell (no filter)"),
                     ("gaps", ">=", "ABSOLUTE sparsity  gaps >= cell median"),
                     ("bar_over_open30", "<=", "CHANGE in sparsity  bar_over_open30 <= median"),
                     ("dv_over_open30", "<=", "intensity  dv_over_open30 <= median")):
    if col is None:
        m = Q
    else:
        v = (g if col == "gaps" else A2[col].values.astype(float))
        t = np.nanquantile(v[Q], .5)
        m = Q & ((v >= t) if op == ">=" else (v <= t)) & ~np.isnan(v)
    d = line(m, lab)
    k = int(m.sum())
    if 20 <= k < nQ:
        null = np.array([pf(rQ[rng.choice(nQ, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        d["pctile"] = round(float((null < pf(r[m])).mean()*100), 1)
    rows.append(d)
print(pd.DataFrame(rows).fillna("").to_string(index=False))
print("  ⭐ the theory predicts the ABSOLUTE row beats the CHANGE row.")
