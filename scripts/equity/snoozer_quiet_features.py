"""S43ct — do INTENSITY and PERSISTENCE add inside the QUIET cell?

⭐ USER (2026-08-16): "Let's focus on this cell [gaps>=1000 x chg>+6% x volat_open30 <
40bp, n=542, PF 3.248, worst -49%]. Do intensity and persistence make a difference
inside it?"

## Why the directions must be re-derived, not imported

The quiet cell is a DIFFERENT POPULATION from the one every short-side direction was
fitted on: median price $17.16 vs $3.78, closing-hour dollars $36.5M vs $6.1M
(§S43cs §3). Those signs were established on a book dominated by sub-$5 thin names.
⚠ Four features have already flipped sign between the two Snoozer systems, and §S43cs
showed volatility itself is BIMODAL rather than directional — so **both directions are
tested for every feature here** and the table reports whichever wins, with the loser
shown so a coin-flip is visible as one.

⚠ Percentiles are against random same-n subsets OF THE QUIET CELL, so they answer
"does this beat cutting the cell at random", not "does this beat the population".

⚠ n = 542 splits to ~271 a side; the noise floor is printed so a 0.3 PF move can be
read against it rather than in the abstract.

Usage:  python scripts/equity/snoozer_quiet_features.py [--chg 0.06]
"""
import argparse
import itertools

import duckdb
import numpy as np
import pandas as pd

pd.set_option("display.width", 260)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--shape", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--volat", default="data/equity/flushfader/snoozer_volat.parquet")
ap.add_argument("--chg", type=float, default=0.06)
ap.add_argument("--vmax", type=float, default=40.0, help="volat_open30 ceiling, bp")
ap.add_argument("--boot", type=int, default=2000)
ap.add_argument("--seed", type=int, default=20260816)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, -(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.chg60k59 AS chg, s.close_d, s.dv_lh,
       s.dv_over_open15, s.dv_over_open30, s.dv_over_open60, s.dv_over_rest,
       s.bar_over_open15, s.bar_over_open30, s.bar_over_open60,
       v.volat_open30, v.volat_lh
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE s.chg60k59 > {args.chg} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
A = con.execute("SELECT * FROM S").fetchdf()
r = A.r.values
yrs = sorted(A.yr.unique())
rng = np.random.default_rng(args.seed)

Q = A.gaps.values >= 1000
Q &= A.volat_open30.values * 1e4 < args.vmax
rQ = r[Q]
nQ = int(Q.sum())


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


def tpf(x):
    return pf(x[x >= np.quantile(x, 0.05)]) if len(x) >= 20 else float("nan")


def st(m, lbl, boot=True):
    x, k = r[m], int(m.sum())
    neg = [y for y in yrs
           if (m & (A.yr.values == y)).sum() >= 8
           and pf(r[m & (A.yr.values == y)]) < 1.0]
    d = {"rule": lbl, "n": k, "PF": round(pf(x), 3),
         "PFtrim": round(tpf(x), 3) if k >= 20 else np.nan,
         "mean%": round(x.mean()*100, 2), "med%": round(np.median(x)*100, 2),
         "win%": round((x > 0).mean()*100),
         "p5%": round(np.percentile(x, 5)*100, 1),
         "worst5%": round(np.sort(x)[:max(1, k//20)].mean()*100, 1),
         "WORST%": round(x.min()*100), "loss%": round((x < 0).mean()*100),
         "yrs<1": len(neg)}
    if boot and 20 <= k < nQ:
        null = np.array([pf(rQ[rng.choice(nQ, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        d["pctile"] = round(float((null < pf(x)).mean()*100), 1)
        d["floor"] = round(float(np.quantile(null, .95)/np.median(null)), 3)
    return d


print(f"QUIET CELL: gaps>=1000  x  chg>{args.chg*100:g}%  x  volat_open30<{args.vmax:g}bp")
print(pd.DataFrame([st(Q, "the cell", boot=False)]).to_string(index=False))
print(f"  median price ${np.nanmedian(A.close_d.values[Q]):.2f} · "
      f"median last-hour dollars ${np.nanmedian(A.dv_lh.values[Q])/1e6:.1f}M")

FE = [("dv_over_open15", "intensity 15m"), ("dv_over_open30", "intensity 30m"),
      ("dv_over_open60", "intensity 60m"), ("dv_over_rest", "intensity rest-of-day"),
      ("bar_over_open15", "persistence 15m"), ("bar_over_open30", "persistence 30m"),
      ("bar_over_open60", "persistence 60m"),
      ("gaps", "absolute gaps (already >=1000)"),
      ("volat_open30", "volatility (within the band)"),
      ("volat_lh", "last-hour volatility")]

print(f"\n{'='*215}\n⭐ §1 BOTH DIRECTIONS, each at the CELL median — the incumbent "
      f"short-side sign is marked\n{'='*215}")
INCUMBENT = {"dv_over_open15": "<=", "dv_over_open30": "<=", "dv_over_open60": "<=",
             "dv_over_rest": "<=", "bar_over_open15": "<=", "bar_over_open30": "<=",
             "bar_over_open60": "<=", "gaps": ">=", "volat_open30": ">=",
             "volat_lh": ">="}
rows = []
for col, lab in FE:
    v = A[col].values.astype(float)
    t = np.nanquantile(v[Q], .5)
    for op in ("<=", ">="):
        m = Q & ((v <= t) if op == "<=" else (v > t)) & ~np.isnan(v)
        d = st(m, f"{lab:<30} {op} cell median"
                  + ("   <- incumbent sign" if INCUMBENT[col] == op else ""))
        d["feature"] = col
        rows.append(d)
D = pd.DataFrame(rows)
print(D[["rule", "n", "PF", "PFtrim", "mean%", "med%", "win%", "p5%", "worst5%",
         "WORST%", "loss%", "yrs<1", "pctile", "floor"]].to_string(index=False))
print(f"  ⚠ noise floor ~{D.floor.median():.3f}: a random half of this cell reaches "
      f"{D.floor.median():.0%} of its PF 1 time in 20.")
print("  ⚠ every row keeps ~half the cell, so PF alone rises mechanically — read pctile.")

print(f"\n{'='*215}\n§2 THE SURVIVORS, stacked (only features clearing the 90th "
      f"percentile above)\n{'='*215}")
surv = [(rr["feature"], rr["rule"].split()[-3]) for _, rr in D.iterrows()
        if rr.get("pctile", 0) >= 90]
rows = [st(Q, "the cell", boot=False)]
masks = {}
for col, op in surv:
    v = A[col].values.astype(float)
    t = np.nanquantile(v[Q], .5)
    m = Q & ((v <= t) if op == "<=" else (v > t)) & ~np.isnan(v)
    masks[(col, op)] = m
    rows.append(st(m, f"+ {col} {op} {t:.3f}"))
if len(masks) >= 2:
    for (c1, o1), (c2, o2) in itertools.combinations(masks, 2):
        rows.append(st(masks[(c1, o1)] & masks[(c2, o2)],
                       f"+ {c1} {o1} AND {c2} {o2}"))
if len(masks) >= 3:
    m = Q.copy()
    for k in masks:
        m &= masks[k]
    rows.append(st(m, "+ ALL survivors"))
print(pd.DataFrame(rows).fillna("").to_string(index=False))
if not surv:
    print("  (nothing cleared the 90th percentile — the cell is homogeneous)")

print(f"\n{'='*215}\n⭐⭐ §3 THE 2020 CONTROL — mandatory here, because 2020 is 39% of "
      f"this cell\n{'='*215}")
# ⚠⚠ A feature that preferentially SELECTS 2020 dates inherits 2020's PF without having
# any edge of its own. Every §1 survivor is therefore re-measured with 2020 removed,
# against a null drawn from the EX-2020 cell.
Qx = Q & (A.yr.values != 2020)
rQx = r[Qx]
nQx = int(Qx.sum())
rows = [st(Q, "the cell (all years)", boot=False),
        st(Qx, "the cell EX-2020", boot=False)]
for (col, op), m in masks.items():
    v = A[col].values.astype(float)
    tx = np.nanquantile(v[Qx], .5)
    mx = Qx & ((v <= tx) if op == "<=" else (v > tx)) & ~np.isnan(v)
    k = int(mx.sum())
    null = np.array([pf(rQx[rng.choice(nQx, k, replace=False)])
                     for _ in range(args.boot)])
    null = null[np.isfinite(null)]
    d = st(mx, f"EX-2020  + {col} {op}", boot=False)
    d["pctile"] = round(float((null < pf(r[mx])).mean()*100), 1)
    d["floor"] = round(float(np.quantile(null, .95)/np.median(null)), 3)
    d["2020 share of full cell"] = f"{(m & (A.yr.values == 2020)).sum()/m.sum()*100:.0f}%"
    rows.append(d)
print(pd.DataFrame(rows).fillna("").to_string(index=False))
print("  ⚠ '2020 share' is of the FULL-sample version of that filter. A share well")
print("    above the cell's own 39% means the filter is selecting 2020, not edge.")

print(f"\n{'='*215}\n§4 PER-YEAR of the cell and of any stacked survivor\n{'='*215}")
rows = []
cands = [("the cell", Q)]
for (c, o), m in list(masks.items())[:3]:
    cands.append((f"+ {c} {o}", m))
for lbl, m in cands:
    row = {"variant": lbl, "n": int(m.sum()), "PF": round(pf(r[m]), 3)}
    for y in yrs:
        mm = m & (A.yr.values == y)
        row[str(y)] = ("." if mm.sum() < 3 else f"{pf(r[mm]):.2f}({int(mm.sum())})")
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
