module TradingEdge.Simulation.HoldDigests

// Per-day (per-row-group) t-digest fit + CDF transform for the hold-detector
// pipeline. Each row group corresponds to one day's bars; we fit four marginals
// (one per feature) on just that day's bars and immediately CDF-transform them.
// Patterns are relative to the surrounding regime, so a day-local digest gives
// the NN inputs that capture "tight relative to today" rather than "tight
// relative to global BTC". No global digest file exists.
//
// Reuses CdfLookupTable / createCdfLookup / lookupValue / applyCdf from
// TradingEdge.Simulation.TDigestProcessing — only the schema and the per-row
// I/O scaffolding are bespoke to the hold-dataset four-feature layout.

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Parquet
open Parquet.Schema
open Parquet.Data
open TDigest
open FSharp.Control
open TradingEdge.Simulation.TDigestProcessing
open TradingEdge.Simulation.HoldDataset

let defaultCompression = 4096.0

// =============================================================================
// Parquet I/O for the hold-dataset schema
// =============================================================================
//
// Mirrors the column order in HoldDataset.datasetSchema:
//   0=day_id, 1=bar_idx, 2=rel_stddev, 3=ret, 4=duration_sec,
//   5=trade_count, 6=buy_count, 7=label
// buy_count is read so it survives a copy/inspection round trip but the CDF
// transform itself only needs trade_count for normalization.

type private RawRowGroup = {
    Index: int
    DayIds: int[]
    BarIdx: int[]
    RelStdDev: float[]
    Ret: float[]
    DurationSec: float[]
    TradeCount: int[]
    Label: int[]
}

let private readRowGroup (rg: ParquetRowGroupReader) (schema: ParquetSchema) (rgIndex: int) = task {
    let f = schema.DataFields
    let! dayIds = rg.ReadColumnAsync(f.[0])
    let! barIdx = rg.ReadColumnAsync(f.[1])
    let! relStd = rg.ReadColumnAsync(f.[2])
    let! ret = rg.ReadColumnAsync(f.[3])
    let! dur = rg.ReadColumnAsync(f.[4])
    let! tc = rg.ReadColumnAsync(f.[5])
    // f.[6] = buy_count (chart-only; CDF stage doesn't use it)
    let! label = rg.ReadColumnAsync(f.[7])
    return {
        Index = rgIndex
        DayIds = dayIds.Data :?> int[]
        BarIdx = barIdx.Data :?> int[]
        RelStdDev = relStd.Data :?> float[]
        Ret = ret.Data :?> float[]
        DurationSec = dur.Data :?> float[]
        TradeCount = tc.Data :?> int[]
        Label = label.Data :?> int[]
    }
}

// =============================================================================
// CDF transform: read raw hold-dataset parquet -> write per-day-CDF'd parquet
// =============================================================================
//
// Output schema preserves day_id / bar_idx / label (Python-side metadata)
// and replaces the four float feature columns with their CDF'd versions in
// [-1, 1]. trade_count's int column becomes float to share the column type
// with the others post-CDF.

let cdfSchema = ParquetSchema(
    DataField<int>("day_id"),
    DataField<int>("bar_idx"),
    DataField<float>("cdf_rel_stddev"),
    DataField<float>("cdf_ret"),
    DataField<float>("cdf_duration"),
    DataField<float>("cdf_trade_count"),
    DataField<int>("label")
)

type private CdfRowGroup = {
    Index: int
    DayIds: int[]
    BarIdx: int[]
    CdfRelStdDev: float[]
    CdfRet: float[]
    CdfDuration: float[]
    CdfTradeCount: float[]
    Label: int[]
}

let private writeCdfRowGroup (writer: ParquetWriter) (d: CdfRowGroup) = task {
    use rg = writer.CreateRowGroup()
    let f = cdfSchema.DataFields
    do! rg.WriteColumnAsync(DataColumn(f.[0], d.DayIds))
    do! rg.WriteColumnAsync(DataColumn(f.[1], d.BarIdx))
    do! rg.WriteColumnAsync(DataColumn(f.[2], d.CdfRelStdDev))
    do! rg.WriteColumnAsync(DataColumn(f.[3], d.CdfRet))
    do! rg.WriteColumnAsync(DataColumn(f.[4], d.CdfDuration))
    do! rg.WriteColumnAsync(DataColumn(f.[5], d.CdfTradeCount))
    do! rg.WriteColumnAsync(DataColumn(f.[6], d.Label))
}

let private addFinite (td: MergingDigest) (v: float) =
    if Double.IsFinite v then td.Add v

/// Standard-normal CDF mapped to [-1, 1]. Used to project z-scored log
/// returns into the same range as the per-day rank-CDF features. Zero z
/// maps to 0; ±∞ maps to ±1.
let private normalCdfSigned (z: float) : float =
    // 2 * Φ(z) - 1 = erf(z / sqrt(2))
    MathNet.Numerics.SpecialFunctions.Erf(z / sqrt 2.0)

/// Fit one MergingDigest per feature on this row group's bars (rel_stddev,
/// duration, trade_count), then CDF the same bars through the per-day
/// digests. Log return uses a fixed-mean (=0) z-score and the analytic
/// standard-normal CDF instead — the rank-only treatment was discarding the
/// magnitude information needed to tell holds (returns reverting to zero)
/// from drifts (returns of similar rank but accumulating directionally).
let private fitAndTransform (compression: float) (numBuckets: int) (rg: RawRowGroup) : CdfRowGroup =
    let relTd = MergingDigest(compression)
    let durTd = MergingDigest(compression)
    let tcTd = MergingDigest(compression)
    let mutable retSqSum = 0.0
    let mutable retCount = 0
    for i in 0 .. rg.RelStdDev.Length - 1 do
        addFinite relTd rg.RelStdDev.[i]
        addFinite durTd rg.DurationSec.[i]
        addFinite tcTd (float rg.TradeCount.[i])
        let r = rg.Ret.[i]
        if Double.IsFinite r then
            retSqSum <- retSqSum + r * r
            retCount <- retCount + 1
    // Mean is fixed at 0; std is the RMS over the day's log returns.
    let retStd =
        if retCount > 0 then sqrt (retSqSum / float retCount) else 1.0
    let retScale = if retStd > 0.0 then retStd else 1.0
    let cdfRet = Array.map (fun r -> normalCdfSigned (r / retScale)) rg.Ret
    let relLookup = createCdfLookup relTd numBuckets
    let durLookup = createCdfLookup durTd numBuckets
    let tcLookup = createCdfLookup tcTd numBuckets
    {
        Index = rg.Index
        DayIds = rg.DayIds
        BarIdx = rg.BarIdx
        CdfRelStdDev = applyCdf relLookup rg.RelStdDev
        CdfRet = cdfRet
        CdfDuration = applyCdf durLookup rg.DurationSec
        CdfTradeCount = applyCdfInt tcLookup rg.TradeCount
        Label = rg.Label
    }

let applyCdfTransform (inputPath: string) (outputPath: string) (compression: float) (numWorkers: int) = task {
    printfn "Per-day CDF transform: %s -> %s" inputPath outputPath

    // Bucket count for the piecewise-linear lookup. With per-day digests the
    // input population is small (~2-6k bars) so we don't need 2^17 buckets;
    // 2^12 is plenty and keeps the per-day fit fast.
    let numBuckets = 1 <<< 12

    use inStream = File.OpenRead(inputPath)
    let! reader = ParquetReader.CreateAsync(inStream)
    use reader = reader
    let rgCount = reader.RowGroupCount
    let schema = reader.Schema
    printfn "  %d row groups, %d workers, compression=%g, buckets=%d" rgCount numWorkers compression numBuckets

    let readChannel = Channel.CreateBounded<RawRowGroup>(BoundedChannelOptions(numWorkers * 2))
    let writeChannel = Channel.CreateBounded<CdfRowGroup>(BoundedChannelOptions(numWorkers * 2))
    let mutable processed = 0

    let producer = task {
        for i in 0 .. rgCount - 1 do
            use rg = reader.OpenRowGroupReader(i)
            let! data = readRowGroup rg schema i
            do! readChannel.Writer.WriteAsync(data)
        readChannel.Writer.Complete()
    }

    let workers = [|
        for _ in 0 .. numWorkers - 1 ->
            task {
                for rg in readChannel.Reader.ReadAllAsync() do
                    let cdf = fitAndTransform compression numBuckets rg
                    do! writeChannel.Writer.WriteAsync(cdf)
                    let n = Interlocked.Increment(&processed)
                    if n % 100 = 0 then printfn "  Transformed %d / %d row groups" n rgCount
            }
    |]

    let workersDone = task {
        let! _ = Task.WhenAll(workers)
        writeChannel.Writer.Complete()
    }

    let dir = Path.GetDirectoryName(outputPath)
    if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    use outStream = File.Create(outputPath)
    let! writer = ParquetWriter.CreateAsync(cdfSchema, outStream)
    use writer = writer

    // Reorder: workers may emit out of order; hold pending until contiguous.
    let pending = Collections.Generic.Dictionary<int, CdfRowGroup>()
    let mutable nextToWrite = 0

    for d in writeChannel.Reader.ReadAllAsync() do
        pending.[d.Index] <- d
        while pending.ContainsKey(nextToWrite) do
            let row = pending.[nextToWrite]
            pending.Remove(nextToWrite) |> ignore
            do! writeCdfRowGroup writer row
            nextToWrite <- nextToWrite + 1

    do! producer
    do! workersDone

    // Sidecar JSON ride-along: if the input parquet has a <basename>.labels.json
    // next to it, copy it to <output basename>.labels.json so the Python side
    // can find class names without the user manually copying.
    let inSidecar = Path.ChangeExtension(inputPath, ".labels.json")
    let outSidecar = Path.ChangeExtension(outputPath, ".labels.json")
    if File.Exists inSidecar then
        File.Copy(inSidecar, outSidecar, overwrite = true)
        printfn "Copied label sidecar: %s -> %s" inSidecar outSidecar

    printfn "Done. Wrote %d row groups to %s" rgCount outputPath
}
