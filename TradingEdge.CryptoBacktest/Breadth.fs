module TradingEdge.CryptoBacktest.Breadth

open System
open System.IO
open System.Collections.Concurrent
open DuckDB.NET.Data
open TDigest
open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Universe-wide breadth panel
// =============================================================================
//
// Per-(symbol, hour) signal rows from OrderflowMA.Engine, then aggregated
// across the universe to a per-hour breadth panel. The signal-decision logic
// is reused from the backtest engine — we don't recompute the buy/sell ratio
// here. The engine publishes its evaluated target (Long/Short/Flat) on every
// bar after the rolling window has filled and any active lockout has expired,
// regardless of whether a trade actually fires (gates apply to entries, not
// to the regime view).
//
// Two parquets are produced:
//   per_symbol_hour.parquet — every (symbol, hour) the engine evaluated
//   per_hour.parquet        — one row per hour, aggregated across the universe

[<Struct>]
type BreadthRow = {
    Symbol: string
    /// Bucket-start timestamp of the bar (idx * bucketUs). This is the
    /// canonical hour-key used to align across symbols — bar.EndUs varies
    /// per-symbol because it's the last-trade timestamp inside the bar.
    StartUs: int64
    /// Last-trade timestamp inside the bar. Diagnostic only; not used for
    /// alignment.
    EndUs: int64
    Target: Side                // engine.LastEvaluatedTarget
    Ratio: float                // engine.LastEvaluatedRatio (NaN before first eval)
    BuyDollarVolume: float      // bar.BuyDollarVolume
    SellDollarVolume: float     // bar.SellDollarVolume
    InTrade: bool               // engine.InTrade
}

/// Push every bar through an OrderflowMA.Engine, calling `emit` with each row.
/// The engine is configured with the v0 pinned StrategyConfig the user
/// passes in. We do NOT call Flush — there's no force-close needed here, we
/// only care about the signal trace, not P&L.
///
/// Streaming variant: rows are not materialised into an array. The caller
/// passes `emit` to consume each row (typically: write to parquet AND fold
/// into the per-hour aggregator), keeping peak memory O(1) per symbol
/// instead of O(rows). This is the load-bearing memory fix for the
/// universe-wide build, where the row count crosses 7M+.
let runSymbolStreaming
    (symbol: string)
    (cfg: StrategyConfig)
    (bars: SignedBar[])
    (emit: BreadthRow -> unit)
    : int =
    let eng = Engine(cfg)
    for i = 0 to bars.Length - 1 do
        let bar = bars.[i]
        eng.ProcessBar bar
        emit {
            Symbol = symbol
            StartUs = bar.StartUs
            EndUs = bar.EndUs
            Target = eng.LastEvaluatedTarget
            Ratio = eng.LastEvaluatedRatio
            BuyDollarVolume = bar.BuyDollarVolume
            SellDollarVolume = bar.SellDollarVolume
            InTrade = eng.InTrade
        }
    bars.Length

/// Convenience wrapper for callers that want all rows materialised (e.g. the
/// single-symbol smoke test). Delegates to runSymbolStreaming under the hood.
let runSymbol
    (symbol: string)
    (cfg: StrategyConfig)
    (bars: SignedBar[])
    : BreadthRow[] =
    let rows = ResizeArray<BreadthRow>(bars.Length)
    runSymbolStreaming symbol cfg bars rows.Add |> ignore
    rows.ToArray()

// -----------------------------------------------------------------------------
// Per-symbol-hour parquet writer
// -----------------------------------------------------------------------------

let private targetToTag (s: Side) : string =
    match s with
    | Flat -> "flat"
    | Long -> "long"
    | Short -> "short"

/// Streaming parquet writer for the per-(symbol, hour) trace. The caller
/// pushes rows in any order (typically as each symbol completes); the writer
/// holds a single in-memory DuckDB table that gets COPYed to parquet on
/// `Close()`. The DuckDB in-memory table is column-oriented so it stores
/// 7M rows × 7 columns in well under 1 GB — the prior OOM was caused by
/// holding a managed-heap row list, not the DuckDB buffer itself.
type PerSymbolHourWriter(outputPath: string) =
    let tmpPath = outputPath + ".tmp"
    do
        if File.Exists tmpPath then File.Delete tmpPath
        Directory.CreateDirectory(Path.GetDirectoryName outputPath) |> ignore
    let conn = new DuckDBConnection("Data Source=:memory:")
    do conn.Open()
    do
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "
            CREATE TABLE breadth_rows (
                symbol VARCHAR,
                start_us BIGINT,
                end_us BIGINT,
                target VARCHAR,
                ratio DOUBLE,
                buy_dollar_volume DOUBLE,
                sell_dollar_volume DOUBLE,
                in_trade BOOLEAN
            )"
        cmd.ExecuteNonQuery() |> ignore
    let appender = conn.CreateAppender("breadth_rows")
    let writeLock = obj()
    let mutable n = 0
    let mutable closed = false

    /// Append one row. Thread-safe (DuckDB appender is not, so we lock).
    member _.Add(r: BreadthRow) =
        lock writeLock (fun () ->
            let row = appender.CreateRow()
            row.AppendValue r.Symbol |> ignore
            row.AppendValue(Nullable r.StartUs) |> ignore
            row.AppendValue(Nullable r.EndUs) |> ignore
            row.AppendValue(targetToTag r.Target) |> ignore
            row.AppendValue(Nullable r.Ratio) |> ignore
            row.AppendValue(Nullable r.BuyDollarVolume) |> ignore
            row.AppendValue(Nullable r.SellDollarVolume) |> ignore
            row.AppendValue(Nullable r.InTrade) |> ignore
            row.EndRow()
            n <- n + 1)

    member _.RowCount = n

    /// Flush, COPY to parquet, atomic rename. Idempotent — calling twice
    /// (e.g. once explicitly and again via `use`-bound Dispose) is a no-op
    /// after the first call.
    member _.Close() =
        if not closed then
            closed <- true
            appender.Close()
            let normalized = tmpPath.Replace('\\', '/').Replace("'", "''")
            use copyCmd = conn.CreateCommand()
            copyCmd.CommandText <-
                sprintf "COPY breadth_rows TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
                    normalized
            copyCmd.ExecuteNonQuery() |> ignore
            conn.Dispose()
            if File.Exists outputPath then File.Delete outputPath
            File.Move(tmpPath, outputPath)

    interface IDisposable with
        member this.Dispose() = this.Close()

/// Eager batch writer (kept for the small smoke-test path).
let writePerSymbolHourParquet (rows: seq<BreadthRow>) (outputPath: string) : int =
    use w = new PerSymbolHourWriter(outputPath)
    for r in rows do w.Add r
    let n = w.RowCount
    w.Close()
    n

// -----------------------------------------------------------------------------
// Per-hour aggregator
// -----------------------------------------------------------------------------

type PerHour = {
    HourUs: int64
    NSymbols: int
    NLong: int
    NShort: int
    NFlat: int
    PctLong: float
    PctShort: float
    /// Σ buy_dollar_volume across the universe in this hour (raw, no smoothing).
    CompositeBuyVolume: float
    /// Σ sell_dollar_volume across the universe in this hour (raw).
    CompositeSellVolume: float
    /// CompositeBuyVolume - CompositeSellVolume (raw).
    CompositeSignedVolume: float
    /// T-digest CDF of CompositeSignedVolume against past+present hours.
    /// Empirically very noisy at 1h cadence — see CompositeSignedRankMa200
    /// for the smoothed view.
    CompositeSignedRank: float
    /// 200h trailing sum of CompositeBuyVolume. nan until the window has filled.
    CompositeBuyVolumeMa200: float
    /// 200h trailing sum of CompositeSellVolume. nan until filled.
    CompositeSellVolumeMa200: float
    /// CompositeBuyVolumeMa200 - CompositeSellVolumeMa200. The smoothed
    /// universe-wide net taker flow at the same 200h horizon as the
    /// orderflow signal that drives the per-symbol target. nan until filled.
    CompositeSignedVolumeMa200: float
    /// T-digest CDF of CompositeSignedVolumeMa200 against past+present
    /// (smoothed) values. The leak-free regime signal we actually want for
    /// chart overlay. nan until the window has filled.
    CompositeSignedRankMa200: float
}

// Per-hour mutable accumulator — one entry per (hour) in a thread-safe
// dictionary. Rows are folded in as they arrive from worker threads; the
// dictionary holds totals only, not the rows themselves, so memory is O(hours)
// regardless of universe size.
type private MutableHour() =
    let mutable nLong = 0
    let mutable nShort = 0
    let mutable nFlat = 0
    let mutable buyV = 0.0
    let mutable sellV = 0.0
    let lockObj = obj()
    member _.Add(target: Side, buy: float, sell: float) =
        lock lockObj (fun () ->
            match target with
            | Long -> nLong <- nLong + 1
            | Short -> nShort <- nShort + 1
            | Flat -> nFlat <- nFlat + 1
            buyV <- buyV + buy
            sellV <- sellV + sell)
    member _.Snapshot() =
        lock lockObj (fun () -> nLong, nShort, nFlat, buyV, sellV)

/// Thread-safe incremental per-hour aggregator. Workers push BreadthRows in
/// any order; the aggregator folds each into a per-hour bucket. After all
/// rows are pushed, call `Finalize` to walk the buckets in EndUs order,
/// stream them through a single MergingDigest, and emit PerHour records.
type PerHourAggregator(?maWindow: int) =
    let buckets = ConcurrentDictionary<int64, MutableHour>()
    let maWindow = defaultArg maWindow 200

    /// Add one row to the bucket for its hour. Thread-safe. Key is
    /// `r.StartUs` (the bar bucket-start), which is identical across all
    /// symbols at the same hour boundary — `EndUs` would not be (it's the
    /// last-trade timestamp inside the bar, varies per symbol).
    member _.Push(r: BreadthRow) =
        let bucket =
            buckets.GetOrAdd(r.StartUs, fun _ -> MutableHour())
        bucket.Add(r.Target, r.BuyDollarVolume, r.SellDollarVolume)

    /// Walk all hours in ascending order, compute the per-hour composite
    /// volumes, fold them through `maWindow`-hour rolling sums, and feed the
    /// smoothed signed-volume series through a separate t-digest. Two ranks
    /// are emitted: the raw single-hour rank (kept for diagnostics; user
    /// confirmed it's too noisy to be useful) and the smoothed rank against
    /// the MA-windowed series (the actual regime signal).
    ///
    /// Leak-free: t-digests are fed in time order, each hour's rank is the
    /// CDF *after* that hour's value has been added.
    member _.Finalize() : PerHour[] =
        let hours = buckets.Keys |> Seq.toArray
        Array.sortInPlace hours
        let rawDigest = MergingDigest(200.0)
        let smoothedDigest = MergingDigest(200.0)
        let buyMa = SumMa(maWindow)
        let sellMa = SumMa(maWindow)
        [| for hourUs in hours ->
            let nLong, nShort, nFlat, buyV, sellV = buckets.[hourUs].Snapshot()
            let nSymbols = nLong + nShort + nFlat
            let signedV = buyV - sellV
            rawDigest.Add signedV
            let rawRank = rawDigest.Cdf signedV

            buyMa.Push buyV
            sellMa.Push sellV
            let buyMaState = buyMa.State
            let sellMaState = sellMa.State
            let signedMa = buyMaState - sellMaState
            let smoothedRank =
                if buyMa.Count >= maWindow then
                    smoothedDigest.Add signedMa
                    smoothedDigest.Cdf signedMa
                else
                    nan
            let buyMa200 = if buyMa.Count >= maWindow then buyMaState else nan
            let sellMa200 = if sellMa.Count >= maWindow then sellMaState else nan
            let signedMa200 = if buyMa.Count >= maWindow then signedMa else nan

            {
                HourUs = hourUs
                NSymbols = nSymbols
                NLong = nLong
                NShort = nShort
                NFlat = nFlat
                PctLong = if nSymbols > 0 then float nLong / float nSymbols else 0.0
                PctShort = if nSymbols > 0 then float nShort / float nSymbols else 0.0
                CompositeBuyVolume = buyV
                CompositeSellVolume = sellV
                CompositeSignedVolume = signedV
                CompositeSignedRank = rawRank
                CompositeBuyVolumeMa200 = buyMa200
                CompositeSellVolumeMa200 = sellMa200
                CompositeSignedVolumeMa200 = signedMa200
                CompositeSignedRankMa200 = smoothedRank
            } |]

/// Eager batch wrapper (kept for the small smoke-test path).
let aggregatePerHour (rows: seq<BreadthRow>) : PerHour[] =
    let agg = PerHourAggregator()
    for r in rows do agg.Push r
    agg.Finalize()

/// Write the per-hour aggregated parquet. Atomic.
let writePerHourParquet (hours: PerHour[]) (outputPath: string) : int =
    let tmpPath = outputPath + ".tmp"
    if File.Exists tmpPath then File.Delete tmpPath
    Directory.CreateDirectory(Path.GetDirectoryName outputPath) |> ignore
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()
    do
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "
            CREATE TABLE breadth_hours (
                hour_us BIGINT,
                n_symbols INTEGER,
                n_long INTEGER,
                n_short INTEGER,
                n_flat INTEGER,
                pct_long DOUBLE,
                pct_short DOUBLE,
                composite_buy_volume DOUBLE,
                composite_sell_volume DOUBLE,
                composite_signed_volume DOUBLE,
                composite_signed_rank DOUBLE,
                composite_buy_volume_ma200 DOUBLE,
                composite_sell_volume_ma200 DOUBLE,
                composite_signed_volume_ma200 DOUBLE,
                composite_signed_rank_ma200 DOUBLE
            )"
        cmd.ExecuteNonQuery() |> ignore
    use appender = conn.CreateAppender("breadth_hours")
    for h in hours do
        let row = appender.CreateRow()
        row.AppendValue(Nullable h.HourUs) |> ignore
        row.AppendValue(Nullable h.NSymbols) |> ignore
        row.AppendValue(Nullable h.NLong) |> ignore
        row.AppendValue(Nullable h.NShort) |> ignore
        row.AppendValue(Nullable h.NFlat) |> ignore
        row.AppendValue(Nullable h.PctLong) |> ignore
        row.AppendValue(Nullable h.PctShort) |> ignore
        row.AppendValue(Nullable h.CompositeBuyVolume) |> ignore
        row.AppendValue(Nullable h.CompositeSellVolume) |> ignore
        row.AppendValue(Nullable h.CompositeSignedVolume) |> ignore
        row.AppendValue(Nullable h.CompositeSignedRank) |> ignore
        row.AppendValue(Nullable h.CompositeBuyVolumeMa200) |> ignore
        row.AppendValue(Nullable h.CompositeSellVolumeMa200) |> ignore
        row.AppendValue(Nullable h.CompositeSignedVolumeMa200) |> ignore
        row.AppendValue(Nullable h.CompositeSignedRankMa200) |> ignore
        row.EndRow()
    appender.Close()
    let normalized = tmpPath.Replace('\\', '/').Replace("'", "''")
    use copyCmd = conn.CreateCommand()
    copyCmd.CommandText <-
        sprintf "COPY breadth_hours TO '%s' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 3)"
            normalized
    copyCmd.ExecuteNonQuery() |> ignore
    if File.Exists outputPath then File.Delete outputPath
    File.Move(tmpPath, outputPath)
    hours.Length
