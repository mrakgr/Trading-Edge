module TradingEdge.ReplaySimulatorV1.Program

// CLI smoke test for the snapshot-based replay architecture.
//
// Usage:
//   dotnet run --project TradingEdge.ReplaySimulatorV1 -- <day-dir>
//
// e.g.
//   dotnet run --project TradingEdge.ReplaySimulatorV1 -- data/databento/mbo/EOSE/2026-05-13
//
// Builds the SnapshotStore for the day, then calls play(t) at a few hand-picked
// times and dumps a summary of each result.

open System
open System.IO
open System.Diagnostics
open FSharp.Control
open TradingEdge.ReplaySimulatorV1.Dbn
open TradingEdge.ReplaySimulatorV1.MboReader
open TradingEdge.ReplaySimulatorV1.Bars
open TradingEdge.ReplaySimulatorV1.Book
open TradingEdge.ReplaySimulatorV1.Snapshots
open TradingEdge.ReplaySimulatorV1.Play

let private NY_TZ =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

let private nyTimeOf (utcNs: int64) =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ)

let private nyHmmssOf (utcNs: int64) =
    if utcNs = Int64.MinValue then "--:--:--"
    else (nyTimeOf utcNs).ToString("HH:mm:ss")

/// UTC ns at HH:MM:SS NY on the same NY-local date as `referenceUtcNs`.
let private nyTimeOnSameDay (referenceUtcNs: int64) (hh: int) (mm: int) (ss: int) : int64 =
    let refNy = nyTimeOf referenceUtcNs
    let target = DateTime(refNy.Year, refNy.Month, refNy.Day, hh, mm, ss, DateTimeKind.Unspecified)
    let utc = TimeZoneInfo.ConvertTimeToUtc(target, NY_TZ)
    let epoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    (utc.Ticks - epoch.Ticks) * 100L

/// Open a single .dbn.zst venue file. Returns (stream, publisherId-from-metadata).
/// The metadata reader leaves the stream positioned at the first record.
let openVenue (path: string) : Stream * DbnMetadata =
    let fs : Stream = File.OpenRead(path) :> Stream
    let zs : Stream =
        if path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) then
            new ZstdSharp.DecompressionStream(fs) :> Stream
        else fs
    let metadata = Dbn.readMetadata(zs).Result
    if metadata.Schema <> Dbn.SCHEMA_MBO then
        zs.Dispose()
        failwithf "%s has schema=%d, expected MBO (0)" path metadata.Schema
    zs, metadata

let private summarizeBook (book: L3Book) =
    // Top-3 bids (highest prices first) and top-3 asks (lowest prices first).
    let topBids =
        book.Bids
        |> Map.toSeq
        |> Seq.sortByDescending fst
        |> Seq.truncate 3
        |> Seq.toList
    let topAsks =
        book.Asks
        |> Map.toSeq
        |> Seq.sortBy fst
        |> Seq.truncate 3
        |> Seq.toList
    let priceStr (p: int64) = sprintf "$%.4f" (priceToUsd p)
    let levelStr (p, q: System.Collections.Immutable.ImmutableList<Order>) =
        let totalSize = q |> Seq.sumBy (fun o -> int64 o.Size)
        sprintf "%s x %d (%d orders)" (priceStr p) totalSize q.Count
    let join s = String.concat ", " s
    let bidsStr = topBids |> List.map levelStr |> join
    let asksStr = topAsks |> List.map levelStr |> join
    sprintf "bids: %s | asks: %s" bidsStr asksStr

let private dumpResult (label: string) (r: PlayResult) =
    printfn ""
    printfn "===== play(%s) — %s ET =====" label (nyHmmssOf r.Time)
    printfn "Venues with books: %d" r.Books.Count
    for kv in r.Books do
        let totalBidOrders = kv.Value.Bids |> Map.toSeq |> Seq.sumBy (fun (_, q) -> q.Count)
        let totalAskOrders = kv.Value.Asks |> Map.toSeq |> Seq.sumBy (fun (_, q) -> q.Count)
        printfn "  publisher %d: %d bid levels, %d ask levels, %d+%d resting orders"
            kv.Key kv.Value.Bids.Count kv.Value.Asks.Count totalBidOrders totalAskOrders
        if kv.Value.Bids.Count > 0 || kv.Value.Asks.Count > 0 then
            printfn "    %s" (summarizeBook kv.Value)
    let totalCompletedTrades = r.TradeBuckets |> Seq.sumBy (fun arr -> int64 arr.Length)
    printfn "Trades: %d in completed buckets + %d in tail" totalCompletedTrades r.TradeTail.Length
    let totalCompletedBars = r.BarBuckets |> Seq.sumBy (fun arr -> int64 arr.Length)
    printfn "Bars: %d completed in buckets + %d in tail + %s forming"
        totalCompletedBars r.BarTail.Length (if r.FormingBar.IsSome then "1" else "0")
    // Last 5 bars (from tail first; if tail is short, fall back into the prior bucket).
    let lastBars =
        let tail = r.BarTail |> Seq.toList
        if tail.Length >= 5 then tail |> List.skip (tail.Length - 5)
        else
            let prior =
                if r.BarBuckets.Count = 0 then []
                else r.BarBuckets.[r.BarBuckets.Count - 1] |> Seq.toList
            (prior @ tail) |> List.rev |> List.truncate 5 |> List.rev
    if not lastBars.IsEmpty then
        printfn "Last %d closed 1m bars:" lastBars.Length
        for b in lastBars do
            let vwapStr =
                match b.SessionVwap with
                | Some v -> sprintf "%.4f" v
                | None -> "--"
            printfn "  %s  O=%.4f H=%.4f L=%.4f C=%.4f V=%d VWAP=%s"
                (nyHmmssOf b.BucketStartNs) b.Open b.High b.Low b.Close b.Volume vwapStr
    match r.FormingBar with
    | None -> ()
    | Some b ->
        printfn "Forming bar at %s: O=%.4f H=%.4f L=%.4f C=%.4f V=%d (trades=%d)"
            (nyHmmssOf b.BucketStartNs) b.Open b.High b.Low b.Close b.Volume b.TradeCount

[<EntryPoint>]
let main argv =
    if argv.Length <> 1 then
        eprintfn "Usage: TradingEdge.ReplaySimulatorV1 <day-dir>"
        eprintfn "  e.g. data/databento/mbo/EOSE/2026-05-13"
        1
    else

    let dayDir = argv.[0]
    if not (Directory.Exists dayDir) then
        eprintfn "Directory not found: %s" dayDir
        2
    else

    let files = Directory.GetFiles(dayDir, "*.dbn.zst") |> Array.sort
    if files.Length = 0 then
        eprintfn "No .dbn.zst files in %s" dayDir
        2
    else

    printfn "Loading %d venue files from %s" files.Length dayDir
    let sw = Stopwatch.StartNew()
    let venues = files |> Array.map openVenue
    try
        let streams = venues |> Array.map fst |> Array.toList
        let merged = mergeByTsEvent (streams |> List.map readMboRecords)
        printfn "  metadata parsed in %.2fs; building snapshot store..." sw.Elapsed.TotalSeconds
        sw.Restart()
        let store = (buildAsync merged).Result
        sw.Stop()
        printfn "  built %d snapshots (%d records) in %.2fs"
            store.Snapshots.Count store.Records.Length sw.Elapsed.TotalSeconds
        if store.Records.Length = 0 then 0
        else

        let referenceNs = store.Records.[0].TsEvent
        let player = Player(store)
        let runAt label hh mm ss =
            let t = nyTimeOnSameDay referenceNs hh mm ss
            let last = store.Records.[store.Records.Length - 1].TsEvent
            let tClamped = min t last
            let r = player.Play tClamped
            dumpResult label r

        runAt "09:30:00 ET" 9 30 0
        runAt "10:00:00 ET" 10 0 0
        runAt "16:00:00 ET" 16 0 0
        // Memoization probe: same time twice.
        printfn ""
        printfn "===== memoization probe ====="
        let probeT = nyTimeOnSameDay referenceNs 10 0 0 |> min store.Records.[store.Records.Length - 1].TsEvent
        let r1 = player.Play probeT
        let r2 = player.Play probeT
        printfn "  identical-result reference equality: %b" (obj.ReferenceEquals(r1, r2))
        0
    finally
        for (s, _) in venues do
            try s.Dispose() with _ -> ()
