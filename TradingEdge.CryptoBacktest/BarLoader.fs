module TradingEdge.CryptoBacktest.BarLoader

open System
open System.IO
open DuckDB.NET.Data
open TradingEdge.CryptoBacktest.SignedBar

// =============================================================================
// Pre-aggregated bar parquet loader
// =============================================================================
//
// CryptoData's build-bars subcommand writes one parquet per (symbol,
// timeframe) at {root}/{tf}/{SYMBOL}.parquet. Schema (see BarPreprocess.fs):
//
//   start_us BIGINT, end_us BIGINT,
//   open/high/low/close DOUBLE, volume DOUBLE, vwap DOUBLE,
//   vol_weighted_std DOUBLE, buy_dollar_volume DOUBLE, sell_dollar_volume DOUBLE,
//   trade_count INTEGER
//
// Loading 17k bars is dramatically faster than streaming 7M trades/day from
// the per-day parquets and re-aggregating, so the backtester prefers this
// path when the bars are present.

let barPath (root: string) (timeframe: string) (symbol: string) : string =
    Path.Combine(root, timeframe, sprintf "%s.parquet" symbol)

/// True if there's a bar parquet on disk for this (symbol, timeframe).
let exists (root: string) (timeframe: string) (symbol: string) : bool =
    File.Exists(barPath root timeframe symbol)

/// Load all bars for one symbol-timeframe, in start_us order. Filters by
/// [startUs, endUs] inclusive on the bucket start; pass int64 bounds (use
/// MinValue / MaxValue for unbounded).
let loadRange
    (root: string)
    (timeframe: string)
    (symbol: string)
    (startUs: int64)
    (endUs: int64)
    : SignedBar[] =
    let path = barPath root timeframe symbol
    if not (File.Exists path) then [||]
    else
        let normalized = path.Replace('\\', '/').Replace("'", "''")
        use conn = new DuckDBConnection("Data Source=:memory:")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            sprintf
                "SELECT start_us, end_us, open, high, low, close, volume, vwap,
                        vol_weighted_std, buy_dollar_volume, sell_dollar_volume, trade_count
                 FROM read_parquet('%s')
                 WHERE start_us BETWEEN %d AND %d
                 ORDER BY start_us"
                normalized startUs endUs
        use reader = cmd.ExecuteReader()
        let result = ResizeArray<SignedBar>(capacity = 32_768)
        while reader.Read() do
            result.Add {
                StartUs = reader.GetInt64 0
                EndUs = reader.GetInt64 1
                Open = reader.GetDouble 2
                High = reader.GetDouble 3
                Low = reader.GetDouble 4
                Close = reader.GetDouble 5
                Volume = reader.GetDouble 6
                VWAP = reader.GetDouble 7
                VolWeightedStdDev = reader.GetDouble 8
                BuyDollarVolume = reader.GetDouble 9
                SellDollarVolume = reader.GetDouble 10
                TradeCount = reader.GetInt32 11
            }
        result.ToArray()

/// Convert YYYY-MM-DD to a microsecond timestamp anchored at UTC midnight.
let dateToUs (d: DateTime) : int64 =
    let dt = DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)
    DateTimeOffset(dt).ToUnixTimeMilliseconds() * 1000L

/// Convenience: load by date range (inclusive).
let loadByDate
    (root: string)
    (timeframe: string)
    (symbol: string)
    (startDate: DateTime)
    (endDate: DateTime)
    : SignedBar[] =
    // endDate inclusive → bucket starts up to (endDate + 1 day - 1us).
    let lo = dateToUs startDate
    let hi = dateToUs (endDate.AddDays 1.0) - 1L
    loadRange root timeframe symbol lo hi

/// List all symbols present at this (timeframe).
let listSymbols (root: string) (timeframe: string) : string[] =
    let dir = Path.Combine(root, timeframe)
    if not (Directory.Exists dir) then [||]
    else
        Directory.EnumerateFiles(dir, "*.parquet")
        |> Seq.map Path.GetFileNameWithoutExtension
        |> Seq.toArray
        |> Array.sort
