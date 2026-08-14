"""S43ca — extract the OVERNIGHT PATH (16:00 -> next 09:30) for ShortSnoozer candidates.

⭐ USER (2026-08-14): "we should also look into after hours stops... include the after
hours and next day's premarket trading as our stop period... But stops should be
conditional on tape density probably."

S43bz showed a post-market cover at 17:00 moves the worst trade -245% -> -169%. That
is progress and not enough. This extends the stop window to the FULL overnight session
and simulates real fills instead of a fixed checkpoint.

## Why a PATH and not more checkpoint columns

A stop is path-dependent: it fires at the first moment the tape trades through a level,
and that moment is what determines the fill. Endpoint columns (`pm60`, `px_f60`) cannot
express it — they answer "where was it at 17:00", not "did it ever touch +8%, and what
could I have covered at". So this pulls the actual 1s bars.

⭐ Affordable because the candidate set is TINY. Only ticker-days that could ever be a
ShortSnoozer trade need a path: `chg60k59 > +2%` is ~36k of 1.42M ticker-days (2.5%).
The scan is a filtered projection, not a full corpus read.

## The window

    day D    (16:00, 24:00)   buckets 57600 <  b            post-market
    day D+1  [00:00, 09:30)   buckets        b < 34200      overnight + premarket

⚠ D+1 is THE NEXT TRADING DAY for that ticker, derived from `daily_prices` with
`lead(date)` partitioned by ticker — NOT date + 1. Weekends, holidays and mid-history
delistings all break the naive version, and a wrong D+1 silently imports a different
session's tape.

⚠ Buckets are seconds-since-ET-midnight WITHIN their own file, so day D and day D+1
buckets are not comparable as raw numbers. `t` re-times everything to seconds since
day D's 16:00, giving one monotone clock across the whole overnight.

Output: one row per (ticker, trade_date, t) — the path — plus the trade's entry
context, so the stop simulator needs no further joins.

Usage:  python scripts/equity/snoozer_build_stoppath.py [--min-chg 0.02] [--force]
"""
import argparse
import os
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--bars1s", default="data/intraday_1s_slim/*.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--rth", default="data/equity/flushfader/snoozer_cache.parquet")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_stoppath.parquet")
ap.add_argument("--min-chg", type=float, default=0.02,
                help="candidate floor on chg60k59; 0.02 keeps ~36k ticker-days")
ap.add_argument("--force", action="store_true")
args = ap.parse_args()

if os.path.exists(args.out) and not args.force:
    raise SystemExit(f"{args.out} exists — pass --force to rebuild")

con = duckdb.connect(config={"memory_limit": "12GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")

t0 = time.time()
# ---- candidates + their true next trading day ------------------------------
con.execute(f"""
CREATE OR REPLACE TABLE cand AS
WITH nxt AS (
  SELECT ticker, date, lead(date) OVER (PARTITION BY ticker ORDER BY date) AS d1
  FROM db.daily_prices WHERE date >= DATE '2016-01-01'
)
SELECT r.ticker, r.date AS trade_date, n.d1 AS next_date,
       r.close_d, r.open_p1, r.div_p1, r.chg60k59, r.nb60k59,
       r.px_lim_1559_1600, r.vol_lh, r.dv_0945_tape
FROM read_parquet('{args.rth}') r
JOIN nxt n ON n.ticker = r.ticker AND n.date = r.date
WHERE r.chg60k59 > {args.min_chg} AND r.close_d > 0
  AND r.open_p1 IS NOT NULL AND n.d1 IS NOT NULL""")
nc = con.execute("SELECT count(*) FROM cand").fetchone()[0]
print(f"candidates (chg60k59 > {args.min_chg}): {nc:,} ticker-days")

# ---- the path: day D post-market, then day D+1 pre-open --------------------
# ⭐ `t` = seconds since day D's 16:00, so the two legs form ONE monotone clock.
#    D leg:   b - 57600            (b > 57600)          -> 0 .. 23400
#    D+1 leg: (86400 - 57600) + b  (b < 34200)          -> 28800 .. 63000
print("scanning the overnight tape ...")
con.execute(f"""
CREATE OR REPLACE TABLE path AS
WITH bars AS (
  SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS bdate,
         ticker, bucket, vwap, volume
  FROM read_parquet('{args.bars1s}', filename = true)
  WHERE (bucket > 57600) OR (bucket < 34200)
)
SELECT c.ticker, c.trade_date, b.bucket - 57600 AS t, b.vwap, b.volume
FROM cand c JOIN bars b ON b.ticker = c.ticker AND b.bdate = c.trade_date
WHERE b.bucket > 57600
UNION ALL
SELECT c.ticker, c.trade_date, 28800 + b.bucket AS t, b.vwap, b.volume
FROM cand c JOIN bars b ON b.ticker = c.ticker AND b.bdate = c.next_date
WHERE b.bucket < 34200""")
print(f"  scan {time.time()-t0:.0f}s")

t1 = time.time()
con.execute(f"""
COPY (SELECT p.ticker, p.trade_date, p.t, p.vwap, p.volume,
             c.close_d, c.open_p1, c.div_p1, c.chg60k59, c.nb60k59,
             c.px_lim_1559_1600, c.vol_lh, c.dv_0945_tape
      FROM path p JOIN cand c USING (ticker, trade_date)
      ORDER BY p.ticker, p.trade_date, p.t)
TO '{args.out}' (FORMAT PARQUET, COMPRESSION ZSTD)""")
print(f"  write {time.time()-t1:.0f}s -> {args.out}")

d = con.execute(f"""SELECT count(*) AS path_rows FROM read_parquet('{args.out}')""").fetchone()[0]
g = con.execute(f"""SELECT count(*) n, round(avg(cnt)) avg_bars, round(median(cnt)) med_bars,
    round(100.0*avg(CASE WHEN cnt = 0 THEN 1.0 ELSE 0 END),1) AS pct_empty
    FROM (SELECT ticker, trade_date, count(*) cnt FROM read_parquet('{args.out}')
          GROUP BY 1,2)""").fetchdf()
print(f"path rows: {d:,}")
print(g.to_string(index=False))
# ⚠ ticker-days with NO overnight tape at all never appear in `path`; the simulator
# must left-join from `cand` so they are held-to-open rather than silently dropped.
con.execute(f"COPY cand TO '{args.out.replace('.parquet','_cand.parquet')}' (FORMAT PARQUET)")
print(f"candidate table -> {args.out.replace('.parquet','_cand.parquet')}")
