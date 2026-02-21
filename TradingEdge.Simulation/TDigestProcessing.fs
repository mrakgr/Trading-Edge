module TradingEdge.Simulation.TDigestProcessing

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

type TDigests = {
    Vwap: MergingDigest
    Volume: MergingDigest
    StdDev: MergingDigest
}

type private FeatureArrays = {
    Vwap1s: float[]; Volume1s: int[]; StdDev1s: float[]
    Vwap1m: float[]; Volume1m: int[]; StdDev1m: float[]
    Vwap5m: float[]; Volume5m: int[]; StdDev5m: float[]
}

type private RowGroupData = {
    Index: int
    DayIds: int[]; Times: int[]; Sessions: int[]; Trends: int[]
    Features: FeatureArrays
}

let defaultCompression = 4096.0

let private readFeatureArrays (rg: ParquetRowGroupReader) (schema: ParquetSchema) = task {
    let! vwap1s = rg.ReadColumnAsync(schema.DataFields.[4])
    let! vol1s = rg.ReadColumnAsync(schema.DataFields.[5])
    let! std1s = rg.ReadColumnAsync(schema.DataFields.[6])
    let! vwap1m = rg.ReadColumnAsync(schema.DataFields.[7])
    let! vol1m = rg.ReadColumnAsync(schema.DataFields.[8])
    let! std1m = rg.ReadColumnAsync(schema.DataFields.[9])
    let! vwap5m = rg.ReadColumnAsync(schema.DataFields.[10])
    let! vol5m = rg.ReadColumnAsync(schema.DataFields.[11])
    let! std5m = rg.ReadColumnAsync(schema.DataFields.[12])
    return {
        Vwap1s = vwap1s.Data :?> float[]; Volume1s = vol1s.Data :?> int[]; StdDev1s = std1s.Data :?> float[]
        Vwap1m = vwap1m.Data :?> float[]; Volume1m = vol1m.Data :?> int[]; StdDev1m = std1m.Data :?> float[]
        Vwap5m = vwap5m.Data :?> float[]; Volume5m = vol5m.Data :?> int[]; StdDev5m = std5m.Data :?> float[]
    }
}

let private addFinite (td: MergingDigest) (v: float) =
    if Double.IsFinite(v) then td.Add(v)

let private addToDigests (vwapTd: MergingDigest) (volTd: MergingDigest) (stdTd: MergingDigest) (f: FeatureArrays) =
    for i in 0 .. f.Vwap1s.Length - 1 do
        addFinite vwapTd f.Vwap1s.[i]; addFinite vwapTd f.Vwap1m.[i]; addFinite vwapTd f.Vwap5m.[i]
        volTd.Add(float f.Volume1s.[i]); volTd.Add(float f.Volume1m.[i]); volTd.Add(float f.Volume5m.[i])
        addFinite stdTd f.StdDev1s.[i]; addFinite stdTd f.StdDev1m.[i]; addFinite stdTd f.StdDev5m.[i]

let private readRowGroupData (rg: ParquetRowGroupReader) (schema: ParquetSchema) (rgIndex: int) = task {
    let! dayIds = rg.ReadColumnAsync(schema.DataFields.[0])
    let! times = rg.ReadColumnAsync(schema.DataFields.[1])
    let! sessions = rg.ReadColumnAsync(schema.DataFields.[2])
    let! trends = rg.ReadColumnAsync(schema.DataFields.[3])
    let! features = readFeatureArrays rg schema
    return {
        Index = rgIndex
        DayIds = dayIds.Data :?> int[]; Times = times.Data :?> int[]
        Sessions = sessions.Data :?> int[]; Trends = trends.Data :?> int[]
        Features = features
    }
}

let buildTDigestsFromParquet (inputPath: string) (compression: float) (numWorkers: int) = task {
    printfn "Building t-digests from %s..." inputPath
    use stream = File.OpenRead(inputPath)
    let! reader = ParquetReader.CreateAsync(stream)
    use reader = reader
    let rowGroupCount = reader.RowGroupCount
    let schema = reader.Schema
    printfn "  Using %d workers for %d row groups..." numWorkers rowGroupCount

    let channel = Channel.CreateBounded<FeatureArrays>(BoundedChannelOptions(numWorkers * 2))
    let mutable processed = 0

    let producer = task {
        for rgIndex in 0 .. rowGroupCount - 1 do
            use rg = reader.OpenRowGroupReader(rgIndex)
            let! features = readFeatureArrays rg schema
            do! channel.Writer.WriteAsync(features)
        channel.Writer.Complete()
    }

    let workers = [|
        for _ in 0 .. numWorkers - 1 ->
            task {
                let vwapTd = MergingDigest(compression)
                let volTd = MergingDigest(compression)
                let stdTd = MergingDigest(compression)
                for features in channel.Reader.ReadAllAsync() do
                    addToDigests vwapTd volTd stdTd features
                    let count = Interlocked.Increment(&processed)
                    if count % 100 = 0 then
                        printfn "  Processed %d / %d row groups" count rowGroupCount
                return (vwapTd, volTd, stdTd)
            }
    |]

    do! producer
    let! results = Task.WhenAll(workers)

    let merge (compression: float) (digests: MergingDigest seq) =
        let merged = MergingDigest(compression)
        merged.Add(digests |> Seq.cast<Digest>)
        merged

    printfn "Done. Processed %d row groups" rowGroupCount
    return {
        Vwap = merge compression (results |> Seq.map (fun (v,_,_) -> v))
        Volume = merge compression (results |> Seq.map (fun (_,v,_) -> v))
        StdDev = merge compression (results |> Seq.map (fun (_,_,s) -> s))
    }
}

let saveTDigests (tds: TDigests) (outputPath: string) =
    use stream = File.Create(outputPath)
    use writer = new BinaryWriter(stream)
    tds.Vwap.AsBytes(writer)
    tds.Volume.AsBytes(writer)
    tds.StdDev.AsBytes(writer)
    printfn "Saved t-digests to %s" outputPath

let loadTDigests (inputPath: string) =
    use stream = File.OpenRead(inputPath)
    use reader = new BinaryReader(stream)
    { Vwap = MergingDigest.FromBytes(reader)
      Volume = MergingDigest.FromBytes(reader)
      StdDev = MergingDigest.FromBytes(reader) }

type CdfLookupTable = {
    Min: float; Max: float; Step: float; Values: float[]
}

let createCdfLookup (td: MergingDigest) (numBuckets: int) =
    let min = td.GetMin()
    let max = td.GetMax()
    let step = (max - min) / float (numBuckets - 1)
    let values = Array.init numBuckets (fun i ->
        td.Cdf(min + float i * step) * 2.0 - 1.0)
    { Min = min; Max = max; Step = step; Values = values }

let lookupValue (lookup: CdfLookupTable) (v: float) =
    let maxIdx = lookup.Values.Length - 1
    if v <= lookup.Min then lookup.Values.[0]
    elif v >= lookup.Max then lookup.Values.[maxIdx]
    else
        let idx = (v - lookup.Min) / lookup.Step
        let lo = int idx
        let hi = min (lo + 1) maxIdx
        let t = idx - float lo
        lookup.Values.[lo] * (1.0 - t) + lookup.Values.[hi] * t

let applyCdf (lookup: CdfLookupTable) (values: float[]) = Array.map (lookupValue lookup) values
let applyCdfInt (lookup: CdfLookupTable) (values: int[]) = values |> Array.map (fun v -> lookupValue lookup (float v))

type private TransformedRowGroup = {
    Index: int
    DayIds: int[]; Times: int[]; Sessions: int[]; Trends: int[]
    CdfVwap1s: float[]; CdfVolume1s: float[]; CdfStdDev1s: float[]
    CdfVwap1m: float[]; CdfVolume1m: float[]; CdfStdDev1m: float[]
    CdfVwap5m: float[]; CdfVolume5m: float[]; CdfStdDev5m: float[]
}

let private applyTransform (vwapLookup: CdfLookupTable) (volLookup: CdfLookupTable) (stdLookup: CdfLookupTable) (data: RowGroupData) =
    let f = data.Features
    { Index = data.Index
      DayIds = data.DayIds; Times = data.Times; Sessions = data.Sessions; Trends = data.Trends
      CdfVwap1s = applyCdf vwapLookup f.Vwap1s; CdfVolume1s = applyCdfInt volLookup f.Volume1s; CdfStdDev1s = applyCdf stdLookup f.StdDev1s
      CdfVwap1m = applyCdf vwapLookup f.Vwap1m; CdfVolume1m = applyCdfInt volLookup f.Volume1m; CdfStdDev1m = applyCdf stdLookup f.StdDev1m
      CdfVwap5m = applyCdf vwapLookup f.Vwap5m; CdfVolume5m = applyCdfInt volLookup f.Volume5m; CdfStdDev5m = applyCdf stdLookup f.StdDev5m }

let private outSchema = ParquetSchema(
    DataField<int>("day_id"), DataField<int>("time"), DataField<int>("session"), DataField<int>("trend"),
    DataField<float>("cdf_vwap_1s"), DataField<float>("cdf_volume_1s"), DataField<float>("cdf_stddev_1s"),
    DataField<float>("cdf_vwap_1m"), DataField<float>("cdf_volume_1m"), DataField<float>("cdf_stddev_1m"),
    DataField<float>("cdf_vwap_5m"), DataField<float>("cdf_volume_5m"), DataField<float>("cdf_stddev_5m")
)

let private writeRowGroup (writer: ParquetWriter) (d: TransformedRowGroup) = task {
    use rg = writer.CreateRowGroup()
    let f = outSchema.DataFields
    do! rg.WriteColumnAsync(DataColumn(f.[0], d.DayIds))
    do! rg.WriteColumnAsync(DataColumn(f.[1], d.Times))
    do! rg.WriteColumnAsync(DataColumn(f.[2], d.Sessions))
    do! rg.WriteColumnAsync(DataColumn(f.[3], d.Trends))
    do! rg.WriteColumnAsync(DataColumn(f.[4], d.CdfVwap1s))
    do! rg.WriteColumnAsync(DataColumn(f.[5], d.CdfVolume1s))
    do! rg.WriteColumnAsync(DataColumn(f.[6], d.CdfStdDev1s))
    do! rg.WriteColumnAsync(DataColumn(f.[7], d.CdfVwap1m))
    do! rg.WriteColumnAsync(DataColumn(f.[8], d.CdfVolume1m))
    do! rg.WriteColumnAsync(DataColumn(f.[9], d.CdfStdDev1m))
    do! rg.WriteColumnAsync(DataColumn(f.[10], d.CdfVwap5m))
    do! rg.WriteColumnAsync(DataColumn(f.[11], d.CdfVolume5m))
    do! rg.WriteColumnAsync(DataColumn(f.[12], d.CdfStdDev5m))
}

let transformParquetWithCdf (inputPath: string) (tds: TDigests) (outputPath: string) (numWorkers: int) = task {
    printfn "Transforming %s with CDF..." inputPath

    use inStream = File.OpenRead(inputPath)
    let! reader = ParquetReader.CreateAsync(inStream)
    use reader = reader
    let rowGroupCount = reader.RowGroupCount
    let inSchema = reader.Schema

    printfn "  Using %d workers for %d row groups..." numWorkers rowGroupCount

    let numBuckets = 1 <<< 17
    let vwapLookup = createCdfLookup tds.Vwap numBuckets
    let volLookup = createCdfLookup tds.Volume numBuckets
    let stdLookup = createCdfLookup tds.StdDev numBuckets

    let readChannel = Channel.CreateBounded<RowGroupData>(BoundedChannelOptions(numWorkers * 2))
    let writeChannel = Channel.CreateBounded<TransformedRowGroup>(BoundedChannelOptions(numWorkers * 2))
    let mutable processed = 0

    let producer = task {
        for rgIndex in 0 .. rowGroupCount - 1 do
            use rg = reader.OpenRowGroupReader(rgIndex)
            let! data = readRowGroupData rg inSchema rgIndex
            do! readChannel.Writer.WriteAsync(data)
        readChannel.Writer.Complete()
    }

    let workers = [|
        for _ in 0 .. numWorkers - 1 ->
            task {
                for data in readChannel.Reader.ReadAllAsync() do
                    let transformed = applyTransform vwapLookup volLookup stdLookup data
                    do! writeChannel.Writer.WriteAsync(transformed)
                    let count = Interlocked.Increment(&processed)
                    if count % 100 = 0 then
                        printfn "  Transformed %d / %d row groups" count rowGroupCount
            }
    |]

    let workersDone = task {
        let! _ = Task.WhenAll(workers)
        writeChannel.Writer.Complete()
    }

    use outStream = File.Create(outputPath)
    let! writer = ParquetWriter.CreateAsync(outSchema, outStream)
    use writer = writer
    let pending = Collections.Generic.Dictionary<int, TransformedRowGroup>()
    let mutable nextToWrite = 0

    for data in writeChannel.Reader.ReadAllAsync() do
        pending.[data.Index] <- data
        while pending.ContainsKey(nextToWrite) do
            let d = pending.[nextToWrite]
            pending.Remove(nextToWrite) |> ignore
            do! writeRowGroup writer d
            nextToWrite <- nextToWrite + 1

    do! producer
    do! workersDone
    printfn "Done. Transformed %d row groups to %s" rowGroupCount outputPath
}
