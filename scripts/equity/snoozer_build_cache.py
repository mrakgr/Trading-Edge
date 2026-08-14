"""S43by — rebuild the Snoozer signal cache on the FULL-DAY corpus: 60m/30m/15m/5m windows.

Two reasons this has to be re-derived from scratch:

  1. ⭐ THE OLD CACHE WAS BUILT ON A TRUNCATED TAPE. `data/intraday_1s_slim` used to
     end at 15:58:59 (bucket 57539) — no 16:00 bar, and the last RTH minute (the
     day's heaviest, and exactly where a close-entry strategy fills) missing. Every
     LongSnoozer/ShortSnoozer number in the docs carries a 🛑 banner because of it.
  2. It also read `mr_candidate_1s`, the pre-causal universe. `_v2` is the one the
     engine uses now (raw prices, causal `n`/`cum_div`).

⭐ THE QUESTION (user 2026-08-14): does a SHORTER last-window signal — 30m, 15m, 5m —
beat the last hour, on either side?

## Anchors (seconds since ET midnight)

    15:00 = 54000   15:30 = 55800   15:45 = 56700
    15:55 = 57300   15:57 = 57420   15:59 = 57540   16:00 = 57600

## ⚠ THE KNOWABILITY LADDER — three decision times, not one

You cannot condition an order on tape that has not printed, so a signal measured to
16:00 is NOT tradeable. Each window therefore comes in three flavours, and each pairs
with the fill window that starts where its signal stops:

| suffix | signal runs to | fill window | 5m window really spans |
|---|---|---|---|
| (none) | 16:00 | — research only, close-to-open | 5m |
| `k`    | 15:57 | 15:57 → 16:00 (`px_lim_1557_1600`) | **2m** |
| `k59`  | 15:59 | **15:59 → 16:00** (`px_lim_1559_1600`) | **4m** |

⭐ `k59` added on user request (2026-08-14) — "let's try limit entries in the last
minute". It is the better instrument for the SHORT windows: the `k` convention keeps
each window's START anchor and moves only the endpoint back, so at 5m it amputates
3 of 5 minutes, while `k59` amputates 1 of 5. It also buys the fill the heaviest
minute of the session, which is the half of the trade the old corpus could not see
at all.

⚠ `px_lim_1557_1559` is kept ONLY to reproduce the old spec's fill — that window was
an artefact of where the truncated tape stopped, not a choice.

⚠ A fill needs tape. `nb_lastmin` records how many of the final 60 seconds actually
traded, so "could this order have filled at all?" is answerable rather than assumed.

Usage:  python scripts/equity/snoozer_build_cache.py [--out PATH] [--force]
"""
import argparse
import os
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--bars1s", default="data/intraday_1s_slim/*.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_cache.parquet")
ap.add_argument("--force", action="store_true")
args = ap.parse_args()

if os.path.exists(args.out) and not args.force:
    raise SystemExit(f"{args.out} exists — pass --force to rebuild")

con = duckdb.connect(config={"memory_limit": "12GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")

t0 = time.time()
print("scanning the 1s tape over [15:00, 16:00] ...")
con.execute(f"""
CREATE OR REPLACE TABLE lh AS
SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS date,
       ticker,
       -- window START anchors, plus the three decision-time endpoints
       max_by(vwap, bucket) FILTER (bucket <= 54000) AS p1500,
       max_by(vwap, bucket) FILTER (bucket <= 55800) AS p1530,
       max_by(vwap, bucket) FILTER (bucket <= 56700) AS p1545,
       max_by(vwap, bucket) FILTER (bucket <= 57300) AS p1555,
       max_by(vwap, bucket) FILTER (bucket <= 57420) AS p1557,
       max_by(vwap, bucket) FILTER (bucket <= 57540) AS p1559,
       max_by(vwap, bucket) FILTER (bucket <= 57600) AS p1600,
       -- density per window at each decision time: seconds that actually traded
       count(*) FILTER (bucket > 54000 AND bucket <= 57600) AS nb60,
       count(*) FILTER (bucket > 55800 AND bucket <= 57600) AS nb30,
       count(*) FILTER (bucket > 56700 AND bucket <= 57600) AS nb15,
       count(*) FILTER (bucket > 57300 AND bucket <= 57600) AS nb05,
       count(*) FILTER (bucket > 54000 AND bucket <= 57420) AS nb60k,
       count(*) FILTER (bucket > 55800 AND bucket <= 57420) AS nb30k,
       count(*) FILTER (bucket > 56700 AND bucket <= 57420) AS nb15k,
       count(*) FILTER (bucket > 57300 AND bucket <= 57420) AS nb05k,
       count(*) FILTER (bucket > 54000 AND bucket <= 57540) AS nb60k59,
       count(*) FILTER (bucket > 55800 AND bucket <= 57540) AS nb30k59,
       count(*) FILTER (bucket > 56700 AND bucket <= 57540) AS nb15k59,
       count(*) FILTER (bucket > 57300 AND bucket <= 57540) AS nb05k59,
       -- ⭐ LIMIT-ENTRY fill proxies: the vwap of the window the order rests in.
       -- Favourable by construction on the long side — sellers hit bids into the
       -- close, so the fill lands BELOW the official close about half the time
       -- (S43bv measured 51.8%). Which is the whole reason a limit beat the auction.
       sum(vwap*volume) FILTER (bucket > 57540 AND bucket <= 57600)
         / nullif(sum(volume) FILTER (bucket > 57540 AND bucket <= 57600), 0) AS px_lim_1559_1600,
       sum(vwap*volume) FILTER (bucket > 57420 AND bucket <= 57600)
         / nullif(sum(volume) FILTER (bucket > 57420 AND bucket <= 57600), 0) AS px_lim_1557_1600,
       sum(vwap*volume) FILTER (bucket > 57420 AND bucket <= 57540)
         / nullif(sum(volume) FILTER (bucket > 57420 AND bucket <= 57540), 0) AS px_lim_1557_1559,
       -- ⚠ can the order fill AT ALL? seconds traded in the final minute (of 60)
       count(*)    FILTER (bucket > 57540 AND bucket <= 57600) AS nb_lastmin,
       sum(volume) FILTER (bucket > 57540 AND bucket <= 57600) AS vol_lastmin,
       sum(volume) FILTER (bucket > 54000 AND bucket <= 57600) AS vol_lh
FROM read_parquet('{args.bars1s}', filename = true)
WHERE bucket >= 50400 AND bucket <= 57600
GROUP BY 1, 2""")
print(f"  tape scan {time.time()-t0:.0f}s")

t1 = time.time()
con.execute(f"""
COPY (
  SELECT u.ticker, u.date, u.dv_0945_tape, u.n_bars_1s,
         u.close_d, u.open_p1, u.div_p1,
         l.p1500, l.p1530, l.p1545, l.p1555, l.p1557, l.p1559, l.p1600,
         l.nb60, l.nb30, l.nb15, l.nb05,
         l.nb60k, l.nb30k, l.nb15k, l.nb05k,
         l.nb60k59, l.nb30k59, l.nb15k59, l.nb05k59,
         l.px_lim_1559_1600, l.px_lim_1557_1600, l.px_lim_1557_1559,
         l.nb_lastmin, l.vol_lastmin, l.vol_lh,
         -- CLOSE-anchored (to 16:00) — research only, NOT tradeable
         l.p1600/nullif(l.p1500,0) - 1 AS chg60,
         l.p1600/nullif(l.p1530,0) - 1 AS chg30,
         l.p1600/nullif(l.p1545,0) - 1 AS chg15,
         l.p1600/nullif(l.p1555,0) - 1 AS chg05,
         -- knowable at 15:57 (pairs with px_lim_1557_1600)
         l.p1557/nullif(l.p1500,0) - 1 AS chg60k,
         l.p1557/nullif(l.p1530,0) - 1 AS chg30k,
         l.p1557/nullif(l.p1545,0) - 1 AS chg15k,
         l.p1557/nullif(l.p1555,0) - 1 AS chg05k,      -- ⚠ 2 minutes, not 5
         -- ⭐ knowable at 15:59 (pairs with px_lim_1559_1600) — the last-minute entry
         l.p1559/nullif(l.p1500,0) - 1 AS chg60k59,
         l.p1559/nullif(l.p1530,0) - 1 AS chg30k59,
         l.p1559/nullif(l.p1545,0) - 1 AS chg15k59,
         l.p1559/nullif(l.p1555,0) - 1 AS chg05k59,    -- 4 minutes
         -- outcomes, all in day D's raw scale (S43br). `div_p1` is the dividend
         -- increment D->D+1; a dividend goes ex at D+1's OPEN so it belongs here.
         (u.open_p1 + u.div_p1)/nullif(u.close_d,0) - 1            AS ovn_from_close,
         (u.open_p1 + u.div_p1)/nullif(l.px_lim_1559_1600,0) - 1   AS ovn_from_lim59,
         (u.open_p1 + u.div_p1)/nullif(l.px_lim_1557_1600,0) - 1   AS ovn_from_lim57,
         (u.open_p1 + u.div_p1)/nullif(l.px_lim_1557_1559,0) - 1   AS ovn_from_lim5759
  FROM db.mr_candidate_1s_v2 u
  JOIN lh l ON l.ticker = u.ticker AND l.date = u.date
  WHERE u.open_p1 IS NOT NULL AND u.close_d > 0
    AND l.p1500 IS NOT NULL AND l.p1600 IS NOT NULL
) TO '{args.out}' (FORMAT PARQUET, COMPRESSION ZSTD)""")
print(f"  join+write {time.time()-t1:.0f}s -> {args.out}")

print(con.execute(f"""SELECT count(*) n, count(DISTINCT ticker) tickers,
    count(DISTINCT date) AS n_days, min(date) d0, max(date) d1
    FROM read_parquet('{args.out}')""").fetchdf().to_string(index=False))
print(con.execute(f"""SELECT
    count(*) FILTER (chg30 IS NULL) AS null_chg30,
    count(*) FILTER (chg05 IS NULL) AS null_chg05,
    count(*) FILTER (chg05k59 IS NULL) AS null_chg05k59,
    count(*) FILTER (px_lim_1559_1600 IS NULL) AS no_lastmin_fill,
    round(100.0*count(*) FILTER (px_lim_1559_1600 IS NULL)/count(*), 2) AS pct_no_fill,
    round(median(nb_lastmin)) AS med_lastmin_secs
    FROM read_parquet('{args.out}')""").fetchdf().to_string(index=False))
