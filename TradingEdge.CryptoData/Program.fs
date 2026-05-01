module TradingEdge.CryptoData.Program

open System
open System.IO
open System.Net.Http
open System.Threading
open Argu
open TradingEdge.CryptoData.Universe
open TradingEdge.CryptoData.Manifest
open TradingEdge.CryptoData.PerpsDownload

let private defaultUniversePath = "data/crypto/perps_universe.json"
let private defaultOutputDir = "/mnt/d/trading-edge-bulk/crypto/binance/perps"
let private defaultParallelism = 4

// =============================================================================
// download-universe
// =============================================================================

type DownloadUniverseArgs =
    | [<AltCommandLine("-o")>] Output of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Output _ -> sprintf "Output JSON path. Default: %s" defaultUniversePath

let runDownloadUniverse (args: ParseResults<DownloadUniverseArgs>) : Async<int> =
    async {
        let outPath = args.GetResult(Output, defaultValue = defaultUniversePath)
        use http = new HttpClient(Timeout = TimeSpan.FromSeconds 60.0)
        let! entries = fetchUniverse http
        writeUniverse outPath entries
        printfn "Wrote %s" outPath
        return 0
    }

// =============================================================================
// Shared: window parsing + symbol selection
// =============================================================================

let private parseDateOpt (s: string option) (fallback: unit -> DateTime) : DateTime =
    match s with
    | Some str -> DateTime.ParseExact(str, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
    | None -> fallback ()

let private resolveSymbols
    (universeFile: string)
    (explicitSymbols: string list)
    (activeOnly: bool)
    : string[] =
    match explicitSymbols with
    | [] ->
        if not (File.Exists universeFile) then
            eprintfn "Universe file not found: %s. Run `download-universe` first." universeFile
            exit 1
        let entries = readUniverse universeFile
        let filtered =
            if activeOnly then entries |> Array.filter (fun e -> e.Status = Active)
            else entries
        filtered |> Array.map (fun e -> e.Symbol)
    | xs -> xs |> List.toArray

let private printManifestSummary (stats: ManifestStats) (showTopBottom: bool) : unit =
    printfn ""
    printfn "Manifest:"
    printfn "  symbols in universe: %d" stats.Symbols
    printfn "  symbols with data:   %d" stats.SymbolsWithData
    printfn "  monthly archives:    %s" (stats.MonthlyJobs.ToString("N0"))
    printfn "  daily archives:      %s" (stats.DailyJobs.ToString("N0"))
    printfn "  total compressed:    %.2f GB  (%.2f TB)"
        (float stats.TotalBytes / 1.0e9)
        (float stats.TotalBytes / 1.0e12)
    if showTopBottom && stats.PerSymbol.Length > 0 then
        printfn ""
        printfn "Top 20 by bytes:"
        printfn "  %-20s %-8s %-8s %-12s" "symbol" "monthly" "daily" "size_GB"
        for (sym, nm, nd, b) in stats.PerSymbol |> Array.truncate 20 do
            printfn "  %-20s %-8d %-8d %.3f" sym nm nd (float b / 1.0e9)

// =============================================================================
// estimate-size — uses the same manifest path; numbers are exact.
// =============================================================================

type EstimateSizeArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-t")>] Symbol of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | [<AltCommandLine("-u")>] Universe_File of string
    | Active_Only
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "First date inclusive (yyyy-MM-dd). Default: 2 years before --end-date."
            | End_Date _ -> "Last date inclusive (yyyy-MM-dd). Default: yesterday."
            | Symbol _ -> "Symbol (repeatable). Default: every symbol in the universe file."
            | Parallelism _ -> "Max concurrent S3 listings. Default: 16."
            | Universe_File _ -> sprintf "Universe JSON. Default: %s" defaultUniversePath
            | Active_Only -> "Skip symbols tagged delisted_or_archived."

let runEstimateSize (args: ParseResults<EstimateSizeArgs>) : Async<int> =
    async {
        let endDate = parseDateOpt (args.TryGetResult End_Date) (fun () -> DateTime.UtcNow.Date.AddDays(-1.0))
        let startDate = parseDateOpt (args.TryGetResult Start_Date) (fun () -> endDate.AddYears(-2))
        let parallelism = args.GetResult(Parallelism, defaultValue = 16)
        let universeFile = args.GetResult(Universe_File, defaultValue = defaultUniversePath)
        let activeOnly = args.Contains Active_Only
        let symbols = resolveSymbols universeFile (args.GetResults Symbol) activeOnly
        printfn "Building manifest for %d symbols, %s .. %s..."
            symbols.Length (startDate.ToString("yyyy-MM-dd")) (endDate.ToString("yyyy-MM-dd"))
        use http = new HttpClient(Timeout = TimeSpan.FromMinutes 2.0)
        let sw = Diagnostics.Stopwatch.StartNew()
        let! jobs = buildManifest http symbols startDate endDate parallelism
        sw.Stop()
        let stats = summarize symbols.Length jobs
        printfn "Manifest built in %.0fs" sw.Elapsed.TotalSeconds
        printManifestSummary stats true
        return 0
    }

// =============================================================================
// download-perps — manifest-driven, no 404s
// =============================================================================

type DownloadPerpsArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-t")>] Symbol of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | [<AltCommandLine("-l")>] List_Parallelism of int
    | [<AltCommandLine("-u")>] Universe_File of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | Active_Only
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "First date inclusive (yyyy-MM-dd). Default: 2 years before --end-date."
            | End_Date _ -> "Last date inclusive (yyyy-MM-dd). Default: yesterday."
            | Symbol _ -> "Symbol (repeatable). Default: every symbol in the universe file."
            | Parallelism _ -> sprintf "Max concurrent downloads. Default: %d. Each in-flight monthly can hold ~250 MB of appender state, so 4 keeps the working set ~1 GB." defaultParallelism
            | List_Parallelism _ -> "Max concurrent S3 listings during manifest build. Default: 16."
            | Universe_File _ -> sprintf "Universe JSON. Default: %s" defaultUniversePath
            | Output_Dir _ -> sprintf "Output root. Default: %s" defaultOutputDir
            | Active_Only -> "Skip symbols tagged delisted_or_archived."

let runDownloadPerps (args: ParseResults<DownloadPerpsArgs>) : Async<int> =
    async {
        let endDate = parseDateOpt (args.TryGetResult End_Date) (fun () -> DateTime.UtcNow.Date.AddDays(-1.0))
        let startDate = parseDateOpt (args.TryGetResult Start_Date) (fun () -> endDate.AddYears(-2))
        let parallelism = args.GetResult(Parallelism, defaultValue = defaultParallelism)
        let listParallelism = args.GetResult(List_Parallelism, defaultValue = 16)
        let universeFile = args.GetResult(Universe_File, defaultValue = defaultUniversePath)
        let outputDir = args.GetResult(Output_Dir, defaultValue = defaultOutputDir)
        let activeOnly = args.Contains Active_Only

        let symbols = resolveSymbols universeFile (args.GetResults Symbol) activeOnly
        Directory.CreateDirectory outputDir |> ignore

        printfn "Building manifest for %d symbols, %s .. %s..."
            symbols.Length (startDate.ToString("yyyy-MM-dd")) (endDate.ToString("yyyy-MM-dd"))
        use http = new HttpClient(Timeout = TimeSpan.FromMinutes 30.0)  // allow huge monthlies
        let listSw = Diagnostics.Stopwatch.StartNew()
        let! jobs = buildManifest http symbols startDate endDate listParallelism
        listSw.Stop()
        let stats = summarize symbols.Length jobs
        printfn "Manifest built in %.0fs" listSw.Elapsed.TotalSeconds
        printManifestSummary stats false
        printfn ""
        printfn "Output:     %s" outputDir
        printfn "Parallel:   %d" parallelism
        printfn ""

        let sw = Diagnostics.Stopwatch.StartNew()
        let! results = downloadBatch http outputDir jobs parallelism consoleProgress CancellationToken.None
        sw.Stop()

        let mutable nDailyOk, nMonthlyOk, nSkip, nFail = 0, 0, 0, 0
        let mutable totalBytes = 0L
        let mutable totalTrades = 0L
        for r in results do
            match r with
            | DailyDownloaded(_, _, n, sz) ->
                nDailyOk <- nDailyOk + 1
                totalBytes <- totalBytes + sz
                totalTrades <- totalTrades + int64 n
            | MonthlyDownloaded(_, _, _, n, _, sz) ->
                nMonthlyOk <- nMonthlyOk + 1
                totalBytes <- totalBytes + sz
                totalTrades <- totalTrades + int64 n
            | Skipped _ -> nSkip <- nSkip + 1
            | Failed _ -> nFail <- nFail + 1
        printfn ""
        printfn "Done in %.0fs:" sw.Elapsed.TotalSeconds
        printfn "  daily archives ok:   %d" nDailyOk
        printfn "  monthly archives ok: %d" nMonthlyOk
        printfn "  skipped (cached):    %d" nSkip
        printfn "  failed:              %d" nFail
        printfn "  bytes processed:     %.2f GB" (float totalBytes / 1.0e9)
        printfn "  trades ingested:     %s" (totalTrades.ToString("N0"))
        if nFail > 0 then
            printfn ""
            printfn "Failures:"
            for r in results do
                match r with
                | Failed(key, err) -> printfn "  %s: %s" key err
                | _ -> ()
        return if nFail > 0 then 1 else 0
    }

// =============================================================================
// verify-perps — coverage report against on-disk parquets
// =============================================================================

type VerifyPerpsArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-u")>] Universe_File of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "First date inclusive (yyyy-MM-dd)."
            | End_Date _ -> "Last date inclusive (yyyy-MM-dd)."
            | Universe_File _ -> sprintf "Universe JSON. Default: %s" defaultUniversePath
            | Output_Dir _ -> sprintf "Output root. Default: %s" defaultOutputDir

let runVerifyPerps (args: ParseResults<VerifyPerpsArgs>) : int =
    let endDate = parseDateOpt (args.TryGetResult End_Date) (fun () -> DateTime.UtcNow.Date.AddDays(-1.0))
    let startDate = parseDateOpt (args.TryGetResult Start_Date) (fun () -> endDate.AddYears(-2))
    let universeFile = args.GetResult(Universe_File, defaultValue = defaultUniversePath)
    let outputDir = args.GetResult(Output_Dir, defaultValue = defaultOutputDir)
    let entries = readUniverse universeFile

    printfn "Coverage report (%s .. %s) over %s:"
        (startDate.ToString("yyyy-MM-dd"))
        (endDate.ToString("yyyy-MM-dd"))
        outputDir
    printfn ""
    printfn "%-20s %-12s %-12s %-8s %-8s %-8s"
        "symbol" "first" "last" "n_have" "n_gap" "status"

    let dates =
        let n = (endDate - startDate).Days + 1
        Array.init (max 0 n) (fun i -> startDate.AddDays(float i))
    let mutable totalHave = 0
    let mutable totalGap = 0
    for entry in entries do
        let dir = Path.Combine(outputDir, entry.Symbol)
        let presentDates =
            dates
            |> Array.filter (fun d ->
                let p = Path.Combine(dir, sprintf "%s-trades-%s.parquet" entry.Symbol (d.ToString("yyyy-MM-dd")))
                File.Exists p)
        if presentDates.Length > 0 then
            let first = Array.head presentDates
            let last = Array.last presentDates
            let activeWindowDays = (last - first).Days + 1
            let gapsInside = activeWindowDays - presentDates.Length
            totalHave <- totalHave + presentDates.Length
            totalGap <- totalGap + gapsInside
            printfn "%-20s %-12s %-12s %-8d %-8d %s"
                entry.Symbol
                (first.ToString("yyyy-MM-dd"))
                (last.ToString("yyyy-MM-dd"))
                presentDates.Length
                gapsInside
                (match entry.Status with Active -> "active" | _ -> "delisted")
    printfn ""
    printfn "Totals: %d files present, %d gaps inside active windows" totalHave totalGap
    0

// =============================================================================
// CLI dispatcher
// =============================================================================

type Command =
    | [<CliPrefix(CliPrefix.None)>] Download_Universe of ParseResults<DownloadUniverseArgs>
    | [<CliPrefix(CliPrefix.None)>] Estimate_Size of ParseResults<EstimateSizeArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Perps of ParseResults<DownloadPerpsArgs>
    | [<CliPrefix(CliPrefix.None)>] Verify_Perps of ParseResults<VerifyPerpsArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Download_Universe _ -> "Fetch active + archived USDT-perp symbols and write the universe JSON."
            | Estimate_Size _ -> "Build the manifest from S3 listings and report total bytes — no data fetched."
            | Download_Perps _ -> "Bulk-download trade archives via the S3-listing manifest, convert to per-day parquet."
            | Verify_Perps _ -> "Per-symbol coverage report against on-disk parquets."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Command>(programName = "TradingEdge.CryptoData")
    try
        let parsed = parser.Parse(argv, raiseOnUsage = true)
        match parsed.GetSubCommand() with
        | Download_Universe a -> runDownloadUniverse a |> Async.RunSynchronously
        | Estimate_Size a -> runEstimateSize a |> Async.RunSynchronously
        | Download_Perps a -> runDownloadPerps a |> Async.RunSynchronously
        | Verify_Perps a -> runVerifyPerps a
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
