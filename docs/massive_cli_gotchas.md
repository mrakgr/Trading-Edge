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

## ⭐ The full-range files are MIRRORS — the ingest retires ids Polygon dropped (2026-09-22)

Polygon re-keys split and dividend records over time: the same event comes back
under a new `id` and the old id is never published again. Until 2026-09-22 the
ingest was an append-only upsert on `id`, so every retired id stayed in
`trading.db` on top of its replacement and the event STACKED. Found by
`validate_daily_adjusted.py` after the 09-22 backfill:

| | splits | dividends |
|---|---|---|
| rows in the database | 29,067 | 2,066,403 |
| rows in the full-range file | 28,314 | 2,066,630 |
| database ids absent from the file (retired) | 753 | 9 |
| duplicate natural keys in the database | 903 | 688 |
| duplicate natural keys inside Polygon's own file | 185 | 687 |

AAPL's 2020-08-31 4:1 sat under two ids → a 16x two-leg split → the tape
corroboration in `02_split_corrections.sql` REJECTED it → the split was not
applied at all (the table read −74% across the split day). The dividends side
was fine: its duplicates are Polygon's own, present in the file.

**The rule now** (since 2026-10-21 `Database.fs` `replaceFromCsv`, called by both CSV ingests; until then
`retireAbsentIds` + an upsert, the same rows): the table is **rebuilt from the file** on every ingest — dropped,
recreated from `sql/schema/tables/<table>.sql`, filled, in one transaction (a failed load keeps the old table; tested
with a duplicate id) — as the v2 build does. The log says how many ids it retired (`N retired (absent from the
file)`). ⚠ Because the two
download verbs rewrite the file whole, a narrow-range file would make this
delete the table's history — so retiring more than 10% of a table is REFUSED
with a message pointing here. `backfill-daily` always fetches the full range.
After the fix the inventory returned to exactly the 53-split baseline and the
validator was re-pinned (SHIFT 31 / REJECT 138; Polygon had corrected BMI's 2016
split date itself).

## ⚠ The dividends API can SILENTLY return an incomplete current month (2026-10-21)

For a window of at least ~15 minutes (seen 08:51–09:04 UTC, gone by 09:17; a run at 08:15 was complete), different query
shapes for the current month returned DIFFERENT result sets, each paginating to the end with no error:

| query | records | dated 10-02 | dated 10-21 |
|---|---|---|---|
| `ex_dividend_date.gte=2026-10-01&.lte=2026-10-31` (default: `sort=ex_dividend_date&order=desc`) | 6,396 | **346** | 127 |
| the same + `order=asc` | 6,496 | 446 | 127 |
| `ex_dividend_date=2026-10-02` | 446 | 446 | — |
| `ex_dividend_date=2026-10-21` | **32** | — | 32 |

The desc range lost a contiguous block of tickers at a page boundary (page 5 ended at VVBVX, page 6 began at RGISX:
ROP, RSG, SATA, SEMY, … skipped) — the same in three tries minutes apart. The pages are OFFSET-based (the cursor
decodes to `ap=1000, 2000, …`) and sorted on `ex_dividend_date` alone, a non-unique key, so a result set that changes
under the reader (likely the vendor's daily load: today's 10-21 records were arriving) can skip rows. Past months agree
in every order and with our cache (sampled 2008 … 2026-09). The ingest's mirror rule then RETIRED the skipped ids
until the next run.

**Mitigation (`DividendDownload.downloadDateRange`): a month that is downloaded (= not cached: the current month and
the future) is fetched in BOTH orders and merged by `id`** — it recovered all 100 in that window, but it is a mitigation,
not a guarantee; a disagreement prints
`⚠ dividends <from>..<to>: desc N, asc M, merged K`. Splits page the same way but were checked complete the same day
(full range, desc = asc = our file, 28,648 ids).

**⭐ Since 2026-10-31 (user) both — dividends per month, splits over the whole range — are fetched in BOTH orders and
accepted only when the two return the same ids**; a disagreement fetches both again (30 s apart, 10 attempts), then
fails the download and leaves the file untouched. The dividends' carry-over of current-month records the download lacked
is gone: it carried PUTKF's revised dividend (10-08 → 11-09, the same id) back to its old month, a duplicate id that
stopped the ingest and the v2 build (VPS 10-29, home 10-31).

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
