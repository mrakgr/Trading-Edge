// Cross-check v1's bar aggregation against v0's by loading the same day in both
// and comparing OHLCV/count.
//
//   dotnet fsi TradingEdge.ReplaySimulatorV1/CrossCheck.fsx -- <day-dir>

#r "../TradingEdge.ReplaySimulator/bin/Debug/net10.0/TradingEdge.ReplaySimulator.dll"
#r "../TradingEdge.ReplaySimulator/bin/Debug/net10.0/ZstdSharp.dll"
#r "bin/Debug/net10.0/TradingEdge.ReplaySimulatorV1.dll"
#r "bin/Debug/net10.0/FSharp.Control.TaskSeq.dll"

open System
open System.IO

let args = fsi.CommandLineArgs |> Array.skip 1
let dayDir =
    match args with
    | [| d |] -> d
    | _ -> failwithf "usage: dotnet fsi CrossCheck.fsx -- <day-dir>"

if not (Directory.Exists dayDir) then failwithf "Not a directory: %s" dayDir

// ----- v0 path -----
printfn "Loading v0..."
let sw0 = System.Diagnostics.Stopwatch.StartNew()
let v0Bars, v0Venues =
    TradingEdge.ReplaySimulator.Bars.loadDayBars dayDir
sw0.Stop()
v0Venues |> List.iter (fun v -> v.Disposable.Dispose())
printfn "  v0: %d bars in %.2fs" v0Bars.Length sw0.Elapsed.TotalSeconds

// ----- v1 path -----
printfn "Loading v1..."
let sw1 = System.Diagnostics.Stopwatch.StartNew()
let files = Directory.GetFiles(dayDir, "*.dbn.zst") |> Array.sort
let venues =
    files |> Array.map (fun path ->
        let fs : Stream = File.OpenRead(path) :> Stream
        let zs : Stream = new ZstdSharp.DecompressionStream(fs) :> Stream
        let _meta = (TradingEdge.ReplaySimulatorV1.Dbn.readMetadata zs).Result
        zs)
let merged =
    venues
    |> Array.map TradingEdge.ReplaySimulatorV1.MboReader.readMboRecords
    |> Array.toList
    |> TradingEdge.ReplaySimulatorV1.MboReader.mergeByTsEvent
let store = (TradingEdge.ReplaySimulatorV1.Snapshots.buildAsync merged).Result
sw1.Stop()
venues |> Array.iter (fun s -> try s.Dispose() with _ -> ())

// Collect v1 bars: completed bars from every snapshot + the still-forming final bar.
// (v0 calls flush() at end-of-stream, which materializes the final partial bar as a Bar.)
let v1Bars =
    [
        for snap in store.Snapshots do
            for b in snap.ClosedBars -> b
        // The forming bar lives inside the LAST snapshot's AggState — we need to
        // hydrate a BarAggregator and read its Current to materialize it.
        if store.Snapshots.Count > 0 then
            let lastAgg = TradingEdge.ReplaySimulatorV1.Bars.BarAggregator()
            lastAgg.Hydrate (store.Snapshots.[store.Snapshots.Count - 1].AggState)
            // Replay any post-last-snapshot records (none if we built the store fully).
            // Actually, the store retains every record in `store.Records`; the agg state
            // in the last snapshot reflects state at that snapshot's start, NOT EOF.
            // So we need to replay records from the last snapshot's start to EOF.
            let lastSnapStart = store.Snapshots.[store.Snapshots.Count - 1].BucketStartNs
            for m in store.Records do
                if m.TsEvent >= lastSnapStart then
                    lastAgg.Feed m |> ignore
            match lastAgg.Current with
            | Some b -> yield b
            | None -> ()
    ]
printfn "  v1: %d bars in %.2fs (%d snapshots, %d records)"
    v1Bars.Length sw1.Elapsed.TotalSeconds store.Snapshots.Count store.Records.Length

// ----- compare -----
let v0Count = v0Bars.Length
let v1Count = v1Bars.Length
printfn ""
printfn "Bar count: v0=%d  v1=%d  delta=%d" v0Count v1Count (v1Count - v0Count)

if v0Count <> v1Count then
    printfn "Bar counts differ — cross-check FAILED"
    exit 1

let mutable mismatches = 0
let firstMismatches = ResizeArray<int>()
List.iter2 (fun (a: TradingEdge.ReplaySimulator.Bars.Bar) (b: TradingEdge.ReplaySimulatorV1.Bars.Bar) ->
    let i = mismatches
    let eq =
        a.BucketStartNs = b.BucketStartNs
        && a.Open = b.Open
        && a.High = b.High
        && a.Low = b.Low
        && a.Close = b.Close
        && a.Volume = b.Volume
        && a.TradeCount = b.TradeCount
    if not eq then
        mismatches <- mismatches + 1
        if firstMismatches.Count < 5 then firstMismatches.Add i
) v0Bars v1Bars

if mismatches = 0 then
    printfn "All %d bars match exactly. Cross-check PASSED." v0Count
    let v0TotalVol = v0Bars |> List.sumBy (fun b -> b.Volume)
    let v1TotalVol = v1Bars |> List.sumBy (fun b -> b.Volume)
    printfn "Total volume: v0=%d  v1=%d" v0TotalVol v1TotalVol
    exit 0
else
    printfn "%d / %d bars mismatched. Cross-check FAILED." mismatches v0Count
    for idx in firstMismatches do
        let a = v0Bars.[idx]
        let b = v1Bars.[idx]
        printfn "  bar %d: v0 ts=%d O=%g H=%g L=%g C=%g V=%d  vs  v1 ts=%d O=%g H=%g L=%g C=%g V=%d"
            idx a.BucketStartNs a.Open a.High a.Low a.Close a.Volume
                b.BucketStartNs b.Open b.High b.Low b.Close b.Volume
    exit 1
