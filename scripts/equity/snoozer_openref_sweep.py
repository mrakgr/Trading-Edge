"""S43cb — how long should the OPENING REFERENCE window be?

⭐ USER (2026-08-14): "would using the first 30m or 60m dollar volume be better as
reference than just the first 15?"

Background. `lh_over_open15` = last-hour dollars / first-15-minutes dollars beat both
the gap count (PF 3.419 vs 2.645) and the user's last-hour-vs-rest-of-day feature
(2.580) on the ShortSnoozer population. But 15 minutes was never CHOSEN — it is the
window `dv_0945_tape` happens to cover because it is the universe gate. So the
reference length is an unswept free parameter, and this sweeps it: 5 / 15 / 30 / 60 /
90 minutes, plus rest-of-day (~5.5h) as the long end of the same ladder.

⚠ LEVELS ARE NOT COMPARABLE ACROSS THE LADDER — a longer reference window
mechanically holds more dollars, so `lh_over_open90` is numerically smaller than
`lh_over_open5` for the same day. Only the ORDERING within each column is meaningful,
which is why every cut here is a QUANTILE, never an absolute threshold.

⚠ MATCHED SELECTIVITY + a same-n RANDOM control on every row. PF rises mechanically
when trades are cut, and the random floor is what separates a lever from arithmetic.

Usage:  python scripts/equity/snoozer_openref_sweep.py [--side short] [--chg 0.06]
"""
import argparse

import duckdb
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--cache", default="data/equity/flushfader/snoozer_shape.parquet")
ap.add_argument("--side", choices=["short", "long"], default="short")
ap.add_argument("--chg", type=float, default=0.06)
ap.add_argument("--q", type=float, default=0.25)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
sign = "-" if args.side == "short" else ""
cond = f"chg60k59 > {args.chg}" if args.side == "short" else f"chg60k59 < {-args.chg}"
con.execute(f"""CREATE OR REPLACE TEMP TABLE S AS
SELECT *, 3540 - nb60k59 AS gaps, year(date) AS yr, {sign}(ovn_from_lim59) AS r
FROM read_parquet('{args.cache}')
WHERE {cond} AND ovn_from_lim59 IS NOT NULL AND dv_lh > 0""")
N = con.execute("SELECT count(*) FROM S").fetchone()[0]
yrs = [r[0] for r in con.execute("SELECT DISTINCT yr FROM S ORDER BY 1").fetchall()]
print(f"side={args.side}  |chg60k59| > {args.chg}  population {N:,}\n")

PF = ("sum(CASE WHEN r>0 THEN r ELSE 0 END) / "
      "nullif(-sum(CASE WHEN r<0 THEN r ELSE 0 END), 0)")

LADDER = [("first  5m", "lh_over_open5"), ("first 15m", "lh_over_open15"),
          ("first 30m", "lh_over_open30"), ("first 60m", "lh_over_open60"),
          ("first 90m", "lh_over_open90"), ("rest of day (~5.5h)", "lh_over_rest")]

print("=" * 175)
print(f"⭐ §1 THE OPENING-REFERENCE SWEEP — keep the lowest {args.q:.0%} of "
      f"last-hour-dollars / reference-window-dollars")
print("=" * 175)
rows = []
d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
    min(r)*100 w FROM S""").fetchdf()
rows.append({"reference": "— no filter (baseline)", "n": f"{int(d.n[0]):,}",
             "PF": f"{d.pf[0]:.3f}", "mean%": f"{d.m[0]:+.2f}",
             "med%": f"{d.md[0]:+.2f}", "worst%": f"{d.w[0]:.0f}", "yrs<1": ""})
for lbl, col in LADDER:
    w = f"{col} <= (SELECT quantile_cont({col}, {args.q}) FROM S) AND {col} IS NOT NULL"
    d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
        min(r)*100 wo FROM S WHERE {w}""").fetchdf()
    neg = 0
    for y in yrs:
        e = con.execute(f"SELECT count(*) n, {PF} pf FROM S WHERE {w} AND yr={y}").fetchdf()
        if int(e.n[0]) >= 10 and pd.notna(e.pf[0]) and e.pf[0] < 1.0:
            neg += 1
    rows.append({"reference": lbl, "n": f"{int(d.n[0]):,}", "PF": f"{d.pf[0]:.3f}",
                 "mean%": f"{d.m[0]:+.2f}", "med%": f"{d.md[0]:+.2f}",
                 "worst%": f"{d.wo[0]:.0f}", "yrs<1": neg})
d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, median(r)*100 md,
    min(r)*100 w FROM (SELECT * FROM S ORDER BY hash(ticker || date::VARCHAR)
    LIMIT (SELECT CAST(count(*)*{args.q} AS INT) FROM S))""").fetchdf()
rows.append({"reference": "⭐ RANDOM same-n control", "n": f"{int(d.n[0]):,}",
             "PF": f"{d.pf[0]:.3f}", "mean%": f"{d.m[0]:+.2f}",
             "med%": f"{d.md[0]:+.2f}", "worst%": f"{d.w[0]:.0f}", "yrs<1": ""})
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*175}\n§2 IS THE SWEEP FLAT? overlap between the ladder rungs\n"
      f"if adjacent rungs pick ~the same trades, the 'best' rung is noise, not a choice\n{'='*175}")
rows = []
for la, ca in LADDER:
    r = {"reference": la}
    for lb, cb in LADDER:
        if ca == cb:
            r[lb] = "—"
        else:
            v = con.execute(f"""SELECT 100.0*count(*) FILTER (
                {ca} <= (SELECT quantile_cont({ca},{args.q}) FROM S) AND
                {cb} <= (SELECT quantile_cont({cb},{args.q}) FROM S))
                / nullif(count(*) FILTER (
                {ca} <= (SELECT quantile_cont({ca},{args.q}) FROM S)),0) FROM S""").fetchone()[0]
            r[lb] = f"{v:.0f}%"
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))

print(f"\n{'='*175}\n§3 PER-YEAR PF down the ladder\n{'='*175}")
rows = []
for lbl, col in LADDER:
    w = f"{col} <= (SELECT quantile_cont({col}, {args.q}) FROM S) AND {col} IS NOT NULL"
    r = {"reference": lbl}
    for y in yrs:
        e = con.execute(f"SELECT count(*) n, {PF} pf FROM S WHERE {w} AND yr={y}").fetchdf()
        r[str(y)] = ("." if int(e.n[0]) < 10 else
                     (f"{e.pf[0]:.2f}" if pd.notna(e.pf[0]) else "inf"))
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print("  ('.' = fewer than 10 trades that year)")

print(f"\n{'='*175}\n§4 SELECTIVITY ROBUSTNESS — does the winner hold at other cuts?\n{'='*175}")
rows = []
for q in (0.10, 0.20, 0.25, 0.33, 0.50):
    r = {"quantile kept": f"{q:.0%}"}
    for lbl, col in LADDER:
        d = con.execute(f"""SELECT count(*) n, {PF} pf FROM S
            WHERE {col} <= (SELECT quantile_cont({col}, {q}) FROM S)
              AND {col} IS NOT NULL""").fetchdf()
        r[lbl] = f"{d.pf[0]:.2f}" if pd.notna(d.pf[0]) else "inf"
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
