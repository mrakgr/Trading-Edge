# Massive CLI gotchas

## ⚠ `download-splits` / `download-dividends` OVERWRITE the whole CSV — they do not merge

Both verbs write `data/splits.csv` / `data/dividends.csv` from **only the rows
returned for the requested date range**. Running them with a narrow range
DESTROYS the file's history.

Observed 2026-08-08: `download-splits --start-date 2026-06-01 --end-date
2026-12-31` replaced 27,680 rows (2003→2026) with **427 rows** (2026-06-01→
2026-12-17); the file went 828,921 → 12,846 bytes. Recovered only because the
`splits` table in `trading.db` still held the old rows and the API can be
re-queried — neither file is in git (`**/data/*` is ignored).

**Rule: always download these two FULL-RANGE** (`--start-date 2003-01-01
--end-date <today+6mo>`; splits are announced ahead, so the end date must lead
the calendar). Take a `.bak` copy first if the file is large enough that a
re-download is expensive (dividends is ~95 MB).

By contrast `download-bulk`, `download-bulk-trades` and `download-bulk-minute`
are per-day files with a skip-if-exists stage — those ARE safe to run for a
narrow range, and that is the normal way to backfill.

## Daily-pipeline order for an OOS backfill

1. `download-bulk --start-date <first missing> --end-date <today>` → per-day
   `data/daily_aggregates/{date}.csv.gz` (safe, incremental)
2. `download-splits` / `download-dividends` FULL RANGE (see above)
3. `ingest-data` → `daily_prices`, `splits`, `dividends` in `data/trading.db`
4. only then rebuild `mr_candidate_1s` (it reads the daily-derived columns) and
   run the engine.
