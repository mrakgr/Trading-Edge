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
└── materialized/                    # derived tables (rebuilt on ingest-data)
    ├── 01_split_adjusted_prices.sql   # LEGACY back-adjusted prices (lookahead; being retired)
    ├── 02_split_corrections.sql       # splits the price tape contradicts -> SHIFT / REJECT
    └── 03_daily_adjusted.sql          # ⭐ CAUSAL forward adjustment: raw P + n + cum_div
```

> ⚠ The numeric prefixes are **load-bearing** — the folder is executed in NAME
> order (`Database.getEmbeddedSqlFromFolder` sorts), and `03_daily_adjusted`
> consumes the table `02_split_corrections` builds. Do not renumber one alone.
>
> ⭐ **`daily_adjusted` is the one to use.** `split_adjusted_prices` back-adjusts,
> so its `adj_ratio` folds in every FUTURE split — a lookahead in any gate. See
> `docs/price_adjustment.md`. Engines still read the old table; migration is in
> progress, so never mix the two in one calculation.

> Historically `split_adjusted_prices` was the sole materialized table. The old `session_*`,
> `premarket_volume_daily`, `structure_levels`, `stock_volume_4w`, `trading_calendar`
> tables and the `gap_play` / `continuation_plays` views belonged to the retired ORB /
> gap-up research lineage and were removed — they made `ingest-data` slow (one
> full-scanned 13 GB of 10s parquets) for no current reader.

## Database commands

### ⭐ backfill-daily — the whole daily side in one shot

**This is the normal way to bring the daily data up to date.** It runs the four
downloads in the required order and then ingests + materializes, so you cannot
get the order wrong or trip the overwrite footgun described below.

```bash
dotnet run --project TradingEdge.Database -c Release -- backfill-daily
```

No arguments needed: the start date defaults to **the day after `max(date)` in
`daily_prices`**, so it resumes wherever the database left off. On a fresh
database it bootstraps from the archive start, **2003-09-10**.

Steps, in order:

1. **daily aggregates** — per-day `data/daily_aggregates/{date}.csv.gz`,
   skip-if-exists, so re-running is cheap
2. **splits** — always FULL RANGE `2003-01-01 → today + 1 year` (the end date
   leads the calendar because splits are announced ahead)
3. **dividends** — always FULL RANGE
4. **reference tickers** — CS, ADRC + the ETF family (ETF/ETN/ETV/ETS), active
   *and* delisted
5. **ingest + materialize** — same work as `ingest-data`, including
   `split_adjusted_prices`

Options:
- `-s, --start-date <date>` override the resume point
- `-e, --end-date <date>` (default today; weekends/holidays skip themselves)
- `-p, --parallelism <n>` (default 12) — Massive throttles **per connection**,
  so this is the throughput dial; a single-file day cannot go faster than one
  connection allows
- `-d, --database <path>` (default `data/trading.db`)
- `--skip-tickers`, `--skip-ingest`

> ⚠ **Why steps 2 and 3 are forced full-range.** `download-splits` and
> `download-dividends` **rewrite their entire CSV** from just the rows in the
> requested window — they do not merge. On 2026-08-08 a narrow-range splits
> refresh replaced 27,680 rows (2003→2026) with 427. `backfill-daily` ignores
> `--start-date` for these two so that cannot happen. See
> [`docs/massive_cli_gotchas.md`](../docs/massive_cli_gotchas.md).

A failed aggregate download aborts **before** ingest (a silently missing day
truncates history); re-running resumes. Days older than your plan's history
entitlement answer 403 and are reported as skipped, not fatal.

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
builds `split_adjusted_prices`.

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
# everything — downloads in the right order, then ingest + materialize
dotnet run --project TradingEdge.Database -c Release -- backfill-daily
```

Re-run that same command any time to bring the data current; it resumes from
the newest date already in the database.

<details>
<summary>The equivalent by hand (only if you need one step in isolation)</summary>

```bash
# 1. download reference + history (download code lives in TradingEdge.Massive)
dotnet run --project TradingEdge.Database -- download-tickers
dotnet run --project TradingEdge.Database -- download-bulk      -s 2003-01-01
dotnet run --project TradingEdge.Database -- download-splits    -s 2003-01-01   # ⚠ FULL RANGE ONLY
dotnet run --project TradingEdge.Database -- download-dividends -s 2003-01-01   # ⚠ FULL RANGE ONLY

# 2. load + materialize
dotnet run --project TradingEdge.Database -- ingest-data
```
</details>
