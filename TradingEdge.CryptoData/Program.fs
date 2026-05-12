module TradingEdge.CryptoData.Program

open System
open System.IO
open System.Net.Http
open System.Threading
open Argu
open TradingEdge.CryptoData.Universe
open TradingEdge.CryptoData.Manifest
open TradingEdge.CryptoData.PerpsDownload
open TradingEdge.CryptoData.BarPreprocess
open TradingEdge.CryptoData.FundingDownload

let private defaultUniversePath = "data/crypto/perps_universe.json"
let private defaultOutputDir = "/mnt/d/trading-edge-bulk/crypto/binance/perps"
let private defaultBarsDir = "/mnt/d/trading-edge-bulk/crypto/binance/perps_bars"
let private defaultFundingDir = "/mnt/d/trading-edge-bulk/crypto/binance/perps_funding"
// Two-stage pipeline (2026-05-12): downloads and conversions run on
// independent worker pools connected by an unbounded channel.
//
//   * Download workers: network-bound. 4 keeps a Gigabit link saturated
//     without triggering Binance rate-limits in practice.
//   * Convert workers: DuckDB's read_csv is multi-threaded internally, and
//     a single monthly conversion can pull 2-3 GB of CSV through memory.
//     Default 1 — running multiple in parallel OOMs on the largest months.
let private defaultDownloadParallelism = 4
let private defaultConvertParallelism = 1

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
    | [<AltCommandLine("-dp")>] Download_Parallelism of int
    | [<AltCommandLine("-cp")>] Convert_Parallelism of int
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
            | Download_Parallelism _ -> sprintf "Concurrent HTTP downloads. Default: %d. Network-bound; keep at 4 for a Gigabit link." defaultDownloadParallelism
            | Convert_Parallelism _ -> sprintf "Concurrent DuckDB conversions. Default: %d. DuckDB is multi-threaded internally; multiple parallel monthly conversions OOM on the largest archives." defaultConvertParallelism
            | List_Parallelism _ -> "Max concurrent S3 listings during manifest build. Default: 16."
            | Universe_File _ -> sprintf "Universe JSON. Default: %s" defaultUniversePath
            | Output_Dir _ -> sprintf "Output root. Default: %s" defaultOutputDir
            | Active_Only -> "Skip symbols tagged delisted_or_archived."

let runDownloadPerps (args: ParseResults<DownloadPerpsArgs>) : Async<int> =
    async {
        let endDate = parseDateOpt (args.TryGetResult End_Date) (fun () -> DateTime.UtcNow.Date.AddDays(-1.0))
        let startDate = parseDateOpt (args.TryGetResult Start_Date) (fun () -> endDate.AddYears(-2))
        let downloadParallelism = args.GetResult(Download_Parallelism, defaultValue = defaultDownloadParallelism)
        let convertParallelism = args.GetResult(Convert_Parallelism, defaultValue = defaultConvertParallelism)
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
        printfn "Download workers:  %d" downloadParallelism
        printfn "Convert workers:   %d" convertParallelism
        printfn ""

        let sw = Diagnostics.Stopwatch.StartNew()
        let! results =
            runPipeline http outputDir jobs downloadParallelism convertParallelism consoleProgress CancellationToken.None
            |> Async.AwaitTask
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
// build-bars — preprocess per-day trade parquets into per-(symbol, tf) OHLCV bars
// =============================================================================

type BuildBarsArgs =
    | [<AltCommandLine("-t")>] Symbol of string
    | [<AltCommandLine("-f")>] Timeframe of string
    | [<AltCommandLine("-i")>] Trades_Dir of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-u")>] Universe_File of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | Active_Only
    | Overwrite
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Symbol _ -> "Symbol (repeatable). Default: every symbol in the universe file."
            | Timeframe _ -> "Timeframe (repeatable, e.g. 1h 2h 4h). Default: 1h, 2h, 4h."
            | Trades_Dir _ -> sprintf "Trade-parquet root. Default: %s" defaultOutputDir
            | Output_Dir _ -> sprintf "Bar-parquet root. Default: %s" defaultBarsDir
            | Universe_File _ -> sprintf "Universe JSON. Default: %s" defaultUniversePath
            | Parallelism _ -> "Max symbols processed concurrently. Default: 4."
            | Active_Only -> "Skip symbols tagged delisted_or_archived."
            | Overwrite -> "Re-build even if all output parquets exist."

let runBuildBars (args: ParseResults<BuildBarsArgs>) : int =
    let tradesDir = args.GetResult(Trades_Dir, defaultValue = defaultOutputDir)
    let barsDir = args.GetResult(BuildBarsArgs.Output_Dir, defaultValue = defaultBarsDir)
    let universeFile = args.GetResult(BuildBarsArgs.Universe_File, defaultValue = defaultUniversePath)
    let parallelism = args.GetResult(BuildBarsArgs.Parallelism, defaultValue = 4)
    let activeOnly = args.Contains BuildBarsArgs.Active_Only
    let overwrite = args.Contains Overwrite
    let timeframes =
        match args.GetResults BuildBarsArgs.Timeframe with
        | [] -> [| "1h"; "2h"; "4h" |]
        | xs -> xs |> List.toArray
    let symbols = resolveSymbols universeFile (args.GetResults BuildBarsArgs.Symbol) activeOnly
    Directory.CreateDirectory barsDir |> ignore

    printfn "Building %d-symbol × %d-timeframe bar parquets" symbols.Length timeframes.Length
    printfn "  trades: %s" tradesDir
    printfn "  bars:   %s" barsDir
    printfn "  parallel: %d" parallelism
    printfn "  timeframes: %s" (String.concat "," timeframes)
    printfn ""

    let writeLock = obj()
    let mutable doneCount = 0
    let mutable failCount = 0
    let mutable totalTrades = 0L
    let total = symbols.Length

    let sw = Diagnostics.Stopwatch.StartNew()
    let opts = Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let symSw = Diagnostics.Stopwatch.StartNew()
            match buildSymbol tradesDir barsDir symbol timeframes overwrite with
            | Ok (n, counts) ->
                lock writeLock (fun () ->
                    doneCount <- doneCount + 1
                    totalTrades <- totalTrades + n
                    let countsStr =
                        Array.zip timeframes counts
                        |> Array.map (fun (tf, c) -> sprintf "%s=%d" tf c)
                        |> String.concat " "
                    printfn "[bars] %4d/%-4d %-20s %s trades, %s bars, %.1fs"
                        doneCount total symbol (n.ToString("N0")) countsStr
                        symSw.Elapsed.TotalSeconds)
            | Error msg ->
                lock writeLock (fun () ->
                    failCount <- failCount + 1
                    eprintfn "[bars] FAIL %s: %s" symbol msg))
    |> ignore
    sw.Stop()
    printfn ""
    printfn "Done in %.0fs:" sw.Elapsed.TotalSeconds
    printfn "  symbols ok:    %d" doneCount
    printfn "  symbols fail:  %d" failCount
    printfn "  trades read:   %s" (totalTrades.ToString("N0"))
    if failCount > 0 then 1 else 0

// =============================================================================
// download-funding — bulk-download funding rate archives, write per-symbol parquet
// =============================================================================

type DownloadFundingArgs =
    | [<AltCommandLine("-t")>] Symbol of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-u")>] Universe_File of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | Active_Only
    | Overwrite
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Symbol _ -> "Symbol (repeatable). Default: every symbol in the universe file."
            | Output_Dir _ -> sprintf "Funding-parquet root. Default: %s" defaultFundingDir
            | Universe_File _ -> sprintf "Universe JSON. Default: %s" defaultUniversePath
            | Parallelism _ -> "Max symbols downloaded concurrently. Default: 8 (funding archives are tiny)."
            | Active_Only -> "Skip symbols tagged delisted_or_archived."
            | Overwrite -> "Re-download even if a sentinel says we already have this symbol."

let runDownloadFunding (args: ParseResults<DownloadFundingArgs>) : Async<int> =
    async {
        let outputDir = args.GetResult(DownloadFundingArgs.Output_Dir, defaultValue = defaultFundingDir)
        let universeFile = args.GetResult(DownloadFundingArgs.Universe_File, defaultValue = defaultUniversePath)
        let parallelism = args.GetResult(DownloadFundingArgs.Parallelism, defaultValue = 8)
        let activeOnly = args.Contains DownloadFundingArgs.Active_Only
        let overwrite = args.Contains DownloadFundingArgs.Overwrite
        let symbols = resolveSymbols universeFile (args.GetResults DownloadFundingArgs.Symbol) activeOnly
        Directory.CreateDirectory outputDir |> ignore

        printfn "Downloading funding for %d symbols" symbols.Length
        printfn "  output: %s" outputDir
        printfn "  parallel: %d" parallelism
        printfn ""

        use http = new HttpClient(Timeout = TimeSpan.FromMinutes 5.0)
        let writeLock = obj()
        let mutable nDone = 0
        let mutable nSkip = 0
        let mutable nNoData = 0
        let mutable nFail = 0
        let mutable nRows = 0

        let sw = Diagnostics.Stopwatch.StartNew()
        use sem = new SemaphoreSlim(parallelism)
        let total = symbols.Length
        let tasks =
            symbols
            |> Array.map (fun symbol ->
                async {
                    do! sem.WaitAsync() |> Async.AwaitTask
                    try
                        let symSw = Diagnostics.Stopwatch.StartNew()
                        let! result =
                            downloadSymbol http outputDir symbol overwrite CancellationToken.None
                        lock writeLock (fun () ->
                            match result with
                            | DownloadedFunding(s, m, n) ->
                                nDone <- nDone + 1
                                nRows <- nRows + n
                                printfn "[funding] %4d/%-4d %-20s %d months, %d rows, %.1fs"
                                    (nDone + nSkip + nNoData + nFail) total s m n
                                    symSw.Elapsed.TotalSeconds
                            | SkippedFunding s ->
                                nSkip <- nSkip + 1
                                printfn "[funding] %4d/%-4d %-20s SKIP (cached)"
                                    (nDone + nSkip + nNoData + nFail) total s
                            | NoFundingData s ->
                                nNoData <- nNoData + 1
                                printfn "[funding] %4d/%-4d %-20s no funding data"
                                    (nDone + nSkip + nNoData + nFail) total s
                            | FailedFunding(s, err) ->
                                nFail <- nFail + 1
                                eprintfn "[funding] %-20s FAIL: %s" s err)
                    finally
                        sem.Release() |> ignore
                })
        let! _ = tasks |> Async.Parallel
        sw.Stop()
        printfn ""
        printfn "Done in %.0fs:" sw.Elapsed.TotalSeconds
        printfn "  downloaded:    %d" nDone
        printfn "  skipped:       %d" nSkip
        printfn "  no funding:    %d" nNoData
        printfn "  failed:        %d" nFail
        printfn "  rows total:    %s" (nRows.ToString("N0"))
        return if nFail > 0 then 1 else 0
    }

// =============================================================================
// CLI dispatcher
// =============================================================================

type Command =
    | [<CliPrefix(CliPrefix.None)>] Download_Universe of ParseResults<DownloadUniverseArgs>
    | [<CliPrefix(CliPrefix.None)>] Estimate_Size of ParseResults<EstimateSizeArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Perps of ParseResults<DownloadPerpsArgs>
    | [<CliPrefix(CliPrefix.None)>] Verify_Perps of ParseResults<VerifyPerpsArgs>
    | [<CliPrefix(CliPrefix.None)>] Build_Bars of ParseResults<BuildBarsArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Funding of ParseResults<DownloadFundingArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Download_Universe _ -> "Fetch active + archived USDT-perp symbols and write the universe JSON."
            | Estimate_Size _ -> "Build the manifest from S3 listings and report total bytes — no data fetched."
            | Download_Perps _ -> "Bulk-download trade archives via the S3-listing manifest, convert to per-day parquet."
            | Verify_Perps _ -> "Per-symbol coverage report against on-disk parquets."
            | Build_Bars _ -> "Aggregate per-day trade parquets into per-(symbol, timeframe) OHLCV bar parquets."
            | Download_Funding _ -> "Bulk-download monthly funding-rate archives, write per-symbol parquet."

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
        | Build_Bars a -> runBuildBars a
        | Download_Funding a -> runDownloadFunding a |> Async.RunSynchronously
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
