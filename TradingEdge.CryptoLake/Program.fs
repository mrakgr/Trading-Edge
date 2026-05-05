module TradingEdge.CryptoLake.Program

open System
open System.IO
open System.Threading
open Argu

let private defaultLakeRoot = "/mnt/d/trading-edge-bulk/crypto/lake"
let private defaultTradesRoot = "/mnt/d/trading-edge-bulk/crypto/binance/perps"
let private defaultProfile = "crypto-lake"
let private defaultExchange = "BINANCE_FUTURES"

// =============================================================================
// download
// =============================================================================

type DownloadArgs =
    | [<AltCommandLine("-t")>] Symbol of string
    | [<AltCommandLine("-d")>] Date of string
    | Table of string
    | [<AltCommandLine("-e")>] Exchange of string
    | [<AltCommandLine("-r")>] Lake_Root of string
    | [<AltCommandLine("-p")>] Profile of string
    | [<AltCommandLine("-c")>] Concurrency of int
    | Part_Size_Mb of int
    | Overwrite
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Symbol _ -> "Symbol e.g. BTC-USDT-PERP."
            | Date _ -> "YYYY-MM-DD."
            | Table _ -> "book | book_delta_v2 (default: book)."
            | Exchange _ -> sprintf "Exchange tag. Default: %s" defaultExchange
            | Lake_Root _ -> sprintf "Output root. Default: %s" defaultLakeRoot
            | Profile _ -> sprintf "AWS profile. Default: '%s'" defaultProfile
            | Concurrency _ -> "Concurrent S3 byte ranges. Default: 8."
            | Part_Size_Mb _ -> "Multipart part size in MB. Default: 32."
            | Overwrite -> "Re-download even if the destination file exists."

let runDownload (args: ParseResults<DownloadArgs>) : System.Threading.Tasks.Task<int> =
    task {
        let symbol = args.GetResult Symbol
        let date = DateTime.ParseExact(args.GetResult Date, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
        let table = args.GetResult(Table, defaultValue = "book")
        let exchange = args.GetResult(Exchange, defaultValue = defaultExchange)
        let lakeRoot = args.GetResult(Lake_Root, defaultValue = defaultLakeRoot)
        let profile = args.GetResult(Profile, defaultValue = defaultProfile)
        let concurrency = args.GetResult(Concurrency, defaultValue = 8)
        let partMb = args.GetResult(Part_Size_Mb, defaultValue = 32)
        let overwrite = args.Contains Overwrite
        let partSize = int64 partMb * 1024L * 1024L

        printfn "Download: table=%s exchange=%s symbol=%s date=%s" table exchange symbol (date.ToString("yyyy-MM-dd"))
        printfn "  root: %s" lakeRoot
        printfn "  parallel ranges: %d × %d MB" concurrency partMb
        let dest = Schema.dataPath lakeRoot table exchange symbol date
        printfn "  dest: %s" dest
        printfn ""

        use client = S3.createClient profile
        let sw = Diagnostics.Stopwatch.StartNew()
        let! result =
            Download.downloadOne client lakeRoot table exchange symbol date
                concurrency partSize overwrite CancellationToken.None
        sw.Stop()
        match result with
        | Download.Downloaded(bytes, _) ->
            let mb = float bytes / 1024.0 / 1024.0
            let mbps = mb / sw.Elapsed.TotalSeconds
            printfn "OK: %.1f MB in %.1fs (%.1f MB/s)" mb sw.Elapsed.TotalSeconds mbps
            return 0
        | Download.Skipped ->
            printfn "Skipped: %s already exists (use --overwrite to force)" dest
            return 0
        | Download.Failed err ->
            eprintfn "FAIL: %s" err
            return 1
    }

// =============================================================================
// process-day
// =============================================================================

type ProcessDayArgs =
    | [<AltCommandLine("-t")>] Symbol of string
    | [<AltCommandLine("-d")>] Date of string
    | [<AltCommandLine("-v")>] Volume_Per_Bar of float
    | [<AltCommandLine("-l")>] Lambda_Decay of float
    | [<AltCommandLine("-r")>] Lake_Root of string
    | Trades_Root of string
    | Trades_Symbol of string
    | [<AltCommandLine("-e")>] Exchange of string
    | [<AltCommandLine("-o")>] Output of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Symbol _ -> "Crypto Lake symbol e.g. BTC-USDT-PERP."
            | Date _ -> "YYYY-MM-DD."
            | Volume_Per_Bar _ -> "Volume per bar in base-asset units (BTC). Required."
            | Lambda_Decay _ -> "OBI exponential decay per level index. Default: 0.58."
            | Lake_Root _ -> sprintf "Crypto Lake parquet root. Default: %s" defaultLakeRoot
            | Trades_Root _ -> sprintf "Binance trades parquet root. Default: %s" defaultTradesRoot
            | Trades_Symbol _ -> "Binance trades symbol form (e.g. BTCUSDT). Default: derived by stripping hyphens and -PERP from --symbol."
            | Exchange _ -> sprintf "Crypto Lake exchange tag. Default: %s" defaultExchange
            | Output _ -> "Output CSV path. Default: logs/{symbol}_{date}_obi_volume.csv"

let private deriveTradesSymbol (lakeSymbol: string) : string =
    // BTC-USDT-PERP -> BTCUSDT
    let withoutPerp =
        if lakeSymbol.EndsWith("-PERP", StringComparison.Ordinal)
        then lakeSymbol.Substring(0, lakeSymbol.Length - 5)
        else lakeSymbol
    withoutPerp.Replace("-", "")

let runProcessDay (args: ParseResults<ProcessDayArgs>) : int =
    let symbol = args.GetResult Symbol
    let date = DateTime.ParseExact(args.GetResult Date, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
    let volumePerBar = args.GetResult Volume_Per_Bar
    let lambdaDecay = args.GetResult(Lambda_Decay, defaultValue = 0.58)
    let lakeRoot = args.GetResult(Lake_Root, defaultValue = defaultLakeRoot)
    let tradesRoot = args.GetResult(Trades_Root, defaultValue = defaultTradesRoot)
    let exchange = args.GetResult(Exchange, defaultValue = defaultExchange)
    let tradesSymbol =
        args.GetResult(Trades_Symbol, defaultValue = deriveTradesSymbol symbol)

    let bookParquet = Schema.dataPath lakeRoot "book" exchange symbol date
    let tradesParquet =
        Path.Combine(
            tradesRoot, tradesSymbol,
            sprintf "%s-trades-%s.parquet" tradesSymbol (date.ToString("yyyy-MM-dd")))
    let outPath =
        args.GetResult(Output,
            defaultValue = sprintf "logs/%s_%s_obi_volume.csv" symbol (date.ToString("yyyy-MM-dd")))

    if not (File.Exists bookParquet) then
        eprintfn "Book parquet not found: %s" bookParquet
        eprintfn "  Run: TradingEdge.CryptoLake download -t %s -d %s --table book"
            symbol (date.ToString("yyyy-MM-dd"))
        exit 1
    if not (File.Exists tradesParquet) then
        eprintfn "Trades parquet not found: %s" tradesParquet
        exit 1

    printfn "Process-day:"
    printfn "  symbol:         %s (trades=%s)" symbol tradesSymbol
    printfn "  date:           %s" (date.ToString("yyyy-MM-dd"))
    printfn "  trades parquet: %s" tradesParquet
    printfn "  book parquet:   %s" bookParquet
    printfn "  vol/bar:        %g" volumePerBar
    printfn "  lambda:         %g" lambdaDecay
    printfn "  output:         %s" outPath
    printfn ""

    let sw = Diagnostics.Stopwatch.StartNew()
    printfn "Building volume bars..."
    let bars = ProcessDay.buildVolumeBars tradesParquet volumePerBar
    let totalVol = bars |> Array.sumBy (fun b -> b.Volume)
    printfn "  %d bars, total volume = %g" bars.Length totalVol

    printfn "Sampling OBI from book snapshots..."
    let struct (obiTs, obi5, obi10) =
        ProcessDay.sampleObiSeries bookParquet lambdaDecay
    printfn "  %d snapshots" obiTs.Length

    printfn "Aligning OBI to bars..."
    let struct (bar5, bar10) =
        ProcessDay.alignObiToBars bars obiTs obi5 obi10

    ProcessDay.writeCsv outPath bars bar5 bar10
    sw.Stop()
    printfn "Wrote %d rows to %s in %.1fs" bars.Length outPath sw.Elapsed.TotalSeconds
    0

// =============================================================================
// CLI dispatcher
// =============================================================================

type Command =
    | [<CliPrefix(CliPrefix.None)>] Download of ParseResults<DownloadArgs>
    | [<CliPrefix(CliPrefix.None)>] Process_Day of ParseResults<ProcessDayArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Download _ -> "Download a single (table, symbol, date) from Crypto Lake to local parquet."
            | Process_Day _ -> "Build a volume-bar CSV with VWAP, signed flow, and OBI overlay for one (symbol, date)."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Command>(programName = "TradingEdge.CryptoLake")
    try
        let parsed = parser.Parse(argv, raiseOnUsage = true)
        match parsed.GetSubCommand() with
        | Download a -> (runDownload a).GetAwaiter().GetResult()
        | Process_Day a -> runProcessDay a
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
