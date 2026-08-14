"""S43by — LongSnoozer / ShortSnoozer on the FULL-DAY corpus: is 30m or 15m better than 60m?

Re-derives the whole Snoozer map on the rebuilt 1s tape (the old numbers were
measured against a corpus ending 15:58:59) and answers the user's 2026-08-14
question: does a SHORTER last-window signal beat the last hour, on either side?

⭐⭐ THE METHOD POINT — THRESHOLDS ARE NOT COMPARABLE ACROSS WINDOWS. The same
event measures deeper in a 15m window than a 60m one, so "-4%" selects a
different-sized and differently-composed slice in each. Comparing windows at a
fixed threshold measures the threshold, not the window. §3 therefore compares them
at MATCHED SELECTIVITY: take the n most extreme ticker-days by each window and put
the outcomes side by side. That is the only apples-to-apples read.

⚠ MEDIAN is the headline. The raw mean is distorted by corporate actions the splits
table does not describe.

⚠ Absolute thresholds only — a per-day cross-sectional rank is LOOKAHEAD (it needs
every other name's completed window before you can classify your own).

Usage:  python scripts/equity/snoozer_windows.py [--cache PATH] [--side both]
"""
import argparse

import duckdb
import pandas as pd

pd.set_option("display.width", 250)
pd.set_option("display.max_columns", 60)

ap = argparse.ArgumentParser()
ap.add_argument("--cache", default="data/equity/flushfader/snoozer_cache.parquet")
ap.add_argument("--side", choices=["long", "short", "both"], default="both")
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "8GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
C = f"read_parquet('{args.cache}')"
con.execute(f"CREATE OR REPLACE TEMP TABLE X AS SELECT *, year(date) AS yr FROM {C}")
N = con.execute("SELECT count(*) FROM X").fetchone()[0]
yrs = [r[0] for r in con.execute("SELECT DISTINCT yr FROM X ORDER BY 1").fetchall()]
print(f"population: {N:,} universe ticker-days with a next-session open   "
      f"years {yrs[0]}-{yrs[-1]}\n")

WINDOWS = [("60m", "chg60", "nb60", 3600), ("30m", "chg30", "nb30", 1800),
           ("15m", "chg15", "nb15", 900), ("5m", "chg05", "nb05", 300)]

# the same absolute edges on every window, so the SHIFT in where mass lands is
# itself visible — §3 then removes the threshold from the comparison entirely.
LONG_EDGES = [(-1e9, -0.06), (-0.06, -0.04), (-0.04, -0.03), (-0.03, -0.02),
              (-0.02, -0.01), (-0.01, -0.005), (-0.005, 0.005)]
SHORT_EDGES = [(0.005, 0.02), (0.02, 0.04), (0.04, 0.06), (0.06, 1e9)]


def label(lo, hi):
    f = lambda v: ("-inf" if v < -1e8 else "+inf" if v > 1e8 else f"{v*100:g}")
    return f"[{f(lo)}, {f(hi)})"


def band_table(col, edges, title):
    print(f"\n{'='*190}\n{title}\n"
          f"median overnight %% (win%%) — n, then MEDIAN by year\n{'='*190}")
    rows = []
    for lo, hi in edges:
        w = f"{col} >= {lo} AND {col} < {hi}"
        d = con.execute(f"""SELECT count(*) n, median(ovn_from_close)*100 med,
            avg(CASE WHEN ovn_from_close>0 THEN 1.0 ELSE 0 END)*100 w
            FROM X WHERE {w}""").fetchdf()
        r = {"band": label(lo, hi), "n": f"{int(d.n[0]):,}",
             "med%": f"{d.med[0]:+.3f}" if int(d.n[0]) else ".",
             "win%": f"{d.w[0]:.1f}" if int(d.n[0]) else "."}
        for y in yrs:
            e = con.execute(f"""SELECT count(*) n, median(ovn_from_close)*100 m
                FROM X WHERE {w} AND yr = {y}""").fetchdf()
            r[str(y)] = f"{e.m[0]:+.2f}" if int(e.n[0]) >= 20 else "."
        rows.append(r)
    print(pd.DataFrame(rows).to_string(index=False))
    print("  ('.' = fewer than 20 observations in that cell)")


if args.side in ("long", "both"):
    for nm, col, _, _ in WINDOWS:
        band_table(col, LONG_EDGES,
                   f"⭐ LONG SIDE — buy after a last-{nm} DECLINE  (`{col}`, measured to 16:00)")
if args.side in ("short", "both"):
    for nm, col, _, _ in WINDOWS:
        band_table(col, SHORT_EDGES,
                   f"SHORT SIDE — sell after a last-{nm} RALLY  (`{col}`, measured to 16:00)")

print(f"\n{'='*190}\n⭐⭐ §3 THE HONEST COMPARISON — MATCHED SELECTIVITY\n"
      f"the n most extreme ticker-days by each window, so the threshold is removed\n"
      f"from the comparison entirely. Same n, same population, three signals.\n{'='*190}")
for side, order, lbl in (("long", "ASC", "most NEGATIVE (buy)"),
                         ("short", "DESC", "most POSITIVE (sell)")):
    if args.side not in (side, "both"):
        continue
    print(f"\n--- {lbl} ---")
    rows = []
    for n_sel in (500, 1000, 2500, 5000, 10000, 25000):
        r = {"n selected": f"{n_sel:,}"}
        for nm, col, _, _ in WINDOWS:
            d = con.execute(f"""SELECT median(ovn_from_close)*100 med,
                avg(CASE WHEN ovn_from_close>0 THEN 1.0 ELSE 0 END)*100 w,
                min({col})*100 lo, max({col})*100 hi
                FROM (SELECT * FROM X WHERE {col} IS NOT NULL
                      ORDER BY {col} {order} LIMIT {n_sel})""").fetchdf()
            r[f"{nm} med%"] = f"{d.med[0]:+.3f}"
            r[f"{nm} win%"] = f"{d.w[0]:.0f}"
            r[f"{nm} cut"] = (f"{d.hi[0]:.1f}%" if side == "long" else f"{d.lo[0]:.1f}%")
        rows.append(r)
    print(pd.DataFrame(rows).to_string(index=False))
    print("  'cut' = the threshold that selection implies on that window's own scale.")

print(f"\n{'='*190}\n⭐⭐ §3b THE TRADEABLE VERSION — signal knowable at the decision time,\n"
      f"filled by a LIMIT resting in the window that follows it. Matched selectivity.\n"
      f"  k59 = decide 15:59, fill 15:59-16:00 (user 2026-08-14: 'limit entries in the last minute')\n"
      f"  k   = decide 15:57, fill 15:57-16:00      close = the 16:00 signal, unTRADEABLE (upper bound)\n{'='*190}")
LADDER = [("close(untradeable)", "chg{}", "ovn_from_close"),
          ("k57 -> lim 15:57-16:00", "chg{}k", "ovn_from_lim57"),
          ("⭐ k59 -> lim 15:59-16:00", "chg{}k59", "ovn_from_lim59")]
for side, order, lbl in (("long", "ASC", "most NEGATIVE (buy)"),
                         ("short", "DESC", "most POSITIVE (sell)")):
    if args.side not in (side, "both"):
        continue
    print(f"\n--- {lbl} — median overnight %, at n = 2,500 and 10,000 ---")
    rows = []
    for entry_lbl, ctmpl, ret in LADDER:
        for n_sel in (2500, 10000):
            r = {"entry": entry_lbl, "n": f"{n_sel:,}"}
            for nm, col, _, _ in WINDOWS:
                c = ctmpl.format(nm.replace("m", "").rjust(2, "0"))
                d = con.execute(f"""SELECT median({ret})*100 med,
                    avg(CASE WHEN {ret}>0 THEN 1.0 ELSE 0 END)*100 w
                    FROM (SELECT * FROM X
                          WHERE {c} IS NOT NULL AND {ret} IS NOT NULL
                          ORDER BY {c} {order} LIMIT {n_sel})""").fetchdf()
                r[f"{nm} med%"] = f"{d.med[0]:+.3f}"
                r[f"{nm} win%"] = f"{d.w[0]:.0f}"
            rows.append(r)
    print(pd.DataFrame(rows).to_string(index=False))
    print("  ⚠ the `close` row is an UPPER BOUND, not a strategy — its signal uses the\n"
          "     16:00 print, which no order placed before 16:00 can condition on.")

print(f"\n=== §3c can the last-minute order fill at all? ===")
# ⚠ Report this INSIDE the gated population, not across the whole universe. The
# spec only ever trades continuously-traded names (`gaps <= 760 of 3420`), and the
# long-side edge IS that liquidity effect — liquid +1.04% at 59.6% win vs thin
# -0.79% at 37.3% (S43bt). Quoting a universe-wide no-fill rate would import the
# thin tail the strategy never touches and overstate the risk. The residual worth
# measuring is that last-HOUR density does not guarantee last-MINUTE density.
for pop, where in (("whole universe", "TRUE"),
                   ("gated: gaps<=760 of 3420 (the spec's own door)",
                    "(3420 - nb60k59) <= 760")):
    d = con.execute(f"""SELECT count(*) n,
        count(*) FILTER (px_lim_1559_1600 IS NULL) AS no_fill,
        round(100.0*count(*) FILTER (px_lim_1559_1600 IS NULL)/count(*), 3) AS pct_no_fill,
        round(median(nb_lastmin)) AS med_secs_of_60,
        round(quantile_cont(nb_lastmin, 0.10)) AS p10_secs,
        round(quantile_cont(nb_lastmin, 0.01)) AS p01_secs,
        round(100.0*avg(CASE WHEN px_lim_1559_1600 < close_d THEN 1.0 ELSE 0 END), 1)
          AS pct_fill_below_close
        FROM X WHERE {where}""").fetchdf()
    d.insert(0, "population", pop)
    print(d.to_string(index=False))
print("  pct_fill_below_close: the limit's edge over the auction — sellers hitting")
print("  bids into the close. S43bv measured 51.8% on the old 15:57-15:59 window.")

print(f"\n{'='*190}\n§4 DENSITY — does the LongSnoozer gap filter still bite per window?\n"
      f"gaps = seconds of the window that did NOT trade. Long side only.\n{'='*190}")
if args.side in ("long", "both"):
    rows = []
    for nm, col, nb, secs in WINDOWS:
        for q in (0.25, 0.50, 0.75, 1.00):
            thr = con.execute(
                f"SELECT quantile_cont({secs} - {nb}, {q}) FROM X").fetchone()[0]
            d = con.execute(f"""SELECT count(*) n, median(ovn_from_close)*100 med,
                avg(CASE WHEN ovn_from_close>0 THEN 1.0 ELSE 0 END)*100 w
                FROM (SELECT * FROM X WHERE {col} IS NOT NULL AND {secs}-{nb} <= {thr}
                      ORDER BY {col} ASC LIMIT 5000)""").fetchdf()
            rows.append({"window": nm, "gap cut": f"<= {int(thr)} of {secs}",
                         "kept %": f"{q*100:.0f}%", "n": int(d.n[0]),
                         "med%": f"{d.med[0]:+.3f}", "win%": f"{d.w[0]:.0f}"})
    print(pd.DataFrame(rows).to_string(index=False))
    print("  (within each row: the 5,000 most negative ticker-days on that window,\n"
          "   AFTER restricting to the densest X% of the universe)")
