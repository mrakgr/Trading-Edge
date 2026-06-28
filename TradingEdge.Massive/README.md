# TradingEdge.Massive

The **data-download library** for the TradingEdge equity stack: it fetches stock-market
data from the Massive S3 flat-files and the Polygon REST API and writes it to raw files
on disk (CSV / JSON / Parquet). It performs **no** database work.

The DuckDB side (schema, ingest, materialize, query) and the command-line entry point
live in **[TradingEdge.Database](../TradingEdge.Database/README.md)**, which references
this library. So although the download *code* lives here, you invoke the download
commands through the `TradingEdge.Database` CLI:

```bash
dotnet run --project TradingEdge.Database -- <download-command> [options]
```

## Prerequisites

- .NET 10.0 SDK
- `api_key.json` in the repo root with your Massive / Polygon credentials:

```json
{
    "massive_api_key": "YOUR_API_KEY",
    "massive_s3_access_key": "YOUR_S3_ACCESS_KEY",
    "massive_s3_secret_key": "YOUR_S3_SECRET_KEY"
}
```

## Building

```bash
# As a library, it builds with the solution:
dotnet build TradingEdge.slnx

# Standalone:
dotnet build TradingEdge.Massive/TradingEdge.Massive.fsproj
```

## Download commands

All commands below are exposed by the `TradingEdge.Database` CLI (see note above).

### download-bulk — daily aggregates (whole market, from Massive S3)

```bash
dotnet run --project TradingEdge.Database -- download-bulk [options]
```
- `-s, --start-date <yyyy-MM-dd>` — start (default: 5 years ago)
- `-e, --end-date <yyyy-MM-dd>` — end (default: today)
- `-p, --parallelism <int>` — max parallel downloads (default: 10)

Output: `data/daily_aggregates/{yyyy-MM-dd}.csv.gz`.

### download-bulk-minute — 1-minute bars (whole market, from Massive S3)

Whole-market 1m OHLCV, one parquet per trading day. Two-stage pipeline (download
`.csv.gz` to an SSD staging dir → convert to parquet) so downloads and conversions
overlap. Idempotent/resumable: existing days are skipped; re-run to retry missing days.

```bash
dotnet run --project TradingEdge.Database -- download-bulk-minute [options]
```
- `-s, --start-date` (default: 2024-04-01; Massive minute history starts 2021-01-01)
- `-e, --end-date` (default: today)
- `-p, --parallelism <int>` (default: 10)
- `-cp, --convert-parallelism <int>` (default: 1; 4 keeps up with `-p 10`)
- `-o, --output-dir <path>` (default: `data/minute_aggs`)
- `-T, --temp-dir <path>` — SSD staging for in-flight `.csv.gz` (default: `~/.cache/massive_bulk_tmp`)

Output: `data/minute_aggs/{yyyy-MM-dd}.parquet` (~18 MB/day). Schema: `(ticker, ohlcv, window_start, transactions)`.

### download-bulk-trades — trade flat-files (whole market, from Massive S3)

Whole-market trades flat-files → zstd-compressed Parquet, one per trading day. Same
staging pipeline as `download-bulk-minute`.

```bash
dotnet run --project TradingEdge.Database -- download-bulk-trades [options]
```

### download-splits

```bash
dotnet run --project TradingEdge.Database -- download-splits [options]
```
- `-s/-e` date range (default start: 5 years ago; no end = all future splits)

Output: `data/splits.csv`.

### download-dividends

Polygon dividends, monthly chunked + cached (only the current month is re-fetched).

```bash
dotnet run --project TradingEdge.Database -- download-dividends [options]
```
- `-s/-e` date range (default start: 5 years ago)

Output: `data/dividends.csv` (merged) + `data/dividends_cache/{yyyy-MM}.csv`.

### download-intraday — per-ticker minute/second bars (Polygon REST)

```bash
dotnet run --project TradingEdge.Database -- download-intraday -t TICKER [options]
```
- `-t, --ticker <symbol>` — **required** (the SIP/stocks-in-play mode was removed)
- `-s, --start-date` (default: 1 week ago), `-e, --end-date`
- `-o, --output-dir <path>` (default: `data/intraday`)
- `-p, --parallelism <int>` (default: 5)
- `--timespan <minute|second>` (default: minute)

Output: `data/intraday/{timespan}/{ticker}/{date}.json`.

### download-trades — per-ticker tick-level trades (Polygon REST)

Writes zstd-compressed Parquet keeping only the columns read downstream
(`participant_timestamp, sip_timestamp, price, size, exchange, trf_id, conditions`).
The in-memory Parquet serialization uses DuckDB (`TradesDownload.writeTradesToParquet`).

```bash
dotnet run --project TradingEdge.Database -- download-trades -t TICKER -s <date> [options]
```
- `-t` required, `-s` required, `-e` optional, `-o` (default `data/trades`), `-p` (default 5)

Output: `data/trades/{ticker}/{date}.parquet`.

### download-trades-from-json — batch trades for `(ticker, date)` pairs

```bash
dotnet run --project TradingEdge.Database -- download-trades-from-json -i pairs.json [options]
```
Input JSON: array of `{"ticker": "...", "date": "yyyy-MM-dd"}` (extra fields ignored).
Existing `.parquet` files are skipped. Output: `data/trades/{ticker}/{date}.parquet`.

### download-quotes — per-ticker NBBO quotes (Polygon REST)

```bash
dotnet run --project TradingEdge.Database -- download-quotes -t TICKER -s <date> [options]
```
`-t`/`-s` required, `-e`/`-o`/`-p`/`--pretty`. Output: `data/quotes/{ticker}/{date}.json`.

### download-news — per-ticker news articles (Polygon REST)

```bash
dotnet run --project TradingEdge.Database -- download-news -t TICKER [options]
```
`-t` required; `-s`/`-e` (smart 1-week defaults); `-o`/`-p`. Output: `data/news/{ticker}/{date}.json`.

### download-tickers — ETF/ETN reference list (Polygon `/v3/reference/tickers`)

```bash
dotnet run --project TradingEdge.Database -- download-tickers [-o data/tickers.csv]
```
Then `ingest-data` loads it into the `ticker_reference` table (used to filter the universe
to CS/ADRC). The ETF universe changes slowly — refreshing quarterly is plenty.

### download-ticker-events — rename / corporate-event chains (Polygon `/vX/.../events`)

Per-ticker event chains (e.g. FB→META) anchored by `composite_figi`. Idempotent: skips
tickers already on disk; 404s are persisted and skipped on re-run.

```bash
dotnet run --project TradingEdge.Database -- download-ticker-events [options]
```
- `-d, --database <path>` — DB to source the ticker list (default `data/trading.db`; ignored if `--tickers` set)
- `-o, --output-dir <path>` (default `data/tickers/events`)
- `-p, --parallelism <n>` (default 8)
- `-t, --tickers <csv>` — explicit list (default: every ticker in `ticker_reference`)

Output: `data/tickers/events/{ticker}.json` (the durable copy; the DuckDB table is rebuilt
from these via `ingest-ticker-events`).

## Project structure

```
TradingEdge.Massive/          # download library (this project)
├── Types.fs                  # domain types (MassiveConfig, Split, Dividend, DailyPrice, ...)
├── Config.fs                 # api_key.json loading
├── S3Download.fs             # Massive S3 bulk pipeline (daily / minute / trades)
├── SplitDownload.fs          # splits API client
├── DividendDownload.fs       # dividends API client (monthly cached)
├── IntradayDownload.fs       # per-ticker minute/second bars
├── TradesDownload.fs         # tick-level trades -> Parquet (in-memory DuckDB writer)
├── QuotesDownload.fs         # NBBO quotes
├── NewsDownload.fs           # news articles
├── TickersDownload.fs        # ETF/ETN reference list
└── TickerEventsDownload.fs   # rename / corporate-event chains
```

Types.fs and Config.fs are shared: `TradingEdge.Database` consumes them across the project
reference. The dependency direction is one-way — `TradingEdge.Database → TradingEdge.Massive`
— so this library never references the DB project.
