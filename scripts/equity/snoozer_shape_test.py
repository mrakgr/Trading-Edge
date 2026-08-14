"""S43cb — do intraday dollar-volume SHAPE features beat the gap count on the Snoozer sides?

⭐ USER (2026-08-14): "compare the last hour's dollars to the rest of the day".

Tests a family of shape measures against each other and against the incumbent gap
count, on BOTH sides, using the substitution test — never correlation
(`feedback_high_correlation_proves_nothing`: ρ 0.998 twins read PF 2.92 vs 1.83, and
here ρ 0.008 measures read 3.41 vs 1.28; ρ tells you nothing about a gate).

Features, all knowable at 16:00, all built from Σ(vwap x volume) per bucket:

    dv_over_rest    (E+F)/(A+B+C+D)             the user's ask, directly
    lh_share        (E+F)/(A..F)                bounded [0,1], scale-free twin
    lh_rate         per-second last hour / per-second rest-of-day    1.0 = flat day
    tc_rate         the same on TRADE COUNT rather than dollars
    dv_over_open    (E+F)/A                     ⚠ the S43ca accident, kept as the
                                                incumbent to beat — its denominator
                                                is the universe gate, which is a
                                                coincidence, not a design
    f_share_of_lh   F/(E+F)                     is the last hour front- or back-loaded
    gaps            3540 - nb60k59              persistence, the current lever

⚠ EVERY comparison is at MATCHED SELECTIVITY. A filter that keeps fewer trades raises
PF mechanically (`feedback_iso_trip_control_for_stacked_features`), so all levers are
cut to the same n and a random subsample of that n is shown as the floor.

Usage:  python scripts/equity/snoozer_shape_test.py [--side short] [--chg 0.06]
"""
import argparse

import duckdb
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--cache", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--side", choices=["short", "long"], default="short")
ap.add_argument("--chg", type=float, default=0.06,
                help="signal magnitude floor on chg60k59 (short: >, long: < -chg)")
ap.add_argument("--q", type=float, default=0.25, help="selectivity of each lever")
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
sign = "-" if args.side == "short" else ""
cond = (f"chg60k59 > {args.chg}" if args.side == "short"
        else f"chg60k59 < {-args.chg}")
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT *, 3540 - nb60k59 AS gaps, year(date) AS yr,
       {sign}(ovn_from_lim59) AS r
FROM read_parquet('{args.cache}')
WHERE {cond} AND ovn_from_lim59 IS NOT NULL AND dv_rest > 0 AND dv_lh > 0""")
N = con.execute("SELECT count(*) FROM S").fetchone()[0]
print(f"side={args.side}  signal |chg60k59| > {args.chg}  population {N:,}\n")

PF = ("sum(CASE WHEN r>0 THEN r ELSE 0 END) / "
      "nullif(-sum(CASE WHEN r<0 THEN r ELSE 0 END), 0)")

# ⭐ (label, expression, direction the SHORT side wants). The LONG side wants the
# OPPOSITE on every one of these — established in S43cb, where importing the short's
# sign gave PF 0.829, worse than a random subsample. So the direction is flipped
# wholesale by `--side` rather than listed twice.
LEVERS_SHORT = [
    ("$  dv_over_open15   [incumbent]", "dv_over_open15", "lo"),
    ("$  dv_over_open30", "dv_over_open30", "lo"),
    ("$  dv_over_rest", "dv_over_rest", "lo"),
    ("⭐ BAR bar_over_open15", "bar_over_open15", "lo"),
    ("⭐ BAR bar_over_open30", "bar_over_open30", "lo"),
    ("⭐ BAR bar_over_open5", "bar_over_open5", "lo"),
    ("⭐ BAR bar_over_rest", "bar_over_rest", "lo"),
    ("   tc_rate  (trade count)", "tc_rate", "lo"),
    ("   gaps     (absolute persistence)", "gaps", "hi"),
]
FLIP = {"lo": "hi", "hi": "lo"}
LEVERS = ([(l, e, d) for l, e, d in LEVERS_SHORT] if args.side == "short"
          else [(l, e, FLIP[d]) for l, e, d in LEVERS_SHORT])

print("=" * 170)
print(f"§1 SUBSTITUTION TEST — each lever cut to the same selectivity (q={args.q})")
print("=" * 170)
rows = []
base = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
    min(r)*100 w FROM S""").fetchdf()
rows.append({"lever": "— no filter (baseline)", "n": f"{int(base.n[0]):,}",
             "PF": f"{base.pf[0]:.3f}", "mean%": f"{base.m[0]:+.2f}",
             "med%": f"{base.md[0]:+.2f}", "worst%": f"{base.w[0]:.0f}"})
for lbl, expr, dirn in LEVERS:
    op, q = ((" <= ", args.q) if dirn == "lo" else (" >= ", 1 - args.q))
    d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
        min(r)*100 w FROM S
        WHERE {expr} {op} (SELECT quantile_cont({expr}, {q}) FROM S)""").fetchdf()
    rows.append({"lever": lbl, "n": f"{int(d.n[0]):,}", "PF": f"{d.pf[0]:.3f}",
                 "mean%": f"{d.m[0]:+.2f}", "med%": f"{d.md[0]:+.2f}",
                 "worst%": f"{d.w[0]:.0f}"})
# ⭐ the RANDOM control: same n, no information. Anything not beating this is noise.
d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
    min(r)*100 w FROM (SELECT * FROM S ORDER BY hash(ticker || date::VARCHAR)
                       LIMIT (SELECT CAST(count(*)*{args.q} AS INT) FROM S))""").fetchdf()
rows.append({"lever": "⭐ RANDOM subsample (same n)", "n": f"{int(d.n[0]):,}",
             "PF": f"{d.pf[0]:.3f}", "mean%": f"{d.m[0]:+.2f}",
             "med%": f"{d.md[0]:+.2f}", "worst%": f"{d.w[0]:.0f}"})
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*170}\n§2 ARE THEY THE SAME THING? pairwise ρ and OVERLAP of the selected sets\n"
      f"⚠ ρ is reported for orientation only — it is NOT the test (S43ca: ρ 0.008 "
      f"measures read PF 3.41 vs 1.28)\n{'='*170}")
sel = {}
for lbl, expr, dirn in LEVERS:
    op, q = ((" <= ", args.q) if dirn == "lo" else (" >= ", 1 - args.q))
    sel[expr] = f"{expr} {op} (SELECT quantile_cont({expr}, {q}) FROM S)"
names = [e for _, e, _ in LEVERS]
rows = []
for a in names:
    r = {"lever": a}
    for b in names:
        if a == b:
            r[b] = "—"
        else:
            v = con.execute(f"""SELECT
                100.0*count(*) FILTER ({sel[a]} AND {sel[b]})
                  / nullif(count(*) FILTER ({sel[a]}),0) FROM S""").fetchone()[0]
            r[b] = f"{v:.0f}%"
    rows.append(r)
print("overlap: of the trades lever ROW selects, what % does lever COL also select?")
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*170}\n§3 THE WINNER, BY YEAR (PF) — a lever that only works in one regime is not a lever\n{'='*170}")
yrs = [r[0] for r in con.execute("SELECT DISTINCT yr FROM S ORDER BY 1").fetchall()]
rows = []
for lbl, expr, dirn in LEVERS:
    op, q = ((" <= ", args.q) if dirn == "lo" else (" >= ", 1 - args.q))
    w = f"{expr} {op} (SELECT quantile_cont({expr}, {q}) FROM S)"
    r = {"lever": lbl}
    neg = 0
    for y in yrs:
        d = con.execute(f"SELECT count(*) n, {PF} pf FROM S WHERE {w} AND yr={y}").fetchdf()
        if int(d.n[0]) < 10:
            r[str(y)] = "."
        else:
            r[str(y)] = f"{d.pf[0]:.2f}" if pd.notna(d.pf[0]) else "inf"
            neg += (d.pf[0] or 0) < 1.0
    r["yrs<1"] = neg
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print("  ('.' = fewer than 10 trades that year)")
