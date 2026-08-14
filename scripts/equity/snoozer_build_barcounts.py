"""S43cd — BAR COUNTS per intraday bucket, as a separate light scan.

⭐ USER (2026-08-14): "compare the bar counts in the first 15 and 30m with the bar
counts in the last hour... instead of comparing relative dollar volume, we'll compare
relative bar counts."

## Why this is its own script rather than more columns on `snoozer_build_shape.py`

Adding six `count(*) FILTER` aggregates to that scan **OOM-killed it twice** (13.4GB
RSS against a 15GB box, at both a 12GB and a 6GB DuckDB `memory_limit` — the limit does
not cap the parquet reader's own buffers). The dollar parquet it already produced is
perfectly good, so recomputing it is pure waste.

⭐ This scan reads only `ticker` + `bucket` + `filename`, NOT `vwap`/`volume`. Halving
the projection is what makes it affordable; the aggregates themselves were never the
expensive part.

## The measure

A bar = one second that traded at all, so counts measure PRESENCE where dollars measure
MAGNITUDE. Buckets match the dollar family exactly so the two are comparable:

    O5   [09:30,09:35)  O15 [09:35,09:45)  O30 [09:45,10:00)
    O60  [10:00,10:30)  O90 [10:30,11:00)  Lh  (15:00,16:00]

⚠ RATE-NORMALISED, unlike the dollar ratios. A bar count is hard-capped by window
length (900 seconds cannot yield more than 900 bars), so a raw ratio would be bounded
by the window ratio itself and compress at the top. Dividing each side by its own
window makes **1.0 mean "as continuous now as at the open"**. Dollars have no such cap,
which is why `dv_over_open*` is left as a plain ratio.

Output joins onto `snoozer_shape.parquet` by (ticker, date).

Usage:  python scripts/equity/snoozer_build_barcounts.py [--force]
"""
import argparse
import os
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("--bars1s", default="data/intraday_1s_slim/*.parquet")
ap.add_argument("--out", default="data/equity/flushfader/snoozer_barcounts.parquet")
ap.add_argument("--force", action="store_true")
args = ap.parse_args()

if os.path.exists(args.out) and not args.force:
    raise SystemExit(f"{args.out} exists — pass --force to rebuild")

con = duckdb.connect(config={"memory_limit": "4GB", "threads": 4})
con.execute("SET enable_progress_bar=false")
con.execute("SET preserve_insertion_order=false")   # lets the aggregate stream

t0 = time.time()
print("scanning bar counts (ticker+bucket only) ...", flush=True)
con.execute(f"""
COPY (
  SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) AS DATE) AS date,
         ticker,
         count(*) FILTER (bucket >= 34200 AND bucket < 34500) AS nbO5,
         count(*) FILTER (bucket >= 34500 AND bucket < 35100) AS nbO15,
         count(*) FILTER (bucket >= 35100 AND bucket < 36000) AS nbO30,
         count(*) FILTER (bucket >= 36000 AND bucket < 37800) AS nbO60,
         count(*) FILTER (bucket >= 37800 AND bucket < 39600) AS nbO90,
         count(*) FILTER (bucket >= 34200 AND bucket <  54000) AS nbDayRth,
         count(*) FILTER (bucket >  54000 AND bucket <= 57600) AS nbLh
  FROM read_parquet('{args.bars1s}', filename = true)
  WHERE bucket >= 34200 AND bucket <= 57600
  GROUP BY 1, 2
) TO '{args.out}' (FORMAT PARQUET, COMPRESSION ZSTD)""")
print(f"  scan+write {time.time()-t0:.0f}s -> {args.out}", flush=True)

print(con.execute(f"""SELECT count(*) n,
    round(median(nbO5+nbO15)) AS med_open15_of_900,
    round(median(nbLh))       AS med_lasthour_of_3600,
    round(median((nbLh/3600.0)/nullif((nbO5+nbO15)/900.0,0)), 3) AS med_bar_over_open15
    FROM read_parquet('{args.out}')""").fetchdf().to_string(index=False))
