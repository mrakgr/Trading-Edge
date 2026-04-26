module TradingEdge.Simulation.HoldDigests

// Per-feature t-digests + CDF transform for the hold-detector pipeline.
// Fits one MergingDigest per feature against a parquet of hold-dataset bars
// (typically real BTC bars, since the NN sees synthetic inputs in real-data
// coordinates) and provides an apply-CDF step that reads any hold-dataset
// parquet (synthetic or real) and writes a parquet with each feature mapped
// to [-1, 1] via piecewise-linear CDF lookup tables.
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

// =============================================================================
// Digests bundle
// =============================================================================
//
// Four marginals — one per feature column written by HoldDataset. trade_count
// is integer in the parquet but t-digested as float (categorical-int features
// are uncommon and the TDigest API is float-only).

type HoldDigests = {
    RelStdDev: MergingDigest
    Ret: MergingDigest
    Duration: MergingDigest
    TradeCount: MergingDigest
}

let defaultCompression = 4096.0

// =============================================================================
// Parquet I/O for the hold-dataset schema
// =============================================================================
//
// Mirrors the column order in HoldDataset.datasetSchema:
//   0=day_id, 1=bar_idx, 2=rel_stddev, 3=ret, 4=duration_sec,
//   5=trade_count, 6=is_hold

type private RawRowGroup = {
    Index: int
    DayIds: int[]
    BarIdx: int[]
    RelStdDev: float[]
    Ret: float[]
    DurationSec: float[]
    TradeCount: int[]
    IsHold: int[]
}

let private readRowGroup (rg: ParquetRowGroupReader) (schema: ParquetSchema) (rgIndex: int) = task {
    let f = schema.DataFields
    let! dayIds = rg.ReadColumnAsync(f.[0])
    let! barIdx = rg.ReadColumnAsync(f.[1])
    let! relStd = rg.ReadColumnAsync(f.[2])
    let! ret = rg.ReadColumnAsync(f.[3])
    let! dur = rg.ReadColumnAsync(f.[4])
    let! tc = rg.ReadColumnAsync(f.[5])
    let! isHold = rg.ReadColumnAsync(f.[6])
    return {
        Index = rgIndex
        DayIds = dayIds.Data :?> int[]
        BarIdx = barIdx.Data :?> int[]
        RelStdDev = relStd.Data :?> float[]
        Ret = ret.Data :?> float[]
        DurationSec = dur.Data :?> float[]
        TradeCount = tc.Data :?> int[]
        IsHold = isHold.Data :?> int[]
    }
}

// =============================================================================
// Build digests
// =============================================================================
//
// Streams row groups through a bounded channel to a pool of workers; each
// worker maintains private digests, then we merge at the end. MergingDigest
// is not thread-safe so per-worker locality is required.

let private addFinite (td: MergingDigest) (v: float) =
    if Double.IsFinite v then td.Add v

let private addRow (td: HoldDigests) (rg: RawRowGroup) =
    for i in 0 .. rg.RelStdDev.Length - 1 do
        addFinite td.RelStdDev rg.RelStdDev.[i]
        addFinite td.Ret rg.Ret.[i]
        addFinite td.Duration rg.DurationSec.[i]
        addFinite td.TradeCount (float rg.TradeCount.[i])

let buildDigests (inputPath: string) (compression: float) (numWorkers: int) = task {
    printfn "Building hold-feature t-digests from %s..." inputPath
    use stream = File.OpenRead(inputPath)
    let! reader = ParquetReader.CreateAsync(stream)
    use reader = reader
    let rgCount = reader.RowGroupCount
    let schema = reader.Schema
    printfn "  %d row groups, %d workers" rgCount numWorkers

    let channel = Channel.CreateBounded<RawRowGroup>(BoundedChannelOptions(numWorkers * 2))
    let mutable processed = 0

    let producer = task {
        for i in 0 .. rgCount - 1 do
            use rg = reader.OpenRowGroupReader(i)
            let! data = readRowGroup rg schema i
            do! channel.Writer.WriteAsync(data)
        channel.Writer.Complete()
    }

    let workers = [|
        for _ in 0 .. numWorkers - 1 ->
            task {
                let local = {
                    RelStdDev = MergingDigest(compression)
                    Ret = MergingDigest(compression)
                    Duration = MergingDigest(compression)
                    TradeCount = MergingDigest(compression)
                }
                for rg in channel.Reader.ReadAllAsync() do
                    addRow local rg
                    let n = Interlocked.Increment(&processed)
                    if n % 100 = 0 then printfn "  Processed %d / %d row groups" n rgCount
                return local
            }
    |]

    do! producer
    let! results = Task.WhenAll(workers)

    let merge (digests: MergingDigest seq) =
        let merged = MergingDigest(compression)
        merged.Add(digests |> Seq.cast<Digest>)
        merged

    printfn "Done. Processed %d row groups." rgCount
    return {
        RelStdDev = merge (results |> Seq.map (fun d -> d.RelStdDev))
        Ret = merge (results |> Seq.map (fun d -> d.Ret))
        Duration = merge (results |> Seq.map (fun d -> d.Duration))
        TradeCount = merge (results |> Seq.map (fun d -> d.TradeCount))
    }
}

// =============================================================================
// Save / load (binary, four MergingDigest blobs concatenated)
// =============================================================================

let saveDigests (digests: HoldDigests) (outputPath: string) =
    let dir = Path.GetDirectoryName(outputPath)
    if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    use stream = File.Create(outputPath)
    use writer = new BinaryWriter(stream)
    digests.RelStdDev.AsBytes(writer)
    digests.Ret.AsBytes(writer)
    digests.Duration.AsBytes(writer)
    digests.TradeCount.AsBytes(writer)
    printfn "Saved hold-feature t-digests to %s" outputPath

let loadDigests (inputPath: string) : HoldDigests =
    use stream = File.OpenRead(inputPath)
    use reader = new BinaryReader(stream)
    {
        RelStdDev = MergingDigest.FromBytes(reader)
        Ret = MergingDigest.FromBytes(reader)
        Duration = MergingDigest.FromBytes(reader)
        TradeCount = MergingDigest.FromBytes(reader)
    }

// =============================================================================
// Diagnostics — print quantile sketch so we can sanity-check the digest
// =============================================================================

let printDigestSketch (name: string) (td: MergingDigest) =
    let qs = [| 0.001; 0.01; 0.1; 0.5; 0.9; 0.99; 0.999 |]
    printf "  %-12s  min=%.6g" name (td.GetMin())
    for q in qs do
        printf "  q%-5.3f=%.6g" q (td.Quantile(q))
    printfn "  max=%.6g" (td.GetMax())

let printDigestsSketch (digests: HoldDigests) =
    printfn "Hold-feature digests:"
    printDigestSketch "rel_stddev" digests.RelStdDev
    printDigestSketch "ret" digests.Ret
    printDigestSketch "duration" digests.Duration
    printDigestSketch "trade_count" digests.TradeCount

// =============================================================================
// CDF transform: read hold-dataset parquet -> write CDF'd hold-dataset parquet
// =============================================================================
//
// Output schema preserves day_id / bar_idx / is_hold (Python-side metadata)
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
    DataField<int>("is_hold")
)

type private CdfRowGroup = {
    Index: int
    DayIds: int[]
    BarIdx: int[]
    CdfRelStdDev: float[]
    CdfRet: float[]
    CdfDuration: float[]
    CdfTradeCount: float[]
    IsHold: int[]
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
    do! rg.WriteColumnAsync(DataColumn(f.[6], d.IsHold))
}

let applyCdfTransform (inputPath: string) (digestsPath: string) (outputPath: string) (numWorkers: int) = task {
    printfn "Applying CDF transform: %s -> %s" inputPath outputPath
    let digests = loadDigests digestsPath

    // Pre-build piecewise-linear lookup tables once. 2^17 buckets matches the
    // resolution used in TDigestProcessing.fs:253.
    let numBuckets = 1 <<< 17
    let relLookup = createCdfLookup digests.RelStdDev numBuckets
    let retLookup = createCdfLookup digests.Ret numBuckets
    let durLookup = createCdfLookup digests.Duration numBuckets
    let tcLookup = createCdfLookup digests.TradeCount numBuckets

    use inStream = File.OpenRead(inputPath)
    let! reader = ParquetReader.CreateAsync(inStream)
    use reader = reader
    let rgCount = reader.RowGroupCount
    let schema = reader.Schema
    printfn "  %d row groups, %d workers" rgCount numWorkers

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

    let transform (rg: RawRowGroup) : CdfRowGroup =
        {
            Index = rg.Index
            DayIds = rg.DayIds
            BarIdx = rg.BarIdx
            CdfRelStdDev = applyCdf relLookup rg.RelStdDev
            CdfRet = applyCdf retLookup rg.Ret
            CdfDuration = applyCdf durLookup rg.DurationSec
            CdfTradeCount = applyCdfInt tcLookup rg.TradeCount
            IsHold = rg.IsHold
        }

    let workers = [|
        for _ in 0 .. numWorkers - 1 ->
            task {
                for rg in readChannel.Reader.ReadAllAsync() do
                    let cdf = transform rg
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
    printfn "Done. Wrote %d row groups to %s" rgCount outputPath
}
