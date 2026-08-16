"""S43cf — is the volatility lever REAL, or is it a lucky half of a 370-trade cell?

## Why this script exists

§3 of `snoozer_volat_test.py` printed ONE random-half control per side, and on the
SHORT side that single draw read **PF 4.681 against the incumbent's 3.948** — a random
half of the cell beat the cell it came from. At n≈380 with a fat tail, one draw is not
a control; it is a coin flip. Everything §3 concluded has to be re-tested against the
DISTRIBUTION of random halves, not one sample.

So: for a candidate filter that keeps k of the incumbent cell's n trades, draw B random
k-subsets and report where the candidate's PF falls in that distribution. A candidate at
the 60th percentile is arithmetic; one at the 99th is a lever.

⚠ This is a permutation test on the SAME trades, so it controls for exactly one thing:
the mechanical PF gain from cutting trades. It does NOT control for the candidate having
been chosen after looking at these tables — §3 tried 20 variants per side, so a single
99th-percentile hit is roughly what 20 tries buys you. The defence against that is §2's
threshold sweep (a real lever is a PLATEAU, not a spike) and the per-year column, not a
smaller p-value.

## Sections

  §1  bootstrap percentile for every §3 candidate, both iso-trip controls included
  §2  ABSOLUTE threshold sweep — a live spec cannot gate on a median
  §3  the surviving cell's full per-year record

Usage:  python scripts/equity/snoozer_volat_robust.py --side long
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
ap.add_argument("--side", choices=["long", "short"], default="long")
ap.add_argument("--chg", type=float, default=0.06)
ap.add_argument("--boot", type=int, default=2000)
ap.add_argument("--seed", type=int, default=20260816)
args = ap.parse_args()

sign = "-" if args.side == "short" else ""
cond = f"chg60k59 > {args.chg}" if args.side == "short" else f"chg60k59 < {-args.chg}"
gap_op, shp_op = ("<=", ">=") if args.side == "long" else (">=", "<=")
GAP_T = 760 if args.side == "long" else 2000
SHP_Q = 0.75 if args.side == "long" else 0.25

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, {sign}(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.dv_over_open15 AS shape,
       v.volat_open15, v.volat_open30, v.volat_open60, v.volat_day, v.volat_dayfull,
       v.volat_lh,
       v.volat_lh/nullif(v.volat_open15, 0) AS volat_over_open15,
       v.volat_lh/nullif(v.volat_open30, 0) AS volat_over_open30,
       v.volat_lh/nullif(v.volat_open60, 0) AS volat_over_open60,
       v.volat_lh/nullif(v.volat_day, 0)    AS volat_over_day
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE {cond} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")
INC = (f"gaps {gap_op} {GAP_T} AND shape {shp_op} "
       f"(SELECT quantile_cont(shape, {SHP_Q}) FROM S)")
C = con.execute(f"SELECT * FROM S WHERE {INC}").fetchdf()
r = C.r.values
n = len(C)
print(f"side={args.side}   incumbent cell  gaps {gap_op} {GAP_T} AND shape "
      f"{shp_op} q{SHP_Q:.0%}   n = {n:,}")


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


print(f"  cell PF {pf(r):.3f}   mean {r.mean()*100:+.2f}%   worst {r.min()*100:.0f}%\n")

rng = np.random.default_rng(args.seed)
FEATS = ["volat_open15", "volat_open30", "volat_open60", "volat_day", "volat_dayfull",
         "volat_lh", "volat_over_open15", "volat_over_open30", "volat_over_open60",
         "volat_over_day"]

print("=" * 165)
print(f"⭐ §1 BOOTSTRAP — candidate PF vs {args.boot:,} RANDOM subsets of the same size")
print("=" * 165)
rows = []
for f in FEATS + ["gaps", "shape"]:
    v = C[f].values
    med = np.nanmedian(v)
    for op in ("<=", ">="):
        m = (v <= med) if op == "<=" else (v >= med)
        m &= ~np.isnan(v)
        k = int(m.sum())
        if k < 30 or k > n - 30:
            continue
        cand = pf(r[m])
        # ⭐ the null: same trades, same count, random membership
        null = np.array([pf(r[rng.choice(n, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        pct = float((null < cand).mean() * 100) if np.isfinite(cand) else 100.0
        rows.append({"filter": f"{f} {op} cell median", "k": k,
                     "PF": f"{cand:.3f}", "null p50": f"{np.median(null):.3f}",
                     "null p95": f"{np.quantile(null, .95):.3f}",
                     "⭐ pctile": f"{pct:.1f}",
                     "worst%": f"{r[m].min()*100:.0f}",
                     "mean%": f"{r[m].mean()*100:+.2f}"})
d = pd.DataFrame(rows).sort_values("⭐ pctile", key=lambda s: s.astype(float),
                                   ascending=False)
print(d.to_string(index=False))
print("  pctile = % of random same-size subsets the candidate beats. A LEVER sits at")
print("  95+; a 50 is the arithmetic of cutting trades. `gaps`/`shape` rows are the")
print("  incumbent levers tightened on themselves — the bar a new feature must clear.")

BEST = {"long": ("volat_open60", "<="), "short": ("volat_over_day", ">=")}[args.side]
f, op = BEST
print(f"\n{'='*165}\n⭐ §2 ABSOLUTE THRESHOLD SWEEP for {f} {op} T  —  a live spec "
      f"cannot gate on a median, and a real lever is a PLATEAU\n{'='*165}")
v = C[f].values
sc = 1e4 if "over" not in f else 1.0
rows = []
for q in (0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80):
    t = float(np.nanquantile(v, q if op == "<=" else 1 - q))
    m = ((v <= t) if op == "<=" else (v >= t)) & ~np.isnan(v)
    k = int(m.sum())
    if k < 30:
        continue
    cand = pf(r[m])
    null = np.array([pf(r[rng.choice(n, k, replace=False)]) for _ in range(args.boot)])
    null = null[np.isfinite(null)]
    rows.append({"keeps": f"{q:.0%}",
                 f"T ({'bp' if sc > 1 else 'ratio'})": f"{t*sc:.2f}",
                 "n": k, "PF": f"{cand:.3f}",
                 "null p50": f"{np.median(null):.3f}",
                 "pctile": f"{(null < cand).mean()*100:.1f}",
                 "mean%": f"{r[m].mean()*100:+.2f}",
                 "med%": f"{np.median(r[m])*100:+.2f}",
                 "win%": f"{(r[m] > 0).mean()*100:.0f}",
                 "worst%": f"{r[m].min()*100:.0f}"})
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*165}\n§3 PER-YEAR — incumbent vs incumbent + {f} {op} its median\n{'='*165}")
med = float(np.nanmedian(v))
m = ((v <= med) if op == "<=" else (v >= med)) & ~np.isnan(v)
rows = []
for lbl, mask in (("incumbent", np.ones(n, bool)), (f"+ {f} {op} {med*sc:.1f}"
                                                    f"{'bp' if sc > 1 else ''}", m)):
    row = {"variant": lbl, "n": int(mask.sum()), "PF": f"{pf(r[mask]):.3f}"}
    for y in sorted(C.yr.unique()):
        mm = mask & (C.yr.values == y)
        row[str(y)] = ("." if mm.sum() < 5 else
                       ("inf" if pf(r[mm]) == float("inf") else f"{pf(r[mm]):.2f}"))
        row[str(y)] += f" ({int(mm.sum())})"
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
print("  ('.' = fewer than 5 trades that year;  'inf' = ZERO LOSERS, not missing)")
