# Bulk Parquet → Binary Conversion Pipeline

This doc explains how a day's raw Polygon trade flat file becomes (a) a row
in DuckDB's `session_volume_4w` table and (b) a per-`(ticker, date)` binary
file under `data/trades_bin/` that the ORB pipeline reads. It's the path to
follow whenever you extend the backtestable window — e.g. building a
validation set that covers days past the current training cutoff.

## Stage map

```
data/bulk/trades/{date}.parquet      (multi-ticker, one file per day)
        │
        │  (1) scripts/conversion/build_all_10s_bars.fsx
        ▼
data/bulk/intraday_10s/{date}.parquet        (per-bar volume + trade_count)
        │
        │  (2) scripts/conversion/build_10s_cum.fsx
        ▼
data/bulk/intraday_10s_cum/{date}.parquet    (running sums, sorted by bucket)
        │
        │  (3) TradingEdge.Massive ingest-data
        ▼
DuckDB: session_daily_totals, session_volume_4w, intraday_10s_cum view
        │
        │  scripts/dataset_generation/generate_gap_up_universe.fsx  (or any setup-list emitter)
        ▼
data/gap_up_universe.json    (setup list with raw_avg_4w / txn_avg_4w / split_factor_today)
        │
        │  (4) scripts/conversion/shard_bulk_trades.fsx
        ▼
data/trades/{Ticker}/{Date}.parquet
        │
        │  (5) dotnet run --project TradingEdge.Orb -- convert
        ▼
data/trades_bin/{Ticker}/{Date}.bin           (what the backtest reads)
```

Every stage below is **resume-aware**: rerunning with a wider date window
only does the work for the days that aren't already built.

## Stage 1 — 10s bar build

`scripts/conversion/build_all_10s_bars.fsx` walks `data/bulk/trades/`,
applies the ORB live-system trade filter (size > 0, SIP–participant delta
≤ 50ms, condition-code exclusions), and buckets surviving trades into 10s
windows over the session.

```bash
dotnet fsi scripts/conversion/build_all_10s_bars.fsx -- \
    --start-date 2025-04-18 --end-date 2026-04-17
```

Output columns: `(date, ticker, bucket, volume, trade_count)` where
`bucket = 0` is the 08:30 ET 10-second window. The closing-auction minute
(15:59 / 12:59 ET) is dropped on purpose — those prints are lumpy enough
to distort RVOLs.

## Stage 2 — cumulative columns

`scripts/conversion/build_10s_cum.fsx` adds `cum_volume` and
`cum_trade_count` (running sums within `(ticker, date)`) and rewrites each
day sorted by `bucket` so DuckDB's row-group zone maps turn
`WHERE bucket = N` into a tiny scan.

```bash
dotnet fsi scripts/conversion/build_10s_cum.fsx -- \
    --start-date 2025-04-18 --end-date 2026-04-17
```

This is a view-prep step: the output is what `intraday_10s_cum` (the view
defined in `sql/schema/views/03_intraday_10s_cum.sql`) globs over at query
time.

## Stage 3 — rematerialize DB tables

The gap-up universe generator (next stage) needs three DuckDB objects that
are downstream of the bulk parquet build:

- `session_daily_totals` — per-`(ticker, date)` `session_raw_volume` and
  `session_transactions`, aggregated from `data/bulk/intraday_10s/*.parquet`.
  Defined in `sql/schema/materialized/05_session_daily_totals.sql`.
- `session_volume_4w` — 4-week rolling averages + per-day session RVOLs,
  split-adjusted. Defined in
  `sql/schema/materialized/06_session_volume_4w.sql`. Requires at least
  16 observed trading days in the lookback window; days with less history
  get NULLs (and therefore a pass-through gate downstream).
- `intraday_10s_cum` view — a thin view over the stage-2 parquets.

Plus the usual base tables — `daily_prices`, `splits`, `ticker_reference`,
`stock_volume_4w` — which must be populated for the validation window
before you materialize the session tables.

Running `ingest-data` downloads (if needed) and ingests the base tables,
then materializes all derived tables and refreshes all views:

```bash
dotnet run --project TradingEdge.Massive -- ingest-data
```

If you only need to refresh schema after a file-layout or SQL change
(tables are already populated), use `refresh-views` and rerun
`materializeTables` via `ingest-data` instead. The materializer rebuilds
all tables from scratch, so it's safe to rerun.

**Important**: if `session_volume_4w` is out of date (or absent for the
validation window), the gap-up universe generator will emit NULL 4w
averages, those become NaN in the binary header, and the threshold gate
treats the day as **pass-through** — meaning your "gated" backtest is
actually ungated. Always verify the table date range covers your window:

```sql
SELECT MIN(date), MAX(date) FROM session_volume_4w;
```

## Stage 4 — shard bulk to per-ticker parquet

`scripts/conversion/shard_bulk_trades.fsx` takes a setup list (produced by
the universe generator) and splits the single-file-per-day bulk parquet
into `data/trades/{Ticker}/{Date}.parquet` — the narrow per-ticker format
that `TradingEdge.Orb.TradeLoader` expects.

```bash
# 1. Generate the setup list (reads the DB, emits JSON).
#    --start-date / --end-date clip to a sub-window; omit them for the full
#    session_daily_totals range.
dotnet fsi scripts/dataset_generation/generate_gap_up_universe.fsx -- \
    -o data/gap_up_universe.json

# 2. Shard
dotnet fsi scripts/conversion/shard_bulk_trades.fsx -- \
    -i data/gap_up_universe.json
```

Parallelism defaults to 2 (DuckDB's own scan is multithreaded; higher
parallelism oversubscribes disk).

## Stage 5 — parquet → binary

```bash
dotnet run --project TradingEdge.Orb -- convert \
    -i data/gap_up_universe.json
```

This is the last step. It reads each per-ticker parquet, applies the same
filter + sort used at runtime, and writes
`data/trades_bin/{Ticker}/{Date}.bin`. The `raw_avg_4w`, `txn_avg_4w`, and
`split_factor_today` fields from the setup-list JSON are written into the
binary header so the runtime gate doesn't need DB lookups.

## Validation-set recipe

The full sequence for extending coverage to a new window — for example,
building the 2025-04-18 → 2026-04-17 validation set on top of training that
ends 2025-04-17:

```bash
WINDOW_START=2025-04-18
WINDOW_END=2026-04-17

# 1–2. Bulk parquet → per-bar → cumulative
dotnet fsi scripts/conversion/build_all_10s_bars.fsx -- \
    --start-date $WINDOW_START --end-date $WINDOW_END
dotnet fsi scripts/conversion/build_10s_cum.fsx -- \
    --start-date $WINDOW_START --end-date $WINDOW_END

# 3. Make sure daily_prices / splits / tickers cover the window, then rematerialize
dotnet run --project TradingEdge.Massive -- ingest-data

# 4. Emit the validation setup list (clipped to the validation window)
dotnet fsi scripts/dataset_generation/generate_gap_up_universe.fsx -- \
    --start-date $WINDOW_START --end-date $WINDOW_END \
    -o data/gap_up_universe_validation.json

# 5. Shard + convert
dotnet fsi scripts/conversion/shard_bulk_trades.fsx -- \
    -i data/gap_up_universe_validation.json
dotnet run --project TradingEdge.Orb -- convert \
    -i data/gap_up_universe_validation.json
```

## Sanity checks

- `scripts/conversion/verify_bin_conversion.fsx` — spot-checks that a
  binary round-trips the parquet it was built from.
- `SELECT MIN(date), MAX(date) FROM session_volume_4w;` — confirms the
  metadata actually covers the window you think it does.
- Pick one `(ticker, date)` and inspect the binary header's `RawAvg4w` /
  `TxnAvg4w` fields: if they're NaN for days you expected to be gated,
  the metadata stage didn't cover the day.

## Notes

- Stages 1 and 2 use the same `data/bulk/intraday_10s*` layout that the
  existing tables read, so running them for a new window *does not*
  invalidate older days — they're pure insert-by-filename.
- The `session_adj_volume` split-adjustment in stage 3 uses `splits` as of
  the current DB state. If a split landed after the binaries were built,
  the 4w averages will shift on the next materialize. The training
  binaries written before that split won't pick up the shift unless
  reconverted.
- The gap-up universe generator's `avg_dollar_volume_4w >= 25000000`
  filter comes from `stock_volume_4w`, not `session_volume_4w`. They're
  distinct tables — the former is all-session dollar volume, the latter
  is RTH-only adjusted-share volume.
