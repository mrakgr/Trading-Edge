// Benchmark V1 (mutable book) vs V2 (immutable book) on the same day.
//
//   dotnet fsi Benchmark.fsx -- <day-dir> [<iterations>]
//
// Reports build time, peak managed-memory delta, and bar correctness equality.

#r "TradingEdge.ReplaySimulatorV1/bin/Debug/net10.0/TradingEdge.ReplaySimulatorV1.dll"
#r "TradingEdge.ReplaySimulatorV1/bin/Debug/net10.0/FSharp.Control.TaskSeq.dll"
#r "TradingEdge.ReplaySimulatorV1/bin/Debug/net10.0/ZstdSharp.dll"
#r "TradingEdge.ReplaySimulatorV2/bin/Debug/net10.0/TradingEdge.ReplaySimulatorV2.dll"

open System
open System.IO
open System.Diagnostics

let args = fsi.CommandLineArgs |> Array.skip 1
let dayDir, iters =
    match args with
    | [| d |] -> d, 3
    | [| d; n |] -> d, int n
    | _ -> failwith "usage: dotnet fsi Benchmark.fsx -- <day-dir> [iters]"

if not (Directory.Exists dayDir) then failwithf "Not a directory: %s" dayDir
let files = Directory.GetFiles(dayDir, "*.dbn.zst") |> Array.sort
if files.Length = 0 then failwithf "No .dbn.zst files in %s" dayDir

printfn "Benchmark: %s (%d venue files, %d iterations each)" dayDir files.Length iters
printfn ""

// ---------- helpers ----------
let openVenueV1 (path: string) =
    let fs : Stream = File.OpenRead(path) :> Stream
    let zs : Stream = new ZstdSharp.DecompressionStream(fs) :> Stream
    let _ = (TradingEdge.ReplaySimulatorV1.Dbn.readMetadata zs).Result
    zs

let openVenueV2 (path: string) =
    let fs : Stream = File.OpenRead(path) :> Stream
    let zs : Stream = new ZstdSharp.DecompressionStream(fs) :> Stream
    let _ = (TradingEdge.ReplaySimulatorV2.Dbn.readMetadata zs).Result
    zs

let getMemMB () =
    GC.Collect(2, GCCollectionMode.Forced, true, true)
    GC.WaitForPendingFinalizers()
    GC.Collect(2, GCCollectionMode.Forced, true, true)
    float (GC.GetTotalMemory(true)) / (1024.0 * 1024.0)

// ---------- V1 ----------
let runV1 () =
    let streams = files |> Array.map openVenueV1
    try
        let merged =
            streams
            |> Array.map TradingEdge.ReplaySimulatorV1.MboReader.readMboRecords
            |> Array.toList
            |> TradingEdge.ReplaySimulatorV1.MboReader.mergeByTsEvent
        let memBefore = getMemMB ()
        let sw = Stopwatch.StartNew()
        let store = (TradingEdge.ReplaySimulatorV1.Snapshots.buildAsync merged).Result
        sw.Stop()
        let memAfter = getMemMB ()
        sw.Elapsed.TotalSeconds, memAfter - memBefore, store
    finally
        streams |> Array.iter (fun s -> try s.Dispose() with _ -> ())

let runV2 () =
    let streams = files |> Array.map openVenueV2
    try
        let merged =
            streams
            |> Array.map TradingEdge.ReplaySimulatorV2.MboReader.readMboRecords
            |> Array.toList
            |> TradingEdge.ReplaySimulatorV2.MboReader.mergeByTsEvent
        let memBefore = getMemMB ()
        let sw = Stopwatch.StartNew()
        let store = (TradingEdge.ReplaySimulatorV2.Snapshots.buildAsync merged).Result
        sw.Stop()
        let memAfter = getMemMB ()
        sw.Elapsed.TotalSeconds, memAfter - memBefore, store
    finally
        streams |> Array.iter (fun s -> try s.Dispose() with _ -> ())

let v1Times = ResizeArray<float>()
let v2Times = ResizeArray<float>()
let v1Mems = ResizeArray<float>()
let v2Mems = ResizeArray<float>()

let mutable v1LastStore = Unchecked.defaultof<_>
let mutable v2LastStore = Unchecked.defaultof<_>

for i in 1 .. iters do
    printfn "iteration %d/%d" i iters
    let (t1, m1, s1) = runV1 ()
    v1Times.Add t1; v1Mems.Add m1; v1LastStore <- s1
    printfn "  V1: build=%5.2fs  mem=%6.1f MB  records=%d  snapshots=%d"
        t1 m1 s1.Records.Length s1.Snapshots.Count
    let (t2, m2, s2) = runV2 ()
    v2Times.Add t2; v2Mems.Add m2; v2LastStore <- s2
    printfn "  V2: build=%5.2fs  mem=%6.1f MB  records=%d  snapshots=%d"
        t2 m2 s2.Records.Length s2.Snapshots.Count

let mean (xs: ResizeArray<float>) = Seq.average xs
let minimum (xs: ResizeArray<float>) = Seq.min xs

printfn ""
printfn "===== summary ====="
printfn "                  V1 (mut)   V2 (imm)   ratio"
printfn "build time mean:  %6.2fs    %6.2fs    %4.2fx"
    (mean v1Times) (mean v2Times) (mean v2Times / mean v1Times)
printfn "build time best:  %6.2fs    %6.2fs    %4.2fx"
    (minimum v1Times) (minimum v2Times) (minimum v2Times / minimum v1Times)
printfn "mem delta mean:   %6.1f MB  %6.1f MB  %4.2fx"
    (mean v1Mems) (mean v2Mems) (mean v2Mems / mean v1Mems)
printfn ""

// ---------- bar-correctness equality ----------
let v1Bars =
    [
        for snap in v1LastStore.Snapshots do
            for b in snap.ClosedBars -> b
        if v1LastStore.Snapshots.Count > 0 then
            let agg = TradingEdge.ReplaySimulatorV1.Bars.BarAggregator()
            agg.Hydrate (v1LastStore.Snapshots.[v1LastStore.Snapshots.Count - 1].AggState)
            let lastSnapStart = v1LastStore.Snapshots.[v1LastStore.Snapshots.Count - 1].BucketStartNs
            for m in v1LastStore.Records do
                if m.TsEvent >= lastSnapStart then agg.Feed m |> ignore
            match agg.Current with
            | Some b -> yield b
            | None -> ()
    ]

let v2Bars =
    [
        for snap in v2LastStore.Snapshots do
            for b in snap.ClosedBars -> b
        if v2LastStore.Snapshots.Count > 0 then
            let agg = TradingEdge.ReplaySimulatorV2.Bars.BarAggregator()
            agg.Hydrate (v2LastStore.Snapshots.[v2LastStore.Snapshots.Count - 1].AggState)
            let lastSnapStart = v2LastStore.Snapshots.[v2LastStore.Snapshots.Count - 1].BucketStartNs
            for m in v2LastStore.Records do
                if m.TsEvent >= lastSnapStart then agg.Feed m |> ignore
            match agg.Current with
            | Some b -> yield b
            | None -> ()
    ]

printfn "Bar correctness: V1=%d bars, V2=%d bars" v1Bars.Length v2Bars.Length
if v1Bars.Length = v2Bars.Length then
    let mismatches =
        List.zip v1Bars v2Bars
        |> List.filter (fun (a, b) ->
            a.BucketStartNs <> b.BucketStartNs ||
            a.Open <> b.Open || a.High <> b.High ||
            a.Low <> b.Low || a.Close <> b.Close ||
            a.Volume <> b.Volume || a.TradeCount <> b.TradeCount)
        |> List.length
    if mismatches = 0 then printfn "  All bars match exactly."
    else printfn "  %d bars differ — investigate" mismatches

// ---------- per-event allocation estimate ----------
let totalEvents = float v1LastStore.Records.Length
printfn ""
printfn "Per-event ns (build mean):  V1=%6.1f  V2=%6.1f"
    (mean v1Times * 1e9 / totalEvents)
    (mean v2Times * 1e9 / totalEvents)
