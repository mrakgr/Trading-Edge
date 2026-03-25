# TradingEdge.Massive

A trading data analysis library for downloading and processing stock market data from Massive.

## Prerequisites

- .NET 9.0 SDK
- `api_key.json` file in the project root with your Massive API credentials:

```json
{
    "massive_api_key": "YOUR_API_KEY",
    "massive_s3_access_key": "YOUR_S3_ACCESS_KEY",
    "massive_s3_secret_key": "YOUR_S3_SECRET_KEY"
}
```

## Building

```bash
dotnet build
```

## CLI Commands

### Download Daily Aggregates

Downloads daily OHLCV data from Massive S3 storage as compressed CSV files.

```bash
dotnet run --project TradingEdge.Massive -- download-bulk [options]
```

**Options:**
- `-s, --start-date <yyyy-MM-dd>` - Start date (default: 5 years ago)
- `-e, --end-date <yyyy-MM-dd>` - End date (default: today)
- `-p, --parallelism <int>` - Max parallel downloads (default: 10)

**Examples:**

```bash
# Download last 5 years of data
dotnet run --project TradingEdge.Massive -- download-bulk

# Download specific date range
dotnet run --project TradingEdge.Massive -- download-bulk -s 2024-01-01 -e 2024-12-11

# Download with lower parallelism
dotnet run --project TradingEdge.Massive -- download-bulk -s 2024-12-01 -e 2024-12-11 -p 4
```

Output: `data/daily_aggregates/{yyyy-MM-dd}.csv.gz`

### Download Stock Splits

Downloads stock split information from the Massive API.

```bash
dotnet run --project TradingEdge.Massive -- download-splits [options]
```

**Options:**
- `-s, --start-date <yyyy-MM-dd>` - Start date (default: 5 years ago)
- `-e, --end-date <yyyy-MM-dd>` - End date (default: none, includes all future splits)

**Examples:**

```bash
# Download all splits from the last 5 years
dotnet run --project TradingEdge.Massive -- download-splits

# Download splits for a specific date range
dotnet run --project TradingEdge.Massive -- download-splits -s 2024-01-01 -e 2024-12-11

# Download splits from a date onwards (no end date)
dotnet run --project TradingEdge.Massive -- download-splits -s 2024-01-01
```

Output: `data/splits.csv`

### Download Dividends

Downloads dividend information from the Polygon API. Uses monthly chunking with full parallelism and caches completed months locally.

```bash
dotnet run --project TradingEdge.Massive -- download-dividends [options]
```

**Options:**
- `-s, --start-date <yyyy-MM-dd>` - Start date (default: 5 years ago)
- `-e, --end-date <yyyy-MM-dd>` - End date (default: none, includes all future)

**Examples:**

```bash
# Download all dividends from the last 5 years
dotnet run --project TradingEdge.Massive -- download-dividends

# Download dividends for a specific date range
dotnet run --project TradingEdge.Massive -- download-dividends -s 2024-01-01 -e 2024-12-31
```

Output: `data/dividends.csv` (merged), `data/dividends_cache/{yyyy-MM}.csv` (per-month cache)

**Features:**
- Downloads all months in parallel for maximum speed (~14s for 5 years)
- Caches completed months (only current month is re-fetched on subsequent runs)
- Second run takes ~6s (reads cache + fetches current month only)

### Download Intraday Data

Downloads intraday (minute or second) aggregate bars for specific tickers via the Polygon REST API. Can download for individual tickers or automatically fetch data for all stocks in play.

```bash
dotnet run --project TradingEdge.Massive -- download-intraday [options]
```

**Options:**
- `-t, --ticker <symbol>` - Stock ticker symbol (use with date range)
- `-s, --start-date <yyyy-MM-dd>` - Start date (default: 1 week ago)
- `-e, --end-date <yyyy-MM-dd>` - End date. If omitted with -t, only start date is downloaded
- `-d, --database <path>` - DuckDB database path for SIP lookup (default: data/trading.db)
- `-o, --output-dir <path>` - Output directory (default: data/intraday)
- `-p, --parallelism <int>` - Max parallel downloads (default: 5)
- `--timespan <string>` - Aggregate timespan: 'minute' or 'second' (default: minute)
- `--from-sip` - Download for all stocks in play from database
- `-r, --min-rvol <float>` - Min RVOL filter for SIP (default: 3)
- `-g, --min-gap-pct <float>` - Min gap % for SIP (default: 0.05)
- `-v, --min-dollar-volume <float>` - Min avg dollar volume in millions for SIP (default: 100)

**Examples:**

```bash
# Download minute data for a specific ticker
dotnet run --project TradingEdge.Massive -- download-intraday -t NVDA -s 2024-12-01 -e 2024-12-11

# Download second aggregates for a ticker
dotnet run --project TradingEdge.Massive -- download-intraday -t AAPL -s 2024-12-10 --timespan second

# Download minute data for all stocks in play (from database)
dotnet run --project TradingEdge.Massive -- download-intraday --from-sip -s 2024-12-01 -e 2024-12-11

# Download for SIPs with custom filters
dotnet run --project TradingEdge.Massive -- download-intraday --from-sip -r 5 -g 0.10 -s 2024-12-01
```

Output: `data/intraday/{timespan}/{ticker}/{date}.json`

### Download Trades Data

Downloads tick-level trades data for a specific ticker via the Polygon REST API. Each trade record includes price, size, exchange, conditions, and precise timestamps.

```bash
dotnet run --project TradingEdge.Massive -- download-trades [options]
```

**Options:**
- `-t, --ticker <symbol>` - Stock ticker symbol (required)
- `-s, --start-date <yyyy-MM-dd>` - Start date (required)
- `-e, --end-date <yyyy-MM-dd>` - End date. If omitted, only start date is downloaded
- `-o, --output-dir <path>` - Output directory (default: data/trades)
- `-p, --parallelism <int>` - Max parallel downloads (default: 5)
- `--pretty` - Output JSON with indentation (pretty print)

**Examples:**

```bash
# Download trades for a single day
dotnet run --project TradingEdge.Massive -- download-trades -t NVDA -s 2024-12-20

# Download trades for a date range
dotnet run --project TradingEdge.Massive -- download-trades -t NVDA -s 2024-12-15 -e 2024-12-20

# Download with custom output directory
dotnet run --project TradingEdge.Massive -- download-trades -t AAPL -s 2024-12-20 -o data/my_trades
```

Output: `data/trades/{ticker}/{date}.json`

### Download Quotes Data

Downloads NBBO (National Best Bid and Offer) quotes data for a specific ticker via the Polygon REST API. Each quote record includes bid/ask prices, sizes, exchanges, and timestamps.

```bash
dotnet run --project TradingEdge.Massive -- download-quotes [options]
```

**Options:**
- `-t, --ticker <symbol>` - Stock ticker symbol (required)
- `-s, --start-date <yyyy-MM-dd>` - Start date (required)
- `-e, --end-date <yyyy-MM-dd>` - End date. If omitted, only start date is downloaded
- `-o, --output-dir <path>` - Output directory (default: data/quotes)
- `-p, --parallelism <int>` - Max parallel downloads (default: 5)
- `--pretty` - Output JSON with indentation (pretty print)

**Examples:**

```bash
# Download quotes for a single day
dotnet run --project TradingEdge.Massive -- download-quotes -t NVDA -s 2024-12-20

# Download quotes for a date range
dotnet run --project TradingEdge.Massive -- download-quotes -t NVDA -s 2024-12-15 -e 2024-12-20

# Download with pretty-printed JSON
dotnet run --project TradingEdge.Massive -- download-quotes -t AAPL -s 2024-12-20 --pretty
```

Output: `data/quotes/{ticker}/{date}.json`

### Download News Articles

Downloads news articles for a specific ticker via the Polygon REST API. Each article includes title, description, URL, publication date, sentiment analysis, and publisher information.

```bash
dotnet run --project TradingEdge.Massive -- download-news [options]
```

**Options:**
- `-t, --ticker <symbol>` - Stock ticker symbol (required)
- `-s, --start-date <yyyy-MM-dd>` - Start date. If omitted with -e, defaults to 1 week before end date
- `-e, --end-date <yyyy-MM-dd>` - End date. If omitted with -s, defaults to start date
- `-o, --output-dir <path>` - Output directory (default: data/news)
- `-p, --parallelism <int>` - Max parallel downloads (default: 5)

**Examples:**

```bash
# Download news for a single day
dotnet run --project TradingEdge.Massive -- download-news -t BYND -s 2025-10-22

# Download news for a date range
dotnet run --project TradingEdge.Massive -- download-news -t NVDA -s 2024-12-15 -e 2024-12-20

# Download news for the week leading up to a date (start date defaults to 1 week before)
dotnet run --project TradingEdge.Massive -- download-news -t BYND -e 2025-10-22
```

Output: `data/news/{ticker}/{date}.json`

### Ingest Data

Ingests downloaded CSV files, splits, and dividends into a DuckDB database.

```bash
dotnet run --project TradingEdge.Massive -- ingest-data [options]
```

**Options:**
- `-d, --database <path>` - DuckDB database path (default: data/trading.db)
- `-c, --csv-dir <path>` - Directory containing .csv.gz files (default: data/daily_aggregates)
- `-s, --splits-file <path>` - CSV file containing splits (default: data/splits.csv)
- `--dividends-file <path>` - CSV file containing dividends (default: data/dividends.csv)

**Examples:**

```bash
# Ingest all data with defaults
dotnet run --project TradingEdge.Massive -- ingest-data

# Ingest to a custom database
dotnet run --project TradingEdge.Massive -- ingest-data -d /path/to/custom.db
```

**Features:**
- Uses DuckDB's native CSV reader for fast bulk ingestion
- Upserts splits and dividends (inserts new, updates existing) on each run
- Materializes derived tables (split-and-dividend-adjusted prices, momentum rankings, etc.)

### Ingest Intraday Data

Ingests downloaded intraday JSON files into the DuckDB database.

```bash
dotnet run --project TradingEdge.Massive -- ingest-intraday [options]
```

**Options:**
- `-d, --database <path>` - DuckDB database path (default: data/trading.db)
- `-i, --input-dir <path>` - Input directory for intraday data (default: data/intraday)
- `--timespan <string>` - Filter by timespan: 'minute', 'second', or 'all' (default: all)

**Examples:**

```bash
# Ingest all intraday data (minute and second)
dotnet run --project TradingEdge.Massive -- ingest-intraday

# Ingest only minute data
dotnet run --project TradingEdge.Massive -- ingest-intraday --timespan minute

# Ingest only second data
dotnet run --project TradingEdge.Massive -- ingest-intraday --timespan second

# Ingest from a custom directory
dotnet run --project TradingEdge.Massive -- ingest-intraday -i /path/to/intraday
```

**Features:**
- Uses DuckDB's native JSON reader with glob patterns for fast bulk ingestion
- Separate tables for minute (`intraday_prices_minute`) and second (`intraday_prices_second`) data
- Upserts on conflict (updates existing bars)

### Refresh Views

Refreshes only the SQL views without rematerializing the derived tables. Use this when you've modified view definitions but don't need to recompute the underlying materialized tables.

```bash
dotnet run --project TradingEdge.Massive -- refresh-views [options]
```

**Options:**
- `-d, --database <path>` - DuckDB database path (default: data/trading.db)

**Examples:**

```bash
# Refresh views with default database
dotnet run --project TradingEdge.Massive -- refresh-views

# Refresh views for a custom database
dotnet run --project TradingEdge.Massive -- refresh-views -d /path/to/custom.db
```

### Stocks In Play

Lists top stocks in play for a date range based on relative volume, opening gap, and liquidity.

```bash
dotnet run --project TradingEdge.Massive -- stocks-in-play [options]
```

**Options:**
- `-s, --start-date <yyyy-MM-dd>` - Start date (default: 1 week ago)
- `-e, --end-date <yyyy-MM-dd>` - End date (default: today)
- `-d, --database <path>` - DuckDB database path (default: data/trading.db)
- `-r, --min-rvol <float>` - Minimum relative volume (default: 3)
- `-g, --min-gap-pct <float>` - Minimum gap percentage as decimal (default: 0.05 for 5%)
- `-v, --min-dollar-volume <float>` - Minimum avg dollar volume in millions (default: 100)

**Examples:**

```bash
# List stocks in play for the past week (default filters)
dotnet run --project TradingEdge.Massive -- stocks-in-play

# List stocks in play for a specific date range
dotnet run --project TradingEdge.Massive -- stocks-in-play -s 2024-12-01 -e 2024-12-11

# Find stocks with higher volatility (5x RVOL, 10% gap)
dotnet run --project TradingEdge.Massive -- stocks-in-play -r 5 -g 0.10

# Include smaller-cap stocks ($50M+ avg volume instead of $100M)
dotnet run --project TradingEdge.Massive -- stocks-in-play -v 50

# Combine all filters: aggressive settings for small caps
dotnet run --project TradingEdge.Massive -- stocks-in-play -r 2 -g 0.03 -v 25
```

**Default Criteria:**
- Liquidity: $100M+ average daily dollar volume (4-week)
- Relative Volume (RVOL): >= 3x normal volume
- Opening Gap: >= 5% from previous close
- Ranked by composite score (RVOL + gap magnitude)
- Top 10 stocks per day

## Project Structure

```
TradingEdge/
├── TradingEdge.Massive/         # Core library
│   ├── Types.fs                 # Domain types
│   ├── Config.fs                # Configuration loading
│   ├── S3Download.fs            # S3 bulk download (daily aggregates)
│   ├── SplitDownload.fs         # Splits API client
│   ├── DividendDownload.fs      # Dividends API client (monthly cached)
│   ├── IntradayDownload.fs      # Intraday API client (minute/second bars)
│   ├── TradesDownload.fs        # Trades API client (tick-level data)
│   ├── QuotesDownload.fs        # Quotes API client (NBBO data)
│   ├── NewsDownload.fs          # News API client (articles with sentiment)
│   ├── Database.fs              # DuckDB database operations
│   ├── Program.fs               # CLI application
│   └── sql/schema/              # SQL schema files
│       ├── tables/              # Base tables
│       │   ├── daily_prices.sql
│       │   ├── splits.sql
│       │   └── dividends.sql
│       ├── materialized/        # Materialized tables (slow to rebuild)
│       │   ├── 01_split_adjusted_prices.sql  # Split + dividend adjusted
│       │   ├── 02_trading_calendar.sql
│       │   └── 04_stock_dollar_volume_4w.sql
│       └── views/               # Views/macros (fast to refresh)
│           ├── 09_stocks_in_play.sql
│           └── 10_trades_with_quotes.sql
├── api_key.json                 # API credentials (not in git)
└── data/                        # Downloaded data
    ├── daily_aggregates/        # Daily OHLCV CSV files
    ├── intraday/                # Intraday data (minute/second JSON)
    ├── trades/                  # Tick-level trades data (JSON)
    ├── quotes/                  # NBBO quotes data (JSON)
    ├── news/                    # News articles (JSON)
    ├── splits.csv               # Splits data
    ├── dividends.csv            # Dividends data (merged)
    ├── dividends_cache/         # Per-month dividend cache
    └── trading.db               # DuckDB database
```
