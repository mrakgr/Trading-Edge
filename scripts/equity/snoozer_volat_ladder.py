"""S43cg — does the volatility lever HOLD UP as the base cell widens?

⭐ USER (2026-08-16): "Instead of the 370 trade incumbent, we should verify the
volatility feature against shape >= q50%. The 2k trade bucket would give us much
better confirmation."

## Why widening is the real test

A 370-trade cell splits into halves of 185, where the bootstrap null's own p95 sits
**28% above its median** (2.94 vs 2.29). Nothing can be resolved below that floor.
Widening the base cell lowers the floor — at n=2,082 the null p95 is only 13% above
its median (1.83 vs 1.62). So:

    a REAL lever gets MORE significant as the cell grows (the floor drops faster
    than the effect);  an ARTEFACT of the narrow cell decays toward the floor.

That DIVERGENCE is the test, and it is strictly more informative than any single
percentile. This script runs the same bootstrap at four widths and prints the trend.

⚠ PF is NOT comparable across the ladder — a wider base has a lower base PF by
construction. The comparable quantities are the **percentile** and the **lift ratio**
`PF(candidate) / PF(base)`, both of which are scale-free. Both are printed; the raw
PFs are printed too, but only to be read DOWN a column, never across.

⚠ The base cells are NESTED (q75 ⊂ q50 ⊂ shape50 ⊂ all), so these are not four
independent confirmations — they are one population read at four depths. A feature
surviving all four means it is not an artefact of the narrowest cut; it does NOT mean
four independent tests passed.

Usage:  python scripts/equity/snoozer_volat_ladder.py --side long
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

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT s.ticker, s.date, year(s.date) AS yr, {sign}(s.ovn_from_lim59) AS r,
       3540 - s.nb60k59 AS gaps, s.dv_over_open15 AS shape,
       v.volat_open15, v.volat_open30, v.volat_open60, v.volat_day, v.volat_dayfull,
       v.volat_lh,
       v.volat_lh/nullif(v.volat_open30, 0) AS volat_over_open30,
       v.volat_lh/nullif(v.volat_day, 0)    AS volat_over_day
FROM read_parquet('{args.shape}') s
JOIN read_parquet('{args.volat}') v ON v.ticker = s.ticker AND v.date = s.date
WHERE {cond} AND s.ovn_from_lim59 IS NOT NULL AND s.dv_lh > 0
  AND s.dv_over_open15 IS NOT NULL""")

SQ = f"(SELECT quantile_cont(shape, %s) FROM S)"
BASES = [("q75  gaps+shape>=q75", f"gaps {gap_op} {GAP_T} AND shape {shp_op} {SQ % 0.75}"),
         ("q50  gaps+shape>=q50", f"gaps {gap_op} {GAP_T} AND shape {shp_op} {SQ % 0.50}"),
         ("⭐ shape>=q50 (no gaps)", f"shape {shp_op} {SQ % 0.50}"),
         ("all  no filter", "TRUE")]
FEATS = ["volat_open15", "volat_open30", "volat_open60", "volat_day", "volat_dayfull",
         "volat_lh", "volat_over_open30", "volat_over_day", "gaps", "shape"]
DIRN = {f: ("<=" if not f.startswith("volat_over") else ">=") for f in FEATS}
DIRN["gaps"], DIRN["shape"] = gap_op, shp_op
rng = np.random.default_rng(args.seed)


def pf(x):
    g, l = x[x > 0].sum(), -x[x < 0].sum()
    return float("inf") if l == 0 else g / l


cells = {}
for lbl, w in BASES:
    C = con.execute(f"SELECT * FROM S WHERE {w}").fetchdf()
    cells[lbl] = C
    print(f"{lbl:<26} n = {len(C):>5,}   PF {pf(C.r.values):.3f}   "
          f"mean {C.r.values.mean()*100:+.2f}%   worst {C.r.values.min()*100:.0f}%")

print(f"\n{'='*180}\n⭐ §1 BOOTSTRAP PERCENTILE across the ladder "
      f"(each feature cut at ITS OWN median WITHIN that cell)\n{'='*180}")
res = {}
for f in FEATS:
    row = {"feature": f"{f} {DIRN[f]} cell median"}
    for lbl, _ in BASES:
        C = cells[lbl]
        r, n = C.r.values, len(C)
        v = C[f].values.astype(float)
        med = np.nanmedian(v)
        m = ((v <= med) if DIRN[f] == "<=" else (v >= med)) & ~np.isnan(v)
        k = int(m.sum())
        cand = pf(r[m])
        null = np.array([pf(r[rng.choice(n, k, replace=False)])
                         for _ in range(args.boot)])
        null = null[np.isfinite(null)]
        pct = float((null < cand).mean() * 100)
        # ⚠ TAIL = mean of the worst 5%, NOT min. The single worst trade is the least
        # stable statistic in the book (feedback_trim_bottom_5pct_when_comparing) and
        # it lies here: on the wide cells min is UNCHANGED at -90% for every
        # volatility cut, while p5 goes -18.8 -> -10.1 and the loss rate 43% -> 36%.
        # Reading min would have said "the tail cut does not survive widening".
        res[(f, lbl)] = (cand, pf(r), pct, np.sort(r[m])[:max(1, k // 20)].mean(),
                         np.median(null), np.quantile(null, .95))
        row[lbl] = f"{pct:.1f}"
    res_row = row
    print_row = res_row
    if "rows" not in dir():
        rows = []
    rows.append(print_row)
print(pd.DataFrame(rows).to_string(index=False))
print("  95+ = a lever. `gaps`/`shape` are the INCUMBENT levers tightened on")
print("  themselves — the bar. ⭐ read ACROSS: rising or flat = real, falling = artefact.")

print(f"\n{'='*180}\n§2 LIFT RATIO  PF(candidate) / PF(base)  — the scale-free "
      f"effect size\n{'='*180}")
rows = []
for f in FEATS:
    row = {"feature": f}
    for lbl, _ in BASES:
        cand, base, pct, _, _, _ = res[(f, lbl)]
        row[lbl] = f"{cand/base:.2f}x"
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*180}\n§3 THE NOISE FLOOR — why the wide cell is the better test\n{'='*180}")
rows = []
for lbl, _ in BASES:
    cand, base, pct, _, nm, n95 = res[("volat_open60", lbl)]
    rows.append({"base cell": lbl, "n": f"{len(cells[lbl]):,}",
                 "base PF": f"{base:.3f}", "null p50": f"{nm:.3f}",
                 "null p95": f"{n95:.3f}",
                 "⚠ floor = p95/p50": f"{n95/nm:.3f}",
                 "volat_open60 PF": f"{cand:.3f}", "pctile": f"{pct:.1f}"})
print(pd.DataFrame(rows).to_string(index=False))
print("  floor = how far a RANDOM half can wander above the cell it came from.")
print("  A narrow cell cannot resolve anything below its own floor.")

print(f"\n{'='*180}\n⭐ §4 DOES IT SURVIVE THE TAIL?  MEAN OF THE WORST 5% "
      f"(never min — see the note in §1)\n{'='*180}")
rows = []
for f in FEATS:
    row = {"feature": f}
    for lbl, _ in BASES:
        cand, base, pct, wo, _, _ = res[(f, lbl)]
        r0 = cells[lbl].r.values
        b = np.sort(r0)[:max(1, len(r0) // 20)].mean()
        row[lbl] = f"{wo*100:.1f}% (base {b*100:.1f}%)"
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
print("  a feature that only raises PF by dropping small losers leaves this column flat.")
