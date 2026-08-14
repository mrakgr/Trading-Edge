"""S43bz — POST-MARKET spike signals, 16:00 -> 16:15 / 16:30 / 17:00.

⭐ USER QUESTION (2026-08-14): "Are stocks which are spiking [after the close] good
shorts, especially if they went up significantly during the last hour of RTH?"

This is the question the corpus rebuild was FOR. Until 2026-08-13 `intraday_1s_slim`
ended at 15:58:59, so the post-close session did not exist in this dataset at all and
the question was unanswerable rather than unanswered.

## Design

Decision times T ∈ {16:15, 16:30, 17:00}. For each:

    signal   pm{T} = vwap(last print <= T) / vwap(last print <= 16:00) - 1
    entry    LIMIT resting in the 15 minutes AFTER T (never the signal bar itself)
    exit     next session's OPEN
    density  seconds traded in (16:00, T]  — post-market tape is SPARSE, this is
             not a formality: a name with 4 prints has no measurable "spike"

⚠ THE FILL WINDOW NEVER OVERLAPS THE SIGNAL WINDOW. Signal to T, fill strictly after
T. Reusing the signal endpoint as the entry price would be a one-bar lookahead —
small in RTH, potentially huge on a 5-print post-market tape.

⚠ 16:00 IS THE AUCTION BUCKET. `p1600` is the last print at or before 16:00, which on
most names IS the closing auction print — a genuinely tradeable reference, and the
same anchor the RTH cache uses, so the two chain without a seam.

Anchors: 16:00 = 57600  16:15 = 58500  16:30 = 59400  16:45 = 60300
         17:00 = 61200  17:15 = 62100

Usage:  python scripts/equity/snoozer_build_postmarket.py [--force]
"""
import argparse
import os
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--bars1s", default="data/intraday_1s_slim/*.parquet")
ap.add_argument("--db", default="data/trading.db")
ap.add_argument("--rth", default="data/equity/flushfader/snoozer_cache.parquet")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_pm_cache.parquet")
ap.add_argument("--force", action="store_true")
args = ap.parse_args()

if os.path.exists(args.out) and not args.force:
    raise SystemExit(f"{args.out} exists — pass --force to rebuild")

con = duckdb.connect(config={"memory_limit": "12GB", "threads": 8})
con.execute("SET enable_progress_bar=false")
con.execute(f"ATTACH '{args.db}' AS db (READ_ONLY)")

t0 = time.time()
print("scanning the POST-MARKET tape over [16:00, 17:15] ...")
con.execute(f"""
CREATE OR REPLACE TABLE pm AS
SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS date,
       ticker,
       max_by(vwap, bucket) FILTER (bucket <= 57600) AS p1600,
       max_by(vwap, bucket) FILTER (bucket <= 58500) AS p1615,
       max_by(vwap, bucket) FILTER (bucket <= 59400) AS p1630,
       max_by(vwap, bucket) FILTER (bucket <= 61200) AS p1700,
       -- post-market density up to each decision time (of 900 / 1800 / 3600 s)
       count(*) FILTER (bucket > 57600 AND bucket <= 58500) AS nb_pm15,
       count(*) FILTER (bucket > 57600 AND bucket <= 59400) AS nb_pm30,
       count(*) FILTER (bucket > 57600 AND bucket <= 61200) AS nb_pm60,
       -- ⭐ FILL windows: strictly AFTER each decision time, 15 minutes each
       sum(vwap*volume) FILTER (bucket > 58500 AND bucket <= 59400)
         / nullif(sum(volume) FILTER (bucket > 58500 AND bucket <= 59400), 0) AS px_f15,
       sum(vwap*volume) FILTER (bucket > 59400 AND bucket <= 60300)
         / nullif(sum(volume) FILTER (bucket > 59400 AND bucket <= 60300), 0) AS px_f30,
       sum(vwap*volume) FILTER (bucket > 61200 AND bucket <= 62100)
         / nullif(sum(volume) FILTER (bucket > 61200 AND bucket <= 62100), 0) AS px_f60,
       count(*) FILTER (bucket > 58500 AND bucket <= 59400) AS nbf15,
       count(*) FILTER (bucket > 59400 AND bucket <= 60300) AS nbf30,
       count(*) FILTER (bucket > 61200 AND bucket <= 62100) AS nbf60,
       sum(vwap*volume) FILTER (bucket > 57600 AND bucket <= 61200) AS dv_pm60
FROM read_parquet('{args.bars1s}', filename = true)
WHERE bucket >= 57600 AND bucket <= 62100
GROUP BY 1, 2""")
print(f"  tape scan {time.time()-t0:.0f}s")

t1 = time.time()
con.execute(f"""
COPY (
  SELECT r.ticker, r.date, r.close_d, r.open_p1, r.div_p1,
         r.chg60, r.chg60k59, r.nb60k59, r.dv_0945_tape,
         p.p1600, p.p1615, p.p1630, p.p1700,
         p.nb_pm15, p.nb_pm30, p.nb_pm60, p.nbf15, p.nbf30, p.nbf60, p.dv_pm60,
         p.px_f15, p.px_f30, p.px_f60,
         -- post-market move off the 16:00 anchor
         p.p1615/nullif(p.p1600,0) - 1 AS pm15,
         p.p1630/nullif(p.p1600,0) - 1 AS pm30,
         p.p1700/nullif(p.p1600,0) - 1 AS pm60,
         -- outcome from each LIMIT fill to the next open (long-sense; negate for short)
         (r.open_p1 + r.div_p1)/nullif(p.px_f15,0) - 1 AS ovn_f15,
         (r.open_p1 + r.div_p1)/nullif(p.px_f30,0) - 1 AS ovn_f30,
         (r.open_p1 + r.div_p1)/nullif(p.px_f60,0) - 1 AS ovn_f60,
         -- reference: the same night measured from the CLOSE, so the post-market
         -- signal can be judged against doing nothing after 16:00
         (r.open_p1 + r.div_p1)/nullif(r.close_d,0) - 1 AS ovn_from_close
  FROM read_parquet('{args.rth}') r
  JOIN pm p ON p.ticker = r.ticker AND p.date = r.date
  WHERE p.p1600 IS NOT NULL
) TO '{args.out}' (FORMAT PARQUET, COMPRESSION ZSTD)""")
print(f"  join+write {time.time()-t1:.0f}s -> {args.out}")

print(con.execute(f"""SELECT count(*) n,
    count(*) FILTER (nb_pm15 > 0) AS any_tape_by_1615,
    count(*) FILTER (nb_pm60 > 0) AS any_tape_by_1700,
    round(100.0*count(*) FILTER (nb_pm60 = 0)/count(*), 1) AS pct_DEAD_postmarket,
    round(median(nb_pm60)) AS med_secs_of_3600,
    round(quantile_cont(nb_pm60, 0.90)) AS p90_secs
    FROM read_parquet('{args.out}')""").fetchdf().to_string(index=False))
print(con.execute(f"""SELECT
    count(*) FILTER (px_f15 IS NOT NULL) AS fillable_1615,
    count(*) FILTER (px_f30 IS NOT NULL) AS fillable_1630,
    count(*) FILTER (px_f60 IS NOT NULL) AS fillable_1700,
    count(*) AS total FROM read_parquet('{args.out}')""").fetchdf().to_string(index=False))
