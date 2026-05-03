module TradingEdge.CryptoData.BarPreprocess

open System
open System.IO
open System.Threading
open DuckDB.NET.Data

// =============================================================================
// Trade → time-bar parquet preprocessor
// =============================================================================
//
// Reads per-day trade parquets for a symbol, builds OHLCV bars at the
// requested timeframes (1h / 2h / 4h), and writes one parquet per
// (symbol, timeframe) to disk. Backtests can then load 17 thousand bars
// instead of streaming 7 million trades per day.
//
// Per-bar schema:
//   start_us            BIGINT   bucket start (= bucketIdx * bucketUs)
//   end_us              BIGINT   timestamp of last trade in the bar
//   open                DOUBLE   first trade's price
//   high                DOUBLE
//   low                 DOUBLE
//   close               DOUBLE   last trade's price
//   volume              DOUBLE   Σ qty
//   vwap                DOUBLE   Σ(p·v) / Σv
//   vol_weighted_std    DOUBLE   sqrt(max 0 (Σ(p²v)/Σv − vwap²))
//   buy_dollar_volume   DOUBLE   Σ p·v where sign = +1
//   sell_dollar_volume  DOUBLE   Σ p·v where sign = -1
//   trade_count         INTEGER
//
// Bucket alignment: bucketIdx = trade.timestamp_us / bucketUs. Bars are
// anchored to unix-epoch boundaries so the same bucketIdx maps to the same
// wall-clock window across symbols and runs.
//
// Trades are NOT split by time bars (a trade landing at the boundary belongs
// fully to whichever bucket contains its timestamp).

/// One time-bar accumulator. Mutable; emit() materializes the closed bar
/// and resetTo() prepares the next bucket.
type private BarAccum = {
    mutable HasOpen: bool
    mutable BucketIdx: int64
    mutable StartUs: int64
    mutable EndUs: int64
    mutable Open: float
    mutable High: float
    mutable Low: float
    mutable Close: float
    mutable SumVol: float
    mutable SumPV: float
    mutable SumPPV: float
    mutable BuyDV: float
    mutable SellDV: float
    mutable TradeCount: int
}

let private newAccum () : BarAccum =
    { HasOpen = false
      BucketIdx = 0L
      StartUs = 0L
      EndUs = 0L
      Open = 0.0
      High = 0.0
      Low = 0.0
      Close = 0.0
      SumVol = 0.0
      SumPV = 0.0
      SumPPV = 0.0
      BuyDV = 0.0
      SellDV = 0.0
      TradeCount = 0 }

let private resetTo (acc: BarAccum) (idx: int64) (bucketUs: int64) (price: float) (ts: int64) =
    acc.HasOpen <- true
    acc.BucketIdx <- idx
    acc.StartUs <- idx * bucketUs
    acc.EndUs <- ts
    acc.Open <- price
    acc.High <- price
    acc.Low <- price
    acc.Close <- price
    acc.SumVol <- 0.0
    acc.SumPV <- 0.0
    acc.SumPPV <- 0.0
    acc.BuyDV <- 0.0
    acc.SellDV <- 0.0
    acc.TradeCount <- 0

/// One per-(symbol, timeframe) writer. Streams bars into a DuckDB appender
/// that backs a single parquet file. Memory footprint is one bar's worth of
/// state plus the appender's internal buffers — independent of how many
/// trades are processed.
type BarWriter(symbol: string, timeframe: string, bucketUs: int64, outputPath: string) =
    let tmpPath = outputPath + ".tmp"
    do
        if File.Exists tmpPath then File.Delete tmpPath
        Directory.CreateDirectory(Path.GetDirectoryName outputPath) |> ignore
    let conn = new DuckDBConnection("Data Source=:memory:")
    do conn.Open()
    do
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "
            CREATE TABLE bars (
                start_us BIGINT,
                end_us BIGINT,
                open DOUBLE,
                high DOUBLE,
                low DOUBLE,
                close DOUBLE,
                volume DOUBLE,
                vwap DOUBLE,
                vol_weighted_std DOUBLE,
                buy_dollar_volume DOUBLE,
                sell_dollar_volume DOUBLE,
                trade_count INTEGER
            )"
        cmd.ExecuteNonQuery() |> ignore
    let appender = conn.CreateAppender("bars")
    let acc = newAccum()
    let mutable rowCount = 0

    let emit () =
        let vwap = if acc.SumVol > 0.0 then acc.SumPV / acc.SumVol else 0.0
        let variance =
            if acc.SumVol > 0.0 then max 0.0 (acc.SumPPV / acc.SumVol - vwap * vwap)
            else 0.0
        let row = appender.CreateRow()
        row.AppendValue(Nullable acc.StartUs) |> ignore
        row.AppendValue(Nullable acc.EndUs) |> ignore
        row.AppendValue(Nullable acc.Open) |> ignore
        row.AppendValue(Nullable acc.High) |> ignore
        row.AppendValue(Nullable acc.Low) |> ignore
        row.AppendValue(Nullable acc.Close) |> ignore
        row.AppendValue(Nullable acc.SumVol) |> ignore
        row.AppendValue(Nullable vwap) |> ignore
        row.AppendValue(Nullable (sqrt variance)) |> ignore
        row.AppendValue(Nullable acc.BuyDV) |> ignore
        row.AppendValue(Nullable acc.SellDV) |> ignore
        row.AppendValue(Nullable acc.TradeCount) |> ignore
        row.EndRow()
        rowCount <- rowCount + 1

    member _.Symbol = symbol
    member _.Timeframe = timeframe
    member _.BucketUs = bucketUs
    member _.RowCount = rowCount

    /// Push one trade. May trigger an emit if this trade crosses into a new
    /// bucket. Trades within the same bucket update the running aggregates.
    member _.PushTrade(price: float, qty: float, signFloat: float, ts: int64) =
        let idx = ts / bucketUs
        if not acc.HasOpen then
            resetTo acc idx bucketUs price ts
        elif idx <> acc.BucketIdx then
            emit ()
            resetTo acc idx bucketUs price ts
        let pv = price * qty
        acc.SumVol <- acc.SumVol + qty
        acc.SumPV <- acc.SumPV + pv
        acc.SumPPV <- acc.SumPPV + price * price * qty
        if signFloat > 0.0 then acc.BuyDV <- acc.BuyDV + pv
        else acc.SellDV <- acc.SellDV + pv
        acc.TradeCount <- acc.TradeCount + 1
        if price > acc.High then acc.High <- price
        if price < acc.Low then acc.Low <- price
        acc.Close <- price
        acc.EndUs <- ts

    /// Flush the open bar (if any) and finalize the parquet file.
    member _.Close() =
        if acc.HasOpen then
            emit ()
            acc.HasOpen <- false
        appender.Close()
        let normalized = tmpPath.Replace('\\', '/').Replace("'", "''")
        use copyCmd = conn.CreateCommand()
        copyCmd.CommandText <-
            sprintf "COPY bars TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
                normalized
        copyCmd.ExecuteNonQuery() |> ignore
        conn.Dispose()
        if File.Exists outputPath then File.Delete outputPath
        File.Move(tmpPath, outputPath)

// =============================================================================
// Per-symbol orchestration
// =============================================================================

/// Convert a timeframe string to a microsecond bucket length. Same parser
/// CryptoBacktest uses; duplicated here so this module doesn't need a
/// project reference.
let bucketUsOfTimeframe (tf: string) : int64 =
    let s = tf.Trim().ToLowerInvariant()
    let v = System.Int64.Parse(s.Substring(0, s.Length - 1))
    match s.[s.Length - 1] with
    | 's' -> v * 1_000_000L
    | 'm' -> v * 60L * 1_000_000L
    | 'h' -> v * 3600L * 1_000_000L
    | 'd' -> v * 86400L * 1_000_000L
    | c   -> failwithf "bucketUsOfTimeframe: unknown unit %c in %s" c tf

let outputPath (outputRoot: string) (timeframe: string) (symbol: string) : string =
    Path.Combine(outputRoot, timeframe, sprintf "%s.parquet" symbol)

let private listDailyParquets (tradesRoot: string) (symbol: string) : string[] =
    let dir = Path.Combine(tradesRoot, symbol)
    if not (Directory.Exists dir) then [||]
    else
        Directory.EnumerateFiles(dir, sprintf "%s-trades-*.parquet" symbol)
        |> Seq.toArray
        |> Array.sort

/// Build OHLCV bars for one symbol across all requested timeframes. Streams
/// trades through a DuckDB query rather than materializing them, so memory
/// stays bounded by the per-day trade count.
let buildSymbol
    (tradesRoot: string)
    (outputRoot: string)
    (symbol: string)
    (timeframes: string[])
    (overwrite: bool)
    : Result<int64 * int[], string> =
    try
        let tfPaths = timeframes |> Array.map (fun tf -> tf, outputPath outputRoot tf symbol)
        let allDone =
            not overwrite
            && tfPaths |> Array.forall (fun (_, p) -> File.Exists p)
        if allDone then
            Ok (0L, Array.zeroCreate timeframes.Length)
        else
        let writers =
            timeframes
            |> Array.map (fun tf ->
                BarWriter(symbol, tf, bucketUsOfTimeframe tf, outputPath outputRoot tf symbol))
        let dailyFiles = listDailyParquets tradesRoot symbol
        let mutable totalTrades = 0L
        for path in dailyFiles do
            let normalized = path.Replace('\\', '/').Replace("'", "''")
            use conn = new DuckDBConnection("Data Source=:memory:")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                sprintf "SELECT price, quantity, timestamp_us, sign FROM read_parquet('%s') ORDER BY timestamp_us"
                    normalized
            use reader = cmd.ExecuteReader()
            while reader.Read() do
                let price = reader.GetDouble 0
                let qty = reader.GetDouble 1
                let ts = reader.GetInt64 2
                let sign = reader.GetDouble 3
                for w in writers do
                    w.PushTrade(price, qty, sign, ts)
                totalTrades <- totalTrades + 1L
        let counts = writers |> Array.map (fun w -> w.RowCount)
        for w in writers do w.Close()
        Ok (totalTrades, counts)
    with ex ->
        Error (sprintf "%s: %s" symbol ex.Message)
