"""S43by — the Snoozer PF grid (tape density x move depth), re-derived on the FULL-DAY corpus.

Replaces the S43bt/S43bv grids, every number of which was measured against a 1s
corpus that ended at 15:58:59 — missing the last RTH minute, which is both the
heaviest of the session and exactly where this system fills.

One trade = one ticker-day. Entry is a LIMIT resting in the window that starts
where the signal stops; exit is the next session's open.

    --entry k59   signal to 15:59, fill 15:59-16:00   gaps out of 3540   ⭐ default
    --entry k57   signal to 15:57, fill 15:57-16:00   gaps out of 3420   (the old spec)
    --entry close signal to 16:00, "fill" AT the close — ⚠ UNTRADEABLE, upper bound only

⚠ PF here is on per-trade returns, unlevered and pre-cost. It is NOT the FlushFader
book's PF and the two are not comparable.

⭐ RAW **and** TRIMMED PF are shown together: raw exposes the fat tail, and the
bottom-5% trim is what the sizing decision should read, because the top losers are
the uncertain part of the distribution, not the median trade.

Usage:
    python scripts/equity/snoozer_grid.py --side long
    python scripts/equity/snoozer_grid.py --side short --entry k59
"""
import argparse

import duckdb
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--cache", default="data/equity/flushfader/snoozer_cache.parquet")
ap.add_argument("--side", choices=["long", "short"], default="long")
ap.add_argument("--entry", choices=["k59", "k57", "close"], default="k59")
ap.add_argument("--window", choices=["60", "30", "15", "05"], default="60",
                help="signal window in minutes; 60 is the best on BOTH sides at "
                     "matched selectivity (S43by §3) — the others are for control runs")
ap.add_argument("--trim", type=float, default=0.05)
args = ap.parse_args()

# (signal column, return column, gap column, window seconds)
ENTRY = {"k59":   (f"chg{args.window}k59", "ovn_from_lim59", f"nb{args.window}k59", 3540),
         "k57":   (f"chg{args.window}k",   "ovn_from_lim57", f"nb{args.window}k",   3420),
         "close": (f"chg{args.window}",    "ovn_from_close", f"nb{args.window}",    3600)}
SIG, RET, NB, SECS = ENTRY[args.entry]
if args.window != "60":                       # shorter windows span fewer seconds
    SECS = {"30": 1800, "15": 900, "05": 300}[args.window]
    SECS -= {"k59": 60, "k57": 180, "close": 0}[args.entry]

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
# ⭐ SIGN CONVENTION: `r` is the return TO THE TRADE. A short earns when the stock
# falls, so its return is the NEGATIVE of the overnight move. Everything below —
# PF, win%, mean — then reads identically for both sides.
sign = "" if args.side == "long" else "-"
con.execute(f"""CREATE OR REPLACE TEMP TABLE X AS
SELECT ticker, date, year(date) AS yr, {SIG} AS sig, {SECS} - {NB} AS gaps,
       {sign}({RET}) AS r
FROM read_parquet('{args.cache}')
WHERE {SIG} IS NOT NULL AND {RET} IS NOT NULL AND {NB} IS NOT NULL""")
N = con.execute("SELECT count(*) FROM X").fetchone()[0]
yrs = [r[0] for r in con.execute("SELECT DISTINCT yr FROM X ORDER BY 1").fetchall()]
print(f"population {N:,}   side={args.side}   entry={args.entry} ({SIG} -> {RET})   "
      f"gaps out of {SECS}\n")

GAPS = [200, 400, 600, 760, 900, 1200, 1800, SECS]
DEPTH = ([-0.02, -0.03, -0.04, -0.05, -0.06, -0.08, -0.10] if args.side == "long"
         else [0.02, 0.03, 0.04, 0.06, 0.08, 0.10])
cmp_ = "<" if args.side == "long" else ">"

PF = ("sum(CASE WHEN r>0 THEN r ELSE 0 END) / "
      "nullif(-sum(CASE WHEN r<0 THEN r ELSE 0 END), 0)")


def grid(trim):
    """PF (n) over gaps x depth. trim>0 drops the worst `trim` quantile of trades."""
    rows = []
    for g in GAPS:
        r = {"gaps<=": f"{g}" + ("  (all)" if g == SECS else "")}
        for d in DEPTH:
            w = f"gaps <= {g} AND sig {cmp_} {d}"
            cut = (f"AND r >= (SELECT quantile_cont(r, {trim}) FROM X WHERE {w})"
                   if trim > 0 else "")
            v = con.execute(f"""SELECT count(*) n, {PF} pf
                FROM X WHERE {w} {cut}""").fetchdf()
            n = int(v.n[0])
            r[f"{cmp_}{d*100:g}%"] = (
                "." if n < 30 else
                (f"inf ({n:,})" if pd.isna(v.pf[0]) else f"{v.pf[0]:.3f} ({n:,})"))
            # ⚠ 'inf' means ZERO LOSING TRADES, not missing data — never print NULL.
        rows.append(r)
    return pd.DataFrame(rows)


print("=" * 190)
print(f"RAW PF (n)  — gaps <= row, move {cmp_} col   [the fat tail is IN here]")
print("=" * 190)
print(grid(0.0).to_string(index=False))
print(f"\n{'='*190}\nTRIMMED PF (n)  — bottom {args.trim*100:g}% of trades dropped "
      f"[what the SIZING decision should read]\n{'='*190}")
print(grid(args.trim).to_string(index=False))
print("  ('.' = fewer than 30 trades;  'inf' = zero losing trades, NOT missing data)")

print(f"\n{'='*190}\nPER-YEAR PF for the leading cells — the only test that matters\n{'='*190}")
CELLS = ([(760, -0.04), (760, -0.06), (600, -0.06), (600, -0.08), (1200, -0.03)]
         if args.side == "long"
         else [(760, 0.04), (760, 0.06), (600, 0.06), (600, 0.08), (1200, 0.03)])
rows = []
for g, d in CELLS:
    w = f"gaps <= {g} AND sig {cmp_} {d}"
    v = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 mean_, median(r)*100 med_,
        avg(CASE WHEN r>0 THEN 1.0 ELSE 0 END)*100 win_, min(r)*100 worst_
        FROM X WHERE {w}""").fetchdf()
    if int(v.n[0]) < 30:
        continue
    r = {"cell": f"gaps<={g} x {cmp_}{d*100:g}%", "n": f"{int(v.n[0]):,}",
         "PF": f"{v.pf[0]:.3f}" if pd.notna(v.pf[0]) else "inf",
         "mean%": f"{v.mean_[0]:+.2f}", "med%": f"{v.med_[0]:+.2f}",
         "win%": f"{v.win_[0]:.0f}", "worst%": f"{v.worst_[0]:.1f}"}
    neg = 0
    for y in yrs:
        e = con.execute(f"SELECT count(*) n, {PF} pf FROM X WHERE {w} AND yr={y}").fetchdf()
        if int(e.n[0]) < 5:
            r[str(y)] = "."
        elif pd.isna(e.pf[0]):
            r[str(y)] = "inf"
        else:
            r[str(y)] = f"{e.pf[0]:.2f}"
            neg += e.pf[0] < 1.0
    r["yrs<1"] = neg
    rows.append(r)
print(pd.DataFrame(rows).to_string(index=False))
print("  ('.' = fewer than 5 trades that year; 'inf' = no losers)")

print(f"\n=== trades per year for the leading cell ===")
g, d = CELLS[0]
print(con.execute(f"""SELECT yr, count(*) n, round({PF},3) pf, round(avg(r)*100,2) mean_pct
    FROM X WHERE gaps <= {g} AND sig {cmp_} {d} GROUP BY 1 ORDER BY 1""")
      .fetchdf().to_string(index=False))
