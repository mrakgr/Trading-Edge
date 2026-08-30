"""S43cd — does RELATIVE tape shape work on FlushFader, as it does on the Snoozers?

⭐ USER (2026-08-14): "We have the gap counts so we'll be able to convert those into
bar count percentages and compare them to the ones in the first 15m. Given how well
dollar volume features work here, we might want to compare first 15m dollar volume to
the last bar's dollar volume in rate space."

## ⚠ THE TWO CLOCKS — why this is a DECOMPOSITION, not one ratio

The trip schema measures the two quantities on DIFFERENT clocks, and mixing them
silently would be wrong:

    gap_N          WALL-CLOCK. `GapCounter(N)` keeps present seconds in (t-N, t] and
                   reports `span - count`, so bars in the last N seconds = N - gap_N.
    dollar_vol_N   PRESENT-BAR. `SumMa N` is a rolling sum over the last N bars that
                   TRADED, whose wall-clock span is longer whenever there are gaps and
                   is NOT recoverable from gap_N (different window).

So they cannot be divided into a single "$/wall-second". Kept apart they are two
orthogonal features whose PRODUCT is the total dollar-rate ratio:

    PERSISTENCE   pers_N = ((N - gap_N)/N)  /  (n_bars_1s/900)
                  how CONTINUOUS the tape is now vs in the opening 15 minutes.
                  1.0 = same continuity as the open.

    INTENSITY     inten_N = (dollar_vol_N/N)  /  (dv_0945_tape/n_bars_1s)
                  how BIG a traded second is now vs at the open — dollars per PRESENT
                  bar on both sides, so the clock is consistent within the feature.
                  1.0 = each active second moves the same dollars as at the open.

⚠ `n_bars_1s` and `dv_0945_tape` are the OPENING 15 minutes [09:30, 09:45) and live on
`mr_candidate_1s_v2`, so this joins. Both are knowable at 09:45 and every entry is at
or after 09:45, so no lookahead (CLAUDE.md R5 — the knowability clock).

⚠ `dv_0945_tape` is ALSO the universe gate (>= $2M). A feature whose denominator is
the gate is a coincidence to be aware of, not a design — it is exactly how the Snoozer
`dv_over_open` measure arose. The persistence feature does not share that term.

## Direction

FlushFader is a LONG mean-reversion system, so by analogy with LongSnoozer (dense tape
+ HIGH relative dollar activity) the expectation is that it wants HIGH on both. That is
a HYPOTHESIS: both directions are tested, and the random same-n control is what decides.

⚠ The A++ book already gates `gap_60 < 4`, which leaves persistence almost no room to
vary. The test therefore runs on the FULL four-segment trip set by default, where the
feature can actually discriminate. `--book` restricts to A++ for contrast.

Usage:
    python scripts/equity/flushfader_shape_test.py
    python scripts/equity/flushfader_shape_test.py --book
"""
import argparse
import os
import sys

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flushfader_common import raw_px_expr

pd.set_option("display.width", 230)
pd.set_option("display.max_columns", 50)

ap = argparse.ArgumentParser()
ap.add_argument("--trips", default="data/equity/flushfader/v47_spec20/trips_p*.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--book", action="store_true", help="restrict to the A++ book")
ap.add_argument("--esf", type=int, default=450)
ap.add_argument("--q", type=float, default=0.25)
args = ap.parse_args()

con = duckdb.connect(config={"memory_limit": "3GB", "threads": 4})
con.execute("SET enable_progress_bar=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")
RAWPX, SCHEMA = raw_px_expr(con, args.trips)
VOICES = ["volat_20m*1e4 >= 140",
          "(signal_vwap/first_low_vwap)*(1+d_hi_flow) - 1 < -0.28",
          "signal_vwap/sess_low - 1 >= 0.08",
          "(volat_slope_10m - volat_slope_20m)*2e4 > 12",
          "volat_slope_5m*2e4 <= -24",
          f"secs_since_first_low >= 0 AND secs_since_first_low <= {args.esf}",
          "downticks_since_uptick >= 8",
          "secs_since_halt >= 1200 AND secs_since_halt < 4800",
          "halts_today >= 1 AND secs_since_halt >= 120 AND secs_since_halt < 1200"]
voice = " OR ".join(f"COALESCE({e}, false)" for e in VOICES)
where = f"{RAWPX} >= 1 AND volat_20m >= 0.004 AND signal_sec <= 54000" + (f" AND gap_60 < 4 AND ({voice})" if args.book else "")

con.execute(f"""CREATE OR REPLACE TEMP TABLE T AS
SELECT t.symbol, t.trade_date, t.signal_sec, t.entry_sec, t.exit_sec, t.ret_exit,
       year(t.trade_date::DATE) AS yr, t.gap_60, t.gap_300, t.gap_1200,
       t.dollar_vol_60, t.dollar_vol_300, t.dollar_vol_1200,
       u.n_bars_1s, u.dv_0945_tape AS dv_open,
       -- PERSISTENCE: continuity now vs at the open (wall-clock both sides)
       ((60.0   - t.gap_60)  /   60.0) / nullif(u.n_bars_1s/900.0, 0) AS pers_60,
       ((300.0  - t.gap_300) /  300.0) / nullif(u.n_bars_1s/900.0, 0) AS pers_300,
       ((1200.0 - t.gap_1200)/ 1200.0) / nullif(u.n_bars_1s/900.0, 0) AS pers_1200,
       -- INTENSITY: dollars per PRESENT bar now vs at the open. Same clock both
       -- sides (dollar_vol_N is a SumMa over N PRESENT bars), so this isolates
       -- "how big is a traded second" and is orthogonal to persistence.
       (t.dollar_vol_60  /  60.0) / nullif(u.dv_0945_tape/u.n_bars_1s, 0) AS inten_60,
       (t.dollar_vol_300 / 300.0) / nullif(u.dv_0945_tape/u.n_bars_1s, 0) AS inten_300,
       (t.dollar_vol_1200/1200.0) / nullif(u.dv_0945_tape/u.n_bars_1s, 0) AS inten_1200,
       -- ⭐ USER VARIANT (2026-08-14): divide the open by 900 WALL-CLOCK seconds
       -- instead of by its present-bar count. Algebraically
       --     dvrate_N = inten_N x (n_bars_1s/900)
       -- i.e. intensity MULTIPLIED BY THE OPEN'S OWN DENSITY — a composite, not a
       -- pure rate, because the numerator stays present-bar. Not obviously worse:
       -- the composite may predict more than either part. Both are tested; the
       -- substitution test decides, not the algebra.
       (t.dollar_vol_60  /  60.0) / nullif(u.dv_0945_tape/900.0, 0) AS dvrate_60,
       (t.dollar_vol_300 / 300.0) / nullif(u.dv_0945_tape/900.0, 0) AS dvrate_300,
       (t.dollar_vol_1200/1200.0) / nullif(u.dv_0945_tape/900.0, 0) AS dvrate_1200
FROM read_parquet('{args.trips}') t
JOIN db.mr_candidate_1s_v2 u ON u.ticker = t.symbol AND u.date = t.trade_date::DATE
WHERE {where} AND u.n_bars_1s > 0 AND u.dv_0945_tape > 0
ORDER BY t.symbol, t.trade_date, t.signal_sec""")

# per-ticker-day mc=1, the production concurrency rule
F = con.execute("SELECT * FROM T").fetchdf()
keep, last, prev = np.zeros(len(F), bool), -1, None
key = (F.symbol + "_" + F.trade_date.astype(str)).values
for i in range(len(F)):
    if key[i] != prev:
        prev, last = key[i], -1
    if F.entry_sec.values[i] >= last:
        keep[i] = True
        last = F.exit_sec.values[i]
F = F[keep].reset_index(drop=True)
con.execute("CREATE OR REPLACE TEMP TABLE S AS SELECT * FROM F")
print(f"population {len(F):,} trades   ({'A++ book' if args.book else 'all four segments'}, "
      f"mc=1, {SCHEMA})")
print(f"  median pers_60 {F.pers_60.median():.3f}   inten_60 {F.inten_60.median():.3f}"
      f"   (1.0 = same as the opening 15 minutes)\n")

PF = ("sum(CASE WHEN r>0 THEN r ELSE 0 END) / "
      "nullif(-sum(CASE WHEN r<0 THEN r ELSE 0 END), 0)")
con.execute("CREATE OR REPLACE TEMP TABLE X AS SELECT *, ret_exit AS r FROM S")
yrs = sorted(F.yr.unique())

LEVERS = [("pers_60    continuity vs open", "pers_60"),
          ("pers_300", "pers_300"),
          ("pers_1200", "pers_1200"),
          ("inten_60   $/present-bar vs open", "inten_60"),
          ("inten_300", "inten_300"),
          ("inten_1200", "inten_1200"),
          ("dvrate_60   /900 wall-clock [user]", "dvrate_60"),
          ("dvrate_300", "dvrate_300"),
          ("dvrate_1200", "dvrate_1200"),
          ("gap_60     [incumbent, absolute]", "gap_60")]

print("=" * 175)
print(f"§1 SUBSTITUTION TEST — both directions, matched selectivity q={args.q}")
print("=" * 175)
rows = []
b = con.execute(f"SELECT count(*) n, {PF} pf, avg(r)*100 m, min(r)*100 w FROM X").fetchdf()
rows.append({"lever": "— no filter (baseline)", "dir": "", "n": f"{int(b.n[0]):,}",
             "PF": f"{b.pf[0]:.3f}", "mean%": f"{b.m[0]:+.2f}", "worst%": f"{b.w[0]:.0f}",
             "yrs<1": ""})
for lbl, e in LEVERS:
    for dirn, op, q in (("LOW", "<=", args.q), ("HIGH", ">=", 1 - args.q)):
        w = f"{e} {op} (SELECT quantile_cont({e}, {q}) FROM X) AND {e} IS NOT NULL"
        d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, min(r)*100 wo
            FROM X WHERE {w}""").fetchdf()
        if int(d.n[0]) < 30:
            continue
        neg = 0
        for y in yrs:
            v = con.execute(f"SELECT count(*) n, {PF} pf FROM X WHERE {w} AND yr={y}").fetchdf()
            if int(v.n[0]) >= 10 and pd.notna(v.pf[0]) and v.pf[0] < 1.0:
                neg += 1
        rows.append({"lever": lbl, "dir": dirn, "n": f"{int(d.n[0]):,}",
                     "PF": f"{d.pf[0]:.3f}", "mean%": f"{d.m[0]:+.2f}",
                     "worst%": f"{d.wo[0]:.0f}", "yrs<1": neg})
d = con.execute(f"""SELECT count(*) n, {PF} pf, avg(r)*100 m, min(r)*100 w
    FROM (SELECT * FROM X ORDER BY hash(symbol||trade_date)
          LIMIT (SELECT CAST(count(*)*{args.q} AS INT) FROM X))""").fetchdf()
rows.append({"lever": "⭐ RANDOM subsample (same n)", "dir": "", "n": f"{int(d.n[0]):,}",
             "PF": f"{d.pf[0]:.3f}", "mean%": f"{d.m[0]:+.2f}",
             "worst%": f"{d.w[0]:.0f}", "yrs<1": ""})
print(pd.DataFrame(rows).to_string(index=False))
print("  ⚠ anything not clearly beating the RANDOM row is arithmetic, not signal.")
