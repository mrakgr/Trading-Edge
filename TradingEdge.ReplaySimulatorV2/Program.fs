module TradingEdge.ReplaySimulatorV2.Program

// Entry point.
//   chart <SYM> <yyyy-MM-dd>    -> build snapshot store for that day, launch GUI
//   chart <day-dir>             -> same, but the dir is given directly
//   scan  <day-dir>             -> CLI smoke test: print play(t) at a few times
//   (no args)                   -> chart EOSE 2026-05-13

open System
open System.IO
open System.Diagnostics
open FSharp.Control
open Avalonia
open TradingEdge.ReplaySimulatorV2
open TradingEdge.ReplaySimulatorV2.Dbn
open TradingEdge.ReplaySimulatorV2.MboReader
open TradingEdge.ReplaySimulatorV2.Bars
open TradingEdge.ReplaySimulatorV2.Book
open TradingEdge.ReplaySimulatorV2.Snapshots
open TradingEdge.ReplaySimulatorV2.Play

let private NY_TZ =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private nyTimeOf (utcNs: int64) =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ)

let private nyHmmssOf (utcNs: int64) =
    if utcNs = Int64.MinValue then "--:--:--"
    else (nyTimeOf utcNs).ToString("HH:mm:ss")

let private nyTimeOnSameDay (referenceUtcNs: int64) (hh: int) (mm: int) (ss: int) : int64 =
    let refNy = nyTimeOf referenceUtcNs
    let target = DateTime(refNy.Year, refNy.Month, refNy.Day, hh, mm, ss, DateTimeKind.Unspecified)
    let utc = TimeZoneInfo.ConvertTimeToUtc(target, NY_TZ)
    let epoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    (utc.Ticks - epoch.Ticks) * 100L

let private defaultDataRoot =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "data", "databento", "mbo")
    |> Path.GetFullPath

let private resolveDayDir (a: string) (b: string option) : string * string * string =
    match b with
    | Some date ->
        let dir = Path.Combine(defaultDataRoot, a.ToUpperInvariant(), date)
        if not (Directory.Exists dir) then failwithf "Day directory not found: %s" dir
        dir, a.ToUpperInvariant(), date
    | None ->
        if not (Directory.Exists a) then failwithf "Directory not found: %s" a
        let parts = a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       .Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
        let n = parts.Length
        let date = if n >= 1 then parts.[n - 1] else ""
        let sym  = if n >= 2 then parts.[n - 2] else "?"
        a, sym, date

let openVenue (path: string) : Stream =
    let fs : Stream = File.OpenRead(path) :> Stream
    let zs : Stream =
        if path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) then
            new ZstdSharp.DecompressionStream(fs) :> Stream
        else fs
    let metadata = (Dbn.readMetadata zs).Result
    if metadata.Schema <> Dbn.SCHEMA_MBO then
        zs.Dispose()
        failwithf "%s has schema=%d, expected MBO (0)" path metadata.Schema
    zs

let private buildStore (dayDir: string) : SnapshotStore * Stream[] =
    let files = Directory.GetFiles(dayDir, "*.dbn.zst") |> Array.sort
    if files.Length = 0 then failwithf "No .dbn.zst files in %s" dayDir
    let streams = files |> Array.map openVenue
    let merged =
        streams
        |> Array.map readMboRecords
        |> Array.toList
        |> mergeByTsEvent
    let store = (buildAsync merged).Result
    store, streams

let private dumpResult (label: string) (r: PlayResult) =
    printfn ""
    printfn "===== play(%s) — %s ET =====" label (nyHmmssOf r.Time)
    printfn "Venues with books: %d" r.Books.Count
    let totalCompletedTrades = r.TradeBuckets |> Seq.sumBy (fun arr -> int64 arr.Length)
    printfn "Trades: %d in completed buckets + %d in tail" totalCompletedTrades r.TradeTail.Length
    let totalCompletedBars = r.BarBuckets |> Seq.sumBy (fun arr -> int64 arr.Length)
    printfn "Bars: %d completed in buckets + %d in tail + %s forming"
        totalCompletedBars r.BarTail.Length (if r.FormingBar.IsSome then "1" else "0")

let private runScan (dayDir: string) =
    let sw = Stopwatch.StartNew()
    printfn "Loading %s" dayDir
    let store, streams = buildStore dayDir
    sw.Stop()
    try
        printfn "  built %d snapshots (%d records) in %.2fs"
            store.Snapshots.Count store.Records.Length sw.Elapsed.TotalSeconds
        if store.Records.Length > 0 then
            let referenceNs = store.Records.[0].TsEvent
            let player = Player(store)
            let runAt label hh mm ss =
                let t = nyTimeOnSameDay referenceNs hh mm ss
                let lastTs = store.Records.[store.Records.Length - 1].TsEvent
                player.Play(min t lastTs) |> dumpResult label
            runAt "09:30:00 ET" 9 30 0
            runAt "10:00:00 ET" 10 0 0
            runAt "16:00:00 ET" 16 0 0
    finally
        streams |> Array.iter (fun s -> try s.Dispose() with _ -> ())

let private runChart (dayDir: string) (symbol: string) (date: string) =
    let sw = Stopwatch.StartNew()
    printfn "Loading %s %s from %s ..." symbol date dayDir
    let store, streams = buildStore dayDir
    sw.Stop()
    printfn "  built %d snapshots (%d records) in %.2fs"
        store.Snapshots.Count store.Records.Length sw.Elapsed.TotalSeconds
    if store.Records.Length = 0 then
        printfn "No records — nothing to chart."
        streams |> Array.iter (fun s -> try s.Dispose() with _ -> ())
    else
        let referenceNs = store.Records.[0].TsEvent
        let startCursorNs = nyTimeOnSameDay referenceNs 9 30 0
        try
            let app =
                AppBuilder
                    .Configure<App.App>(fun () -> App.App(symbol, date, store, startCursorNs))
                    .UsePlatformDetect()
                    .LogToTrace()
            app.StartWithClassicDesktopLifetime([||]) |> ignore
        finally
            streams |> Array.iter (fun s -> try s.Dispose() with _ -> ())

[<EntryPoint>]
let main argv =
    try
        match argv with
        | [||] ->
            let dir, sym, date = resolveDayDir "EOSE" (Some "2026-05-13")
            runChart dir sym date
            0
        | [| "scan"; target |] ->
            runScan target
            0
        | [| "chart"; arg |] ->
            let dir, sym, date = resolveDayDir arg None
            runChart dir sym date
            0
        | [| "chart"; sym; date |] ->
            let dir, sym, date = resolveDayDir sym (Some date)
            runChart dir sym date
            0
        | _ ->
            printfn "Usage:"
            printfn "  TradingEdge.ReplaySimulatorV2                          -> chart EOSE 2026-05-13"
            printfn "  TradingEdge.ReplaySimulatorV2 chart <SYM> <yyyy-MM-dd> -> chart that day"
            printfn "  TradingEdge.ReplaySimulatorV2 chart <day-dir>          -> chart that directory"
            printfn "  TradingEdge.ReplaySimulatorV2 scan  <day-dir>          -> print play(t) at a few times"
            1
    with ex ->
        eprintfn "ERROR: %s" ex.Message
        eprintfn "%s" ex.StackTrace
        2
