module TradingEdge.Simulation.HoldDataset

// Volume-bar feature extraction + parquet I/O for the hold-detector training
// pipeline. Both synthetic (from the simulator) and real (BTC tape) trades
// flow through the same TradingEdge.Hmm.VolumeBar.VolumeBarBuilder so feature
// statistics are comparable across the two sources.

open System
open System.IO
open System.Threading.Tasks
open System.Threading.Channels
open Parquet
open Parquet.Schema
open Parquet.Data
open FSharp.Control
open TradingEdge.Simulation.BinanceLoader
open TradingEdge.Simulation.VolumeBar
open TradingEdge.Simulation.SessionDuration
open TradingEdge.Simulation.TradeGeneration
open TradingEdge.Simulation.Patterns

// =============================================================================
// Output schema
// =============================================================================
//
// One parquet row per volume bar. Row groups are per-day, so the Python
// dataset can shuffle by day while iterating bars sequentially within (matches
// the existing RowGroupSampler caching strategy in scripts/training/dataset.py).
//
//   day_id        : day index, contiguous 0..N-1
//   bar_idx       : bar position within the day, 0..bars_in_day-1
//   rel_stddev    : VolumeBar.StdDev / VolumeBar.VWAP — scale-invariant tightness
//   ret           : log(VWAP / prev_VWAP) (=0.0 for the first bar)
//   duration_sec  : (EndUs - StartUs) / 1e6
//   trade_count   : VolumeBar.TradeCount
//   is_hold       : 1 if the bar's first trade was labeled "Hold", else 0.
//                   Always 0 for real (BTC) data — column reserved for synthetic.

let datasetSchema = ParquetSchema(
    DataField<int>("day_id"),
    DataField<int>("bar_idx"),
    DataField<float>("rel_stddev"),
    DataField<float>("ret"),
    DataField<float>("duration_sec"),
    DataField<int>("trade_count"),
    DataField<int>("is_hold")
)

/// Per-bar row laid out as parallel arrays for parquet column writes.
type DayBars = {
    DayId: int
    BarIdx: int[]
    RelStdDev: float[]
    Ret: float[]
    DurationSec: float[]
    TradeCount: int[]
    IsHold: int[]
}

// =============================================================================
// Bar -> features
// =============================================================================

/// Extract per-bar features from an array of VolumeBars + their per-bar Hold
/// labels. `isHold` must be parallel to `bars` (same length); pass `[||]` of
/// the right length filled with zeros for unlabeled (real) data.
let toDayBars (dayId: int) (bars: VolumeBar[]) (isHold: int[]) : DayBars =
    let n = bars.Length
    if isHold.Length <> n then
        failwithf "toDayBars: bars (%d) and isHold (%d) must have same length" n isHold.Length
    let barIdx = Array.init n id
    let relStdDev = Array.init n (fun i ->
        let b = bars.[i]
        if b.VWAP > 0.0 then b.StdDev / b.VWAP else 0.0)
    // Log returns: log(VWAP / prev_VWAP). Symmetric in profit/loss space
    // (+100% and -50% both have |log r| = log 2), centered on 0 for no-change.
    let ret = Array.init n (fun i ->
        if i = 0 then 0.0
        else
            let prev = bars.[i - 1].VWAP
            if prev > 0.0 && bars.[i].VWAP > 0.0 then log (bars.[i].VWAP / prev) else 0.0)
    let durationSec = Array.init n (fun i ->
        let b = bars.[i]
        float (b.EndUs - b.StartUs) / 1e6)
    let tradeCount = Array.init n (fun i -> bars.[i].TradeCount)
    {
        DayId = dayId
        BarIdx = barIdx
        RelStdDev = relStdDev
        Ret = ret
        DurationSec = durationSec
        TradeCount = tradeCount
        IsHold = isHold
    }

// =============================================================================
// Trade builders: synth + BTC -> bars + per-bar Hold labels
// =============================================================================

/// Build bars from a Binance trade array (no labels available).
let barsFromBtcTrades (barVolume: float) (trades: BinanceLoader.Trade[]) : VolumeBar[] =
    buildBars barVolume trades

/// Build bars from synthetic simulator trades, carrying through a per-bar
/// Hold label derived from the FIRST trade fragment that lands in each bar
/// (matches the convention used by scripts/visualization/sim_volume.py).
///
/// Each synthetic Trade carries a Label like ["Hold"; "UptrendDay"] or
/// ["DriftFlat"; "UptrendDay"] — we test whether the head segment is "Hold".
///
/// Synthetic trades have no Sign (the simulator wasn't designed with buy/sell
/// aggression in mind), so we feed Sign=+1 throughout. trade_count is what
/// matters; buy_count is unused downstream.
///
/// Pairs each emitted bar with the Hold-or-not label of the synthetic trade
/// whose fragment opened that bar. Implemented as a wrapper around the Hmm
/// VolumeBarBuilder: between calls to builder.Process, the current bar is
/// either non-empty (we already know its first label) or empty (the next
/// trade's label will become the next bar's first label). Tracking this with
/// a single ref toggled by the onBar callback handles all three overflow
/// cases (no emit, single emit, multiple emits) without inspecting builder
/// internals.
let barsAndLabelsFromSynth (barVolume: float) (trades: Trade[]) : VolumeBar[] * int[] =
    let bars = ResizeArray<VolumeBar>()
    let isHold = ResizeArray<int>()
    let builder = VolumeBarBuilder(barVolume)

    // Label of the trade that opened the current (in-progress) bar. None when
    // the current bar is empty (just-emitted or pre-first-trade).
    let mutable barFirstLabel : string list option = None
    // Label of the trade currently being processed. Set at the top of the
    // per-trade loop; the onBar callback reads it when an emit fills bars
    // mid-trade so the *next* bar inherits this trade's label.
    let mutable currentTradeLabel : string list = []

    let onBar (bar: VolumeBar) =
        bars.Add bar
        let label = barFirstLabel |> Option.defaultValue []
        isHold.Add (match label with "Hold" :: _ -> 1 | _ -> 0)
        // The trade that just emitted this bar may still have remainder; if
        // so, the remainder lands in the next bar, whose first label is this
        // same trade's label. If there's no remainder, the next trade will
        // overwrite barFirstLabel anyway via the loop body below.
        barFirstLabel <- Some currentTradeLabel

    for t in trades do
        currentTradeLabel <- t.Label
        // First fragment of a fresh bar comes from this trade.
        if barFirstLabel.IsNone then
            barFirstLabel <- Some t.Label

        let btcTrade : BinanceLoader.Trade = {
            Price = t.Price
            Quantity = float t.Size
            TimestampUs = int64 (t.Time * 1e6)
            Sign = 1.0
        }
        builder.Process(onBar, btcTrade)

    bars.ToArray(), isHold.ToArray()

// =============================================================================
// Synthetic day generation
// =============================================================================

/// Mean values for the per-day BaseVolume / BaseRate priors. Per-day samples
/// are lognormal with median = 0.7 × mean, giving heavy-tailed, asymmetric
/// variation (mostly normal-ish days, occasional outliers).
[<Literal>]
let dayVolumeMean = 100.0
[<Literal>]
let dayRateMean = 10.0
[<Literal>]
let baseVolBpsConst = 15.0 * bps

/// Sample one day's BaseParams from lognormal priors over BaseVolume and
/// BaseRate (median = 0.7 × mean). BaseVolatility is then re-normalized
/// against the realized day-level volume×rate so the per-trade proposal
/// stays calibrated.
let sampleBaseParams (rng: Random) : BaseParams =
    let dayVolume = sampleLogNormal rng (dayVolumeMean * 0.7) dayVolumeMean
    let dayRate = sampleLogNormal rng (dayRateMean * 0.7) dayRateMean
    let normalizedBaseVol = baseVolBpsConst / sqrt (dayRate * dayVolume)
    {
        BaseVolume = dayVolume
        BaseRate = dayRate
        BaseVolatility = normalizedBaseVol
    }

let simParamsOf (startPrice: float) (bp: BaseParams) : SimulationParams =
    {
        TotalDuration = 390.0
        StartPrice = startPrice
        StartTarget = startPrice
        BaseVolume = bp.BaseVolume
        BaseRate = bp.BaseRate
        BaseVolatility = bp.BaseVolatility
    }

/// Convert one trade from log-space (as the simulator emits) to real-space.
/// Price/TargetMean exp directly; TargetSigma uses the exact lognormal stddev
/// since the band is drawn around TargetMean on charts and must be in price
/// units, not log units.
let expTrade (t: Trade) : Trade =
    { t with
        Price = exp t.Price
        TargetMean = exp t.TargetMean
        TargetSigma = MathNet.Numerics.Distributions.LogNormal(t.TargetMean, t.TargetSigma).StdDev }

/// Generate one synthetic day's bars + labels.
///
/// Two-pass bar sizing: first emit all trades to know the day's realized
/// total volume, then bucket into `barsPerDay` equal-volume bars. Bar volume
/// = totalVolume / barsPerDay. This keeps bars-per-day roughly constant
/// across days even though day-level volume varies by ~3-4× from the
/// lognormal BaseVolume×BaseRate sampling.
let generateSynthDay (dayId: int) (seed: int) (startPrice: float) (barsPerDay: int) : DayBars =
    let rng = Random(seed)
    let baseParams = sampleBaseParams rng
    let simParams = simParamsOf (log startPrice) baseParams
    let ctx = makeDefaultContext rng simParams
    let pattern = trendDay None baseParams
    let trades =
        pattern ctx (fun _ -> ctx.Effects.OnDone ctx)
        |> Array.map expTrade
    let totalVolume = trades |> Array.sumBy (fun t -> float t.Size)
    let barVolume = totalVolume / float barsPerDay
    let bars, isHold = barsAndLabelsFromSynth barVolume trades
    toDayBars dayId bars isHold

// =============================================================================
// Parquet writer: one row group per day
// =============================================================================

let writeRowGroup (writer: ParquetWriter) (d: DayBars) = task {
    use rg = writer.CreateRowGroup()
    let f = datasetSchema.DataFields
    let n = d.BarIdx.Length
    let dayIds = Array.create n d.DayId
    do! rg.WriteColumnAsync(DataColumn(f.[0], dayIds))
    do! rg.WriteColumnAsync(DataColumn(f.[1], d.BarIdx))
    do! rg.WriteColumnAsync(DataColumn(f.[2], d.RelStdDev))
    do! rg.WriteColumnAsync(DataColumn(f.[3], d.Ret))
    do! rg.WriteColumnAsync(DataColumn(f.[4], d.DurationSec))
    do! rg.WriteColumnAsync(DataColumn(f.[5], d.TradeCount))
    do! rg.WriteColumnAsync(DataColumn(f.[6], d.IsHold))
}

let writeOneDayParquet (outputPath: string) (d: DayBars) = task {
    let dir = Path.GetDirectoryName(outputPath)
    if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    use stream = File.Create(outputPath)
    let! writer = ParquetWriter.CreateAsync(datasetSchema, stream)
    use writer = writer
    do! writeRowGroup writer d
}

/// Bulk-generate `numDays` synthetic days in parallel, streaming row groups
/// to a single parquet file in day_id order. Workers generate concurrently;
/// the writer pulls from a bounded channel and reorders via a small pending
/// dictionary so row groups land in monotonic day_id order.
let generateSynthDataset (baseSeed: int) (numDays: int) (startPrice: float) (barsPerDay: int) (outputPath: string) (numWorkers: int) = task {
    printfn "Generating %d synthetic days with %d workers..." numDays numWorkers
    let dir = Path.GetDirectoryName(outputPath)
    if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

    let channel = Channel.CreateBounded<DayBars>(BoundedChannelOptions(numWorkers * 4))

    // Producer workers: round-robin day assignment by (workerId, numWorkers).
    let workers =
        [| for w in 0 .. numWorkers - 1 ->
            task {
                let mutable dayId = w
                while dayId < numDays do
                    let day = generateSynthDay dayId (baseSeed + dayId) startPrice barsPerDay
                    do! channel.Writer.WriteAsync(day)
                    dayId <- dayId + numWorkers
            } |]

    let workersDone = task {
        let! _ = Task.WhenAll(workers)
        channel.Writer.Complete()
    }

    use stream = File.Create(outputPath)
    let! writer = ParquetWriter.CreateAsync(datasetSchema, stream)
    use writer = writer

    // Reorder: workers produce out-of-order days; we hold them until the
    // next contiguous day arrives so row groups are written in day_id order.
    let pending = Collections.Generic.Dictionary<int, DayBars>()
    let mutable nextToWrite = 0
    let mutable written = 0
    let progressEvery = max 1 (numDays / 200)
    let sw = System.Diagnostics.Stopwatch.StartNew()

    for d in channel.Reader.ReadAllAsync() do
        pending.[d.DayId] <- d
        while pending.ContainsKey(nextToWrite) do
            let day = pending.[nextToWrite]
            pending.Remove(nextToWrite) |> ignore
            do! writeRowGroup writer day
            nextToWrite <- nextToWrite + 1
            written <- written + 1
            if written % progressEvery = 0 || written = numDays then
                let elapsed = sw.Elapsed.TotalSeconds
                let rate = float written / max 1e-9 elapsed
                let etaSec = float (numDays - written) / max 1e-9 rate
                printfn "  %d / %d days  (%.0f%%, %.1f days/s, ETA %.0fs)"
                    written numDays (100.0 * float written / float numDays) rate etaSec
                System.Console.Out.Flush()

    do! workersDone
    printfn "Done. Wrote %d days to %s in %.1fs" numDays outputPath sw.Elapsed.TotalSeconds
}

// =============================================================================
// BTC bar builder
// =============================================================================

let buildBtcBars (inputPath: string) (barsPerDay: int) (outputPath: string) = task {
    printfn "Loading BTC trades from %s..." inputPath
    let trades = BinanceLoader.load inputPath
    printfn "  Loaded %d trades" trades.Length
    let totalVolume = trades |> Array.sumBy (fun t -> t.Quantity)
    let barVolume = totalVolume / float barsPerDay
    printfn "  Total volume %g; barsPerDay %d -> barVolume %g" totalVolume barsPerDay barVolume
    let bars = barsFromBtcTrades barVolume trades
    printfn "  Built %d volume bars" bars.Length
    let isHold = Array.zeroCreate bars.Length
    // Use day_id = 0 for a single-day BTC parquet. When we accumulate multiple
    // days later we'll bump this to the date's ordinal.
    let day = toDayBars 0 bars isHold
    do! writeOneDayParquet outputPath day
    printfn "Wrote BTC bars to %s" outputPath
}
