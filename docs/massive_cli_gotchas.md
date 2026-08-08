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

## ⭐ Just use `backfill-daily`

```
dotnet run --project TradingEdge.Database -c Release -- backfill-daily
```
(or the Release binary directly). It runs the whole daily side in the right
order and forces the full range on the two destructive verbs, so the footgun
above cannot fire:

1. daily aggregates from the resume point (day after `max(date)` in
   `daily_prices`) → today — per-day files, skip-if-exists
2. splits, FULL RANGE 2003-01-01 → today+1y (end date leads the calendar
   because splits are announced ahead)
3. dividends, FULL RANGE
4. reference tickers — CS, ADRC + the ETF family, active AND delisted
5. `ingest-data` equivalent: ingest + materialize derived tables
   (`split_adjusted_prices` etc.)

Flags: `--start-date/-s`, `--end-date/-e`, `--parallelism/-p` (default 12 —
Massive throttles per connection, so this is the throughput dial),
`--skip-tickers`, `--skip-ingest`. A failed aggregate download aborts before
ingest, because a silently missing day truncates history; re-running resumes.

Only then rebuild `mr_candidate_1s` (it reads the daily-derived columns) and
run the engine.
