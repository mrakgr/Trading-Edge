# TradingEdge.Database

The **DuckDB warehouse + CLI** for the TradingEdge equity stack. It owns everything on the
database side — schema creation, bulk ingest of downloaded files, the materialized derived
tables, and queries — and it is the **command-line entry point** for the whole pipeline,
including the download commands (whose code lives in
[TradingEdge.Massive](../TradingEdge.Massive/README.md)).

```
TradingEdge.Database (Exe: DB ops + CLI)
        ├──→ TradingEdge.Massive   (download library + Types/Config)
        └──→ TradingEdge.Orb       (Timezone / TradeFilters, used by build-minute-bars)
```

## Prerequisites

- .NET 10.0 SDK
- `api_key.json` in the repo root (needed only for the download commands; see the
  [Massive README](../TradingEdge.Massive/README.md)).

## Building & running

```bash
dotnet build TradingEdge.slnx
dotnet run --project TradingEdge.Database -- --help      # list all subcommands
```

The CLI exposes **both** the download commands (documented in the Massive README) and the
database commands below.

## Database schema

SQL lives under `sql/schema/` and is compiled into this assembly as embedded resources
(`Database.fs` loads them via `Assembly.GetExecutingAssembly()`):

```
sql/schema/
├── tables/                          # base tables (CREATE TABLE)
│   ├── daily_prices.sql
│   ├── splits.sql
│   ├── dividends.sql
│   ├── ticker_reference.sql
│   ├── ticker_events.sql
│   ├── intraday_prices_minute.sql
│   └── intraday_prices_second.sql
└── materialized/                    # derived tables (slow; rebuilt on ingest-data)
    ├── 01_split_adjusted_prices.sql   # split + dividend adjusted (the heavily-used one)
    └── 02_trading_calendar.sql
```

> The `materialized/` set was deliberately trimmed to these two (the only live derived
> tables). The old `session_*`, `premarket_volume_daily`, `structure_levels`,
> `stock_volume_4w` tables and the `gap_play` / `continuation_plays` views belonged to the
> retired ORB / gap-up research lineage and were removed — they made `ingest-data` slow
> (one full-scanned 13 GB of 10s parquets) for no current reader.

## Database commands

### ingest-data — load base tables + materialize

Bulk-loads downloaded daily aggregates, splits, dividends, and the ticker reference into
DuckDB, then materializes the derived tables. Each source is gated by file existence, so
you can refresh one source and re-run.

```bash
dotnet run --project TradingEdge.Database -- ingest-data [options]
```
- `-d, --database <path>` (default `data/trading.db`)
- `-c, --csv-dir <path>` (default `data/daily_aggregates`)
- `-s, --splits-file <path>` (default `data/splits.csv`)
- `--dividends-file <path>` (default `data/dividends.csv`)
- `--tickers-file <path>` (default `data/tickers.csv`)

Uses DuckDB's native CSV reader for fast bulk load; upserts splits/dividends/tickers; then
builds `split_adjusted_prices` + `trading_calendar`.

### ingest-intraday — load per-ticker intraday JSON

```bash
dotnet run --project TradingEdge.Database -- ingest-intraday [options]
```
- `-d, --database <path>` (default `data/trading.db`)
- `-i, --input-dir <path>` (default `data/intraday`)
- `--timespan <minute|second|all>` (default `all`)

Loads into `intraday_prices_minute` / `intraday_prices_second` (upsert on conflict).

### ingest-ticker-events — flatten event JSONs → table

```bash
dotnet run --project TradingEdge.Database -- ingest-ticker-events [options]
```
- `-d, --database <path>` (default `data/trading.db`)
- `-i, --input-dir <path>` (default `data/tickers/events`)
- `-o, --output-parquet <path>` (default `data/tickers/events.parquet`)

Flattens `data/tickers/events/*.json` → a parquet → the `ticker_events` table
(truncate-and-insert; safe to re-run). The parquet is the source of truth.

### refresh-views — rebuild views only (fast)

```bash
dotnet run --project TradingEdge.Database -- refresh-views [-d data/trading.db]
```
Re-runs the `sql/schema/views/` definitions without rematerializing the derived tables.
(There are currently no live views; this is a no-op until one is added.)

### build-minute-bars — 1m bars from bulk trades (parquet → parquet)

Builds 1-minute time-bar aggregates from the bulk trade parquets — one zstd parquet per day.
Rows are `(ticker, bucket, start_ns, open, high, low, close, volume, dollar_volume, vwap,
vwstd, trade_count)`; buckets are 1-minute slots 04:00–20:00 ET (960/day, DST-correct).
Applies the canonical lit-only filter shared with `TradingEdge.Orb/TradeFilters.fs`. This is
why this project references `TradingEdge.Orb`. No persistent DB — it reads parquet and writes
parquet.

```bash
dotnet run --project TradingEdge.Database -- build-minute-bars [options]
```
- `-s, --start-date` / `-e, --end-date` (default: full input range)
- `-i, --input-dir <path>` (default `/mnt/d/trading-edge-bulk/trades`)
- `-o, --output-dir <path>` (default `data/minute_bars_1m`)
- `-p, --parallelism <int>` (default 4)
- `--force` — overwrite existing per-day output parquets

Idempotent — re-runs skip dates whose output already exists unless `--force`.

### convert-trades-to-parquet — one-shot legacy migration

Converts pre-existing `data/trades/{ticker}/{date}.json` to Parquet in place and deletes
each JSON on success. Idempotent. Only needed once for legacy data; new downloads already
write Parquet.

```bash
dotnet run --project TradingEdge.Database -- convert-trades-to-parquet [-i <input-dir>]
```

## Project structure

```
TradingEdge.Database/
├── Database.fs            # DuckDB schema (DDL/materialize) + bulk ingest + query helpers
├── MinuteBarsBuild.fs     # build-minute-bars (uses Orb Timezone/TradeFilters)
├── TickerEventsIngest.fs  # ingest-ticker-events (JSON -> parquet -> table)
├── Program.fs             # the Argu CLI (download + DB subcommands)
└── sql/schema/            # embedded SQL (tables/ + materialized/)
```

## Typical first-run order

```bash
# 1. download reference + history (download code lives in TradingEdge.Massive)
dotnet run --project TradingEdge.Database -- download-tickers
dotnet run --project TradingEdge.Database -- download-bulk      -s 2003-01-01
dotnet run --project TradingEdge.Database -- download-splits    -s 2003-01-01
dotnet run --project TradingEdge.Database -- download-dividends -s 2003-01-01

# 2. load + materialize
dotnet run --project TradingEdge.Database -- ingest-data
```
