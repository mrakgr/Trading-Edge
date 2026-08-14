"""S43cb — the two-lever Snoozer grid: tape PERSISTENCE x last-hour DOLLAR SHAPE.

Supersedes `snoozer_grid.py`, which had only one density axis and — on the short
side — only `gaps <= X` rows, i.e. progressively DENSER names, which is the wrong
half of the axis for that system (user caught it, S43ca).

## The two levers, and why both

    gaps      = 3540 - nb60k59         PERSISTENCE: seconds of (15:00,15:59] that
                                       did NOT trade
    shape     = dv(15:00-16:00) / dv(09:30-09:45)   MAGNITUDE-RELATIVE-TO-THE-OPEN:
                                       is the closing hour loud or quiet compared to
                                       the day's opening burst

They overlap only ~53% at matched selectivity and stack (short: 3.419 + 2.645 alone
-> 3.964 together). They are NOT interchangeable, and neither is `vol_lh /
avgvol20_prior`, which is ρ 0.008 with `shape` and actively HARMFUL (PF 1.275 vs a
1.666 baseline). "Thin tape" is underdetermined; the operationalisation decides it.

## ⭐⭐ THE SIGN FLIPS BETWEEN THE TWO SYSTEMS

    ShortSnoozer wants  THIN tape (many gaps)  x  LOW  shape (quiet close vs open)
    LongSnoozer  wants  DENSE tape (few gaps)  x  HIGH shape (loud  close vs open)

Applying the short's filter to the long side gives PF 0.829 with 7 losing years —
worse than baseline AND worse than a random subsample. One variable, read from both
ends: the overnight move continues in the direction the closing hour's PARTICIPATION
points. Heavy continuous late selling exhausts and bounces; a light gappy late rally
is nobody, and it fades.

⚠ The reference window for `shape` is a SHALLOW optimum: 15m and 30m share 89-92% of
their picks and trade places depending on the selectivity cut. "Short beats long" is
robust (rest-of-day is last on the short side at every cut); "15m beats 30m" is not.
Do not tune inside the 5m-30m plateau.

Usage:
    python scripts/equity/snoozer_grid2.py --side long
    python scripts/equity/snoozer_grid2.py --side short --ref 15
"""
import argparse

import duckdb
import pandas as pd

pd.set_option("display.width", 260)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--cache", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--side", choices=["long", "short"], default="long")
ap.add_argument("--ref", choices=["5", "15", "30", "60", "90", "rest"], default="15",
                help="opening reference window for the shape feature")
ap.add_argument("--trim", type=float, default=0.05)
args = ap.parse_args()

SHAPE = ("dv_over_rest" if args.ref == "rest" else f"dv_over_open{args.ref}")
# ⭐ SIGN CONVENTION: `r` is the return TO THE TRADE, so both sides read identically.
sign = "" if args.side == "long" else "-"
cmp_ = "<" if args.side == "long" else ">"
# the long wants DENSE + LOUD close; the short wants THIN + QUIET close
gap_op = "<=" if args.side == "long" else ">="
shp_op = ">=" if args.side == "long" else "<="

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT *, 3540 - nb60k59 AS gaps, {SHAPE} AS shape, year(date) AS yr,
       {sign}(ovn_from_lim59) AS r
FROM read_parquet('{args.cache}')
WHERE ovn_from_lim59 IS NOT NULL AND dv_lh > 0 AND {SHAPE} IS NOT NULL""")
yrs = [r[0] for r in con.execute("SELECT DISTINCT yr FROM S ORDER BY 1").fetchall()]
print(f"side={args.side}   shape = last-hour $ / first-{args.ref} $   ({SHAPE})")
print(f"  keep gaps {gap_op} row   and   shape {shp_op} its quantile   "
      f"and   chg60k59 {cmp_} col\n")

PF = ("sum(CASE WHEN r>0 THEN r ELSE 0 END) / "
      "nullif(-sum(CASE WHEN r<0 THEN r ELSE 0 END), 0)")
DEPTH = ([-0.03, -0.04, -0.06, -0.08] if args.side == "long"
         else [0.03, 0.04, 0.06, 0.08])
# gap rows read in the direction each side wants
GAPS = ([200, 400, 760, 1200, 1800, 3540] if args.side == "long"
        else [3000, 2500, 2000, 1500, 1000, 0])
SHQ = [0.10, 0.25, 0.50, 1.00]


def cell(gcond, scond, dcond, trim=0.0):
    w = f"{gcond} AND {scond} AND {dcond}"
    cut = (f"AND r >= (SELECT quantile_cont(r, {trim}) FROM S WHERE {w})"
           if trim > 0 else "")
    d = con.execute(f"SELECT count(*) n, {PF} pf FROM S WHERE {w} {cut}").fetchdf()
    n = int(d.n[0])
    if n < 30:
        return "."
    # ⚠ 'inf' = ZERO LOSING TRADES, never NULL (feedback_label_infinite_profit_factors)
    return f"inf ({n:,})" if pd.isna(d.pf[0]) else f"{d.pf[0]:.3f} ({n:,})"


for trim, tag in ((0.0, "RAW PF (n) — the fat tail is IN here"),
                  (args.trim, f"TRIMMED PF (n) — bottom {args.trim:.0%} dropped "
                              f"[what SIZING should read]")):
    for d in DEPTH:
        dcond = f"chg60k59 {cmp_} {d}"
        print(f"\n{'='*150}\n{tag}   |   move {cmp_} {d*100:g}%\n{'='*150}")
        rows = []
        for g in GAPS:
            gcond = f"gaps {gap_op} {g}"
            r = {"gaps" + gap_op: g}
            for q in SHQ:
                qq = q if args.side == "short" else 1 - q
                scond = ("TRUE" if q == 1.00 else
                         f"shape {shp_op} (SELECT quantile_cont(shape, {qq}) FROM S "
                         f"WHERE {gcond} AND {dcond})")
                lab = "shape: all" if q == 1.00 else f"shape {shp_op} q{q:.0%}"
                r[lab] = cell(gcond, scond, dcond, trim)
            rows.append(r)
        print(pd.DataFrame(rows).to_string(index=False))
    print("  ('.' = fewer than 30 trades;  'inf' = zero losers, NOT missing data)")

print(f"\n{'='*170}\n⭐ LEADING CELLS, PER YEAR — the only test that matters\n{'='*170}")
if args.side == "long":
    CELLS = [(760, 0.25, -0.06), (760, 0.50, -0.06), (400, 0.25, -0.06),
             (1200, 0.25, -0.04), (3540, 0.25, -0.06), (760, 1.00, -0.06)]
else:
    CELLS = [(2000, 0.25, 0.06), (2000, 0.50, 0.06), (2500, 0.25, 0.06),
             (1000, 0.25, 0.08), (0, 0.25, 0.06), (2000, 1.00, 0.06)]
rows = []
for g, q, d in CELLS:
    gcond, dcond = f"gaps {gap_op} {g}", f"chg60k59 {cmp_} {d}"
    qq = q if args.side == "short" else 1 - q
    scond = ("TRUE" if q == 1.00 else
             f"shape {shp_op} (SELECT quantile_cont(shape, {qq}) FROM S "
             f"WHERE {gcond} AND {dcond})")
    w = f"{gcond} AND {scond} AND {dcond}"
    v = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m_, median(r)*100 md_,
        avg(CASE WHEN r>0 THEN 1.0 ELSE 0 END)*100 w_, min(r)*100 wo_
        FROM S WHERE {w}""").fetchdf()
    if int(v.n[0]) < 30:
        continue
    row = {"cell": f"gaps{gap_op}{g} x shape{'all' if q==1 else f'{shp_op}q{q:.0%}'}"
                   f" x {cmp_}{d*100:g}%",
           "n": f"{int(v.n[0]):,}", "PF": f"{v.pf[0]:.3f}",
           "mean%": f"{v.m_[0]:+.2f}", "med%": f"{v.md_[0]:+.2f}",
           "win%": f"{v.w_[0]:.0f}", "worst%": f"{v.wo_[0]:.0f}"}
    neg = 0
    for y in yrs:
        e = con.execute(f"SELECT count(*) n, {PF} pf FROM S WHERE {w} AND yr={y}").fetchdf()
        if int(e.n[0]) < 5:
            row[str(y)] = "."
        elif pd.isna(e.pf[0]):
            row[str(y)] = "inf"
        else:
            row[str(y)] = f"{e.pf[0]:.2f}"
            neg += e.pf[0] < 1.0
    row["yrs<1"] = neg
    rows.append(row)
print(pd.DataFrame(rows).to_string(index=False))
print("  ('.' = fewer than 5 trades that year)")
