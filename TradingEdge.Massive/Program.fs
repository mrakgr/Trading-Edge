open System
open System.IO
open System.Net.Http
open System.Threading
open Argu
open TradingEdge
open TradingEdge.Config
open TradingEdge.S3Download
open TradingEdge.SplitDownload
open TradingEdge.DividendDownload
open TradingEdge.IntradayDownload
open TradingEdge.TradesDownload
open TradingEdge.QuotesDownload
open TradingEdge.NewsDownload
open TradingEdge.TickersDownload
open TradingEdge.Database

let private formatDate (d: DateTime) = d.ToString("yyyy-MM-dd")

type DownloadBulkArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-p")>] Parallelism of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "Start date (yyyy-MM-dd). Default: 5 years ago"
            | End_Date _ -> "End date (yyyy-MM-dd). Default: today"
            | Parallelism _ -> "Max parallel downloads. Default: 10"

type DownloadSplitsArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "Start date (yyyy-MM-dd). Default: 5 years ago"
            | End_Date _ -> "End date (yyyy-MM-dd). Default: none (all future)"

type DownloadDividendsArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "Start date (yyyy-MM-dd). Default: 5 years ago"
            | End_Date _ -> "End date (yyyy-MM-dd). Default: none (all future)"

type IngestDataArgs =
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-c")>] Csv_Dir of string
    | [<AltCommandLine("-s")>] Splits_File of string
    | Dividends_File of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB database path (default: data/trading.db)"
            | Csv_Dir _ -> "Directory containing .csv.gz files (default: data/daily_aggregates)"
            | Splits_File _ -> "CSV file containing splits (default: data/splits.csv)"
            | Dividends_File _ -> "CSV file containing dividends (default: data/dividends.csv)"

type StocksInPlayArgs =
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-r")>] Min_Rvol of float
    | [<AltCommandLine("-g")>] Min_Gap_Pct of float
    | [<AltCommandLine("-v")>] Min_Dollar_Volume of float
    | [<AltCommandLine("-rw")>] Rvol_Weight of float
    | [<AltCommandLine("-gw")>] Gap_Weight of float
    | Include_Etfs
    | Pre_Window_Days of int
    | Post_Window_Days of int
    | Min_Range_Ratio of float
    | Json

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Date _ -> "Start date (yyyy-MM-dd). Default: 1 week ago"
            | End_Date _ -> "End date (yyyy-MM-dd). Default: today"
            | Database _ -> "DuckDB database path (default: data/trading.db)"
            | Min_Rvol _ -> "Minimum relative volume (default: 3)"
            | Min_Gap_Pct _ -> "Minimum gap percentage as decimal, e.g. 0.05 for 5% (default: 0.05)"
            | Min_Dollar_Volume _ -> "Minimum avg dollar volume in millions (default: 100)"
            | Rvol_Weight _ -> "Weight for RVOL in scoring (default: 0.95)"
            | Gap_Weight _ -> "Weight for gap in scoring (default: 0.05)"
            | Include_Etfs -> "Do not exclude ETFs/ETNs (default: excluded via ticker_reference)"
            | Pre_Window_Days _ -> "Days before breakout for range baseline (default: 20)"
            | Post_Window_Days _ -> "Days after breakout for range comparison (default: 5)"
            | Min_Range_Ratio _ -> "Min post/pre daily-range ratio (buyout filter, default: 0.5; 0 disables)"
            | Json -> "Emit JSON to stdout instead of the human-readable table"

type DownloadTickersArgs =
    | [<AltCommandLine("-d")>] Database of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB database path (default: data/trading.db)"

type RefreshViewsArgs =
    | [<AltCommandLine("-d")>] Database of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB database path (default: data/trading.db)"

type DownloadIntradayArgs =
    | [<AltCommandLine("-t")>] Ticker of string
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | Timespan of string
    | From_Sip
    | [<AltCommandLine("-r")>] Min_Rvol of float
    | [<AltCommandLine("-g")>] Min_Gap_Pct of float
    | [<AltCommandLine("-v")>] Min_Dollar_Volume of float

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Ticker _ -> "Stock ticker symbol (use with --start-date)"
            | Start_Date _ -> "Start date (yyyy-MM-dd). Default: 1 week ago"
            | End_Date _ -> "End date (yyyy-MM-dd). Default: today"
            | Database _ -> "DuckDB database path for SIP lookup (default: data/trading.db)"
            | Output_Dir _ -> "Output directory for downloaded data (default: data/intraday)"
            | Parallelism _ -> "Max parallel downloads (default: 5)"
            | Timespan _ -> "Aggregate timespan: 'minute' or 'second' (default: minute)"
            | From_Sip -> "Download intraday data for stocks in play from the database"
            | Min_Rvol _ -> "Min RVOL filter for SIP lookup (default: 3)"
            | Min_Gap_Pct _ -> "Min gap % filter for SIP lookup (default: 0.05)"
            | Min_Dollar_Volume _ -> "Min avg dollar volume in millions for SIP lookup (default: 100)"

type DownloadTradesArgs =
    | [<AltCommandLine("-t")>] Ticker of string
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | Pretty

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Ticker _ -> "Stock ticker symbol (required)"
            | Start_Date _ -> "Start date (yyyy-MM-dd, required)"
            | End_Date _ -> "End date (yyyy-MM-dd). If omitted, only start date is downloaded"
            | Output_Dir _ -> "Output directory for downloaded data (default: data/trades)"
            | Parallelism _ -> "Max parallel downloads (default: 5)"
            | Pretty -> "Output JSON with indentation (pretty print)"

type DownloadQuotesArgs =
    | [<AltCommandLine("-t")>] Ticker of string
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-p")>] Parallelism of int
    | Pretty

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Ticker _ -> "Stock ticker symbol (required)"
            | Start_Date _ -> "Start date (yyyy-MM-dd, required)"
            | End_Date _ -> "End date (yyyy-MM-dd). If omitted, only start date is downloaded"
            | Output_Dir _ -> "Output directory for downloaded data (default: data/quotes)"
            | Parallelism _ -> "Max parallel downloads (default: 5)"
            | Pretty -> "Output JSON with indentation (pretty print)"

type DownloadNewsArgs =
    | [<AltCommandLine("-t")>] Ticker of string
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-o")>] Output_Dir of string
    | [<AltCommandLine("-p")>] Parallelism of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Ticker _ -> "Stock ticker symbol (required)"
            | Start_Date _ -> "Start date (yyyy-MM-dd, required)"
            | End_Date _ -> "End date (yyyy-MM-dd). If omitted, uses start date"
            | Output_Dir _ -> "Output directory for downloaded data (default: data/news)"
            | Parallelism _ -> "Max parallel downloads (default: 5)"

type IngestIntradayArgs =
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-i")>] Input_Dir of string
    | Timespan of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB database path (default: data/trading.db)"
            | Input_Dir _ -> "Input directory for intraday data (default: data/intraday)"
            | Timespan _ -> "Filter by timespan: 'minute', 'second', or 'all' (default: all)"

type IngestTradesArgs =
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-i")>] Input_Dir of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB database path (default: data/trading.db)"
            | Input_Dir _ -> "Input directory for trades data (default: data/trades)"

type IngestQuotesArgs =
    | [<AltCommandLine("-d")>] Database of string
    | [<AltCommandLine("-i")>] Input_Dir of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Database _ -> "DuckDB database path (default: data/trading.db)"
            | Input_Dir _ -> "Input directory for quotes data (default: data/quotes)"

type Arguments =
    | [<CliPrefix(CliPrefix.None)>] Download_Bulk of ParseResults<DownloadBulkArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Splits of ParseResults<DownloadSplitsArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Dividends of ParseResults<DownloadDividendsArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Intraday of ParseResults<DownloadIntradayArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Trades of ParseResults<DownloadTradesArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Quotes of ParseResults<DownloadQuotesArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_News of ParseResults<DownloadNewsArgs>
    | [<CliPrefix(CliPrefix.None)>] Ingest_Data of ParseResults<IngestDataArgs>
    | [<CliPrefix(CliPrefix.None)>] Ingest_Intraday of ParseResults<IngestIntradayArgs>
    | [<CliPrefix(CliPrefix.None)>] Ingest_Trades of ParseResults<IngestTradesArgs>
    | [<CliPrefix(CliPrefix.None)>] Ingest_Quotes of ParseResults<IngestQuotesArgs>
    | [<CliPrefix(CliPrefix.None)>] Stocks_In_Play of ParseResults<StocksInPlayArgs>
    | [<CliPrefix(CliPrefix.None)>] Download_Tickers of ParseResults<DownloadTickersArgs>
    | [<CliPrefix(CliPrefix.None)>] Refresh_Views of ParseResults<RefreshViewsArgs>

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Download_Bulk _ -> "Download daily aggregate files from Massive S3"
            | Download_Splits _ -> "Download stock splits from Massive API"
            | Download_Dividends _ -> "Download dividends from Polygon API"
            | Download_Intraday _ -> "Download intraday (minute/second) data for tickers"
            | Download_Trades _ -> "Download tick-level trades data for a ticker"
            | Download_Quotes _ -> "Download NBBO quotes data for a ticker"
            | Download_News _ -> "Download news articles for a ticker"
            | Ingest_Data _ -> "Ingest daily data into DuckDB database"
            | Ingest_Intraday _ -> "Ingest intraday data into DuckDB database"
            | Ingest_Trades _ -> "Ingest trades data into DuckDB database"
            | Ingest_Quotes _ -> "Ingest quotes data into DuckDB database"
            | Stocks_In_Play _ -> "List top stocks in play for a date range"
            | Download_Tickers _ -> "Download ETF/ETN ticker reference data from Polygon"
            | Refresh_Views _ -> "Refresh views only (fast, no table rematerialization)"

let private ensureDataDir () =
    Directory.CreateDirectory("data") |> ignore
    Directory.CreateDirectory("data/daily_aggregates") |> ignore

let private handleDownloadBulk (config: MassiveConfig) (args: ParseResults<DownloadBulkArgs>) =
    ensureDataDir ()

    let endDate =
        args.TryGetResult DownloadBulkArgs.End_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue DateTime.Now

    let startDate =
        args.TryGetResult DownloadBulkArgs.Start_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue (endDate.AddYears(-5))

    let parallelism = args.GetResult(DownloadBulkArgs.Parallelism, defaultValue = 10)
    let outputDir = "data/daily_aggregates"

    printfn "Downloading daily aggregates from %s to %s" (formatDate startDate) (formatDate endDate)
    printfn "Output directory: %s" (Path.GetFullPath outputDir)
    printfn "Parallelism: %d" parallelism

    use client = createS3Client config.S3AccessKey config.S3SecretKey
    use cts = new CancellationTokenSource()

    let results =
        downloadDailyAggregates client startDate endDate outputDir parallelism (Some S3Download.consoleProgress) cts.Token
        |> Async.RunSynchronously

    let downloaded = results |> List.filter (function Downloaded _ -> true | _ -> false) |> List.length
    let skipped = results |> List.filter (function Skipped _ -> true | _ -> false) |> List.length
    let failed = results |> List.filter (function Failed _ -> true | _ -> false) |> List.length

    printfn ""
    printfn "Download complete: %d downloaded, %d skipped, %d failed" downloaded skipped failed

let private handleDownloadSplits (config: MassiveConfig) (args: ParseResults<DownloadSplitsArgs>) =
    ensureDataDir ()

    let startDate =
        args.TryGetResult DownloadSplitsArgs.Start_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue (DateTime.Now.AddYears(-5))

    let endDate =
        args.TryGetResult DownloadSplitsArgs.End_Date
        |> Option.map DateTime.Parse

    let endDateStr =
        match endDate with
        | Some d -> sprintf " to %s" (formatDate d)
        | None -> ""

    printfn "Downloading splits from %s%s" (formatDate startDate) endDateStr

    use httpClient = new HttpClient()
    use cts = new CancellationTokenSource()

    let result =
        downloadSplitsWithConsoleProgress httpClient config.ApiKey startDate endDate cts.Token
        |> Async.RunSynchronously

    match result with
    | Ok splits ->
        printfn ""
        printfn "Downloaded %d splits" splits.Length

        // Save to CSV for fast DuckDB ingestion
        let outputPath = "data/splits.csv"
        use writer = new StreamWriter(outputPath)
        writer.WriteLine("ticker,execution_date,split_from,split_to,split_ratio")
        for split in splits do
            let dateStr = split.ExecutionDate.ToString("yyyy-MM-dd")
            writer.WriteLine($"{split.Ticker},{dateStr},{split.SplitFrom},{split.SplitTo},{split.SplitRatio}")
        printfn "Saved splits to %s" (Path.GetFullPath outputPath)

    | Error msg ->
        printfn "Error downloading splits: %s" msg

let private handleDownloadDividends (config: MassiveConfig) (args: ParseResults<DownloadDividendsArgs>) =
    ensureDataDir ()

    let startDate =
        args.TryGetResult DownloadDividendsArgs.Start_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue (DateTime.Now.AddYears(-5))

    let endDate =
        args.TryGetResult DownloadDividendsArgs.End_Date
        |> Option.map DateTime.Parse

    let endDateStr =
        match endDate with
        | Some d -> sprintf " to %s" (formatDate d)
        | None -> ""

    printfn "Downloading dividends from %s%s" (formatDate startDate) endDateStr

    use httpClient = new HttpClient()
    use cts = new CancellationTokenSource()

    let result =
        downloadDividendsWithConsoleProgress httpClient config.ApiKey startDate endDate cts.Token
        |> Async.RunSynchronously

    match result with
    | Ok dividends ->
        printfn ""
        printfn "Downloaded %d dividends" dividends.Length

        // Save to CSV for fast DuckDB ingestion
        let outputPath = "data/dividends.csv"
        use writer = new StreamWriter(outputPath)
        writer.WriteLine("ticker,ex_dividend_date,cash_amount,declaration_date,pay_date,frequency,dividend_type")
        for d in dividends do
            let exDateStr = d.ExDividendDate.ToString("yyyy-MM-dd")
            let declDateStr = d.DeclarationDate |> Option.map (fun dt -> dt.ToString("yyyy-MM-dd")) |> Option.defaultValue ""
            let payDateStr = d.PayDate |> Option.map (fun dt -> dt.ToString("yyyy-MM-dd")) |> Option.defaultValue ""
            writer.WriteLine($"{d.Ticker},{exDateStr},{d.CashAmount},{declDateStr},{payDateStr},{d.Frequency},{d.DividendType}")
        printfn "Saved dividends to %s" (Path.GetFullPath outputPath)

    | Error msg ->
        printfn "Error downloading dividends: %s" msg

let private handleIngestData (args: ParseResults<IngestDataArgs>) =
    ensureDataDir ()

    let dbPath =
        args.TryGetResult IngestDataArgs.Database
        |> Option.defaultValue "data/trading.db"

    let csvDir =
        args.TryGetResult IngestDataArgs.Csv_Dir
        |> Option.defaultValue "data/daily_aggregates"

    let splitsFile =
        args.TryGetResult IngestDataArgs.Splits_File
        |> Option.defaultValue "data/splits.csv"

    let dividendsFile =
        args.TryGetResult IngestDataArgs.Dividends_File
        |> Option.defaultValue "data/dividends.csv"

    printfn "Database: %s" (Path.GetFullPath dbPath)
    printfn "CSV directory: %s" (Path.GetFullPath csvDir)
    printfn "Splits file: %s" (Path.GetFullPath splitsFile)
    printfn "Dividends file: %s" (Path.GetFullPath dividendsFile)
    printfn ""

    use connection = openConnection dbPath
    initializeSchema connection

    // Ingest daily prices from CSV files using DuckDB's native CSV reader with glob
    if Directory.Exists csvDir then
        let allFiles = Directory.GetFiles(csvDir, "*.csv.gz")
        printfn "Found %d CSV files" allFiles.Length

        let globPattern = Path.Combine(csvDir, "*.csv.gz")
        printfn "Bulk loading from: %s" globPattern
        
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let countBefore = getDailyPriceCount connection
        let _ = ingestDailyPricesFromGlob connection globPattern
        let countAfter = getDailyPriceCount connection
        let totalPrices = countAfter - countBefore
        sw.Stop()

        let rowsPerSec = if sw.Elapsed.TotalSeconds > 0.0 then float countAfter / sw.Elapsed.TotalSeconds else 0.0
        printfn "Ingested %d new daily prices (total: %d) in %.2fs (%.0f rows/sec)" totalPrices countAfter sw.Elapsed.TotalSeconds rowsPerSec
    else
        printfn "CSV directory not found: %s" csvDir

    // Ingest splits from CSV file using DuckDB's native CSV reader
    if File.Exists splitsFile then
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let countBefore = getSplitCount connection
        let _ = ingestSplitsFromCsv connection splitsFile
        let countAfter = getSplitCount connection
        let newSplits = countAfter - countBefore
        sw.Stop()
        printfn "Ingested %d new splits (total: %d) in %.2fs" newSplits countAfter sw.Elapsed.TotalSeconds
    else
        printfn "Splits file not found: %s" splitsFile

    // Ingest dividends from CSV file using DuckDB's native CSV reader
    if File.Exists dividendsFile then
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let countBefore = getDividendCount connection
        let _ = ingestDividendsFromCsv connection dividendsFile
        let countAfter = getDividendCount connection
        let newDividends = countAfter - countBefore
        sw.Stop()
        printfn "Ingested %d new dividends (total: %d) in %.2fs" newDividends countAfter sw.Elapsed.TotalSeconds
    else
        printfn "Dividends file not found: %s" dividendsFile

    printfn "Data ingestion complete."

    // Materialize derived tables (split-adjusted prices, momentum, DOM indicator, etc.)
    printfn ""
    printfn "Materializing derived tables..."
    let sw = System.Diagnostics.Stopwatch.StartNew()
    materializeTables connection
    sw.Stop()
    printfn "Materialized derived tables in %.2fs" sw.Elapsed.TotalSeconds

    // Refresh views
    printfn "Refreshing views..."
    let sw2 = System.Diagnostics.Stopwatch.StartNew()
    refreshViews connection
    sw2.Stop()
    printfn "Refreshed views in %.2fs" sw2.Elapsed.TotalSeconds

    // Show summary
    printfn ""
    printfn "Database summary:"
    printfn "  Daily prices: %d" (getDailyPriceCount connection)
    printfn "  Splits: %d" (getSplitCount connection)
    printfn "  Dividends: %d" (getDividendCount connection)

    match getDateRange connection with
    | Some (minDate, maxDate) ->
        printfn "  Date range: %s to %s" (formatDate minDate) (formatDate maxDate)
    | None ->
        printfn "  Date range: (no data)"

let private handleStocksInPlay (args: ParseResults<StocksInPlayArgs>) =
    let endDate =
        args.TryGetResult StocksInPlayArgs.End_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue DateTime.Now

    let startDate =
        args.TryGetResult StocksInPlayArgs.Start_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue (endDate.AddDays(-7))

    let dbPath =
        args.TryGetResult StocksInPlayArgs.Database
        |> Option.defaultValue "data/trading.db"

    let minRvol = args.GetResult(StocksInPlayArgs.Min_Rvol, defaultValue = 3.0)
    let minGapPct = args.GetResult(StocksInPlayArgs.Min_Gap_Pct, defaultValue = 0.05)
    let minDollarVolume = args.GetResult(StocksInPlayArgs.Min_Dollar_Volume, defaultValue = 100.0) * 1_000_000.0
    let rvolWeight = args.GetResult(StocksInPlayArgs.Rvol_Weight, defaultValue = 0.95)
    let gapWeight = args.GetResult(StocksInPlayArgs.Gap_Weight, defaultValue = 0.05)
    let excludeEtfs = not (args.Contains StocksInPlayArgs.Include_Etfs)
    let preWindowDays = args.GetResult(StocksInPlayArgs.Pre_Window_Days, defaultValue = 20)
    let postWindowDays = args.GetResult(StocksInPlayArgs.Post_Window_Days, defaultValue = 5)
    let minRangeRatio = args.GetResult(StocksInPlayArgs.Min_Range_Ratio, defaultValue = 0.5)
    let jsonMode = args.Contains StocksInPlayArgs.Json

    if not jsonMode then
        printfn "Stocks In Play from %s to %s" (formatDate startDate) (formatDate endDate)
        printfn "Filters: RVOL >= %.1fx, Gap >= %.1f%%, Avg Dollar Volume >= $%.0fM" minRvol (minGapPct * 100.0) (minDollarVolume / 1_000_000.0)
        printfn "ETF exclusion: %b   Pre/post window: %d/%d   Min range ratio: %.2f" excludeEtfs preWindowDays postWindowDays minRangeRatio
        printfn "Database: %s" (Path.GetFullPath dbPath)
        printfn ""

    use connection = openConnection dbPath
    let stocks =
        getStocksInPlay
            connection startDate endDate minRvol minGapPct minDollarVolume
            rvolWeight gapWeight excludeEtfs preWindowDays postWindowDays minRangeRatio

    if jsonMode then
        // Emit a JSON array of {ticker, date, avg_dollar_volume_4w} objects.
        let sb = System.Text.StringBuilder()
        sb.Append("[") |> ignore
        let mutable first = true
        for stock in stocks do
            if not first then sb.Append(",") |> ignore
            first <- false
            sb.AppendFormat(
                System.Globalization.CultureInfo.InvariantCulture,
                "\n  {{\"ticker\": \"{0}\", \"date\": \"{1}\", \"avg_dollar_volume_4w\": {2}}}",
                stock.ticker,
                stock.date.ToString("yyyy-MM-dd"),
                stock.avg_dollar_volume_4w) |> ignore
        if not first then sb.Append("\n") |> ignore
        sb.Append("]") |> ignore
        printfn "%s" (sb.ToString())
    else
        if stocks.Length = 0 then
            printfn "No stocks in play found for the given date range."
        else
            let mutable currentDate = DateOnly.MinValue
            for stock in stocks do
                if stock.date <> currentDate then
                    currentDate <- stock.date
                    printfn "=== %s ===" (currentDate.ToString("yyyy-MM-dd"))
                let ratioStr =
                    if stock.range_ratio.HasValue then sprintf "%5.2f" stock.range_ratio.Value
                    else "  n/a"
                printfn "  %2d. %-6s  Gap: %+6.2f%%  RVOL: %5.1fx  Score: %5.2f  RangeRatio: %s"
                    stock.rank stock.ticker (stock.gap_pct * 100.0) stock.rvol stock.in_play_score ratioStr

let private handleDownloadTickers (config: MassiveConfig) (args: ParseResults<DownloadTickersArgs>) =
    let dbPath =
        args.TryGetResult DownloadTickersArgs.Database
        |> Option.defaultValue "data/trading.db"

    printfn "Downloading ETF/ETN ticker reference from Polygon..."
    printfn "Database: %s" (Path.GetFullPath dbPath)

    use httpClient = new HttpClient()
    use cts = new CancellationTokenSource()

    let result =
        downloadAllEtfTickers httpClient config.ApiKey cts.Token
        |> Async.RunSynchronously

    match result with
    | Ok rows ->
        printfn ""
        printfn "Downloaded %d ETF-like tickers" rows.Length

        use connection = openConnection dbPath
        initializeSchema connection
        let inserted = replaceTickerReference connection rows
        let total = getTickerReferenceCount connection
        printfn "Inserted %d rows; ticker_reference now contains %d rows." inserted total

    | Error msg ->
        eprintfn "Error downloading tickers: %s" msg
        exit 1

let private handleRefreshViews (args: ParseResults<RefreshViewsArgs>) =
    let dbPath =
        args.TryGetResult RefreshViewsArgs.Database
        |> Option.defaultValue "data/trading.db"

    printfn "Refreshing views..."
    printfn "Database: %s" (Path.GetFullPath dbPath)

    use connection = openConnection dbPath
    // Make sure base tables (e.g. ticker_reference) exist before installing views
    // that reference them.
    initializeSchema connection
    let sw = System.Diagnostics.Stopwatch.StartNew()
    refreshViews connection
    sw.Stop()
    printfn "Refreshed views in %.2fs" sw.Elapsed.TotalSeconds

let private handleDownloadIntraday (config: MassiveConfig) (args: ParseResults<DownloadIntradayArgs>) =
    let startDateOpt = args.TryGetResult DownloadIntradayArgs.Start_Date |> Option.map DateTime.Parse
    let endDateOpt = args.TryGetResult DownloadIntradayArgs.End_Date |> Option.map DateTime.Parse

    // If end date is omitted, use start date (single day download)
    let (startDate, endDate) =
        match startDateOpt, endDateOpt with
        | Some s, Some e -> (s, e)
        | Some s, None -> (s, s)
        | None, Some e -> (e.AddDays(-7), e)
        | None, None -> let now = DateTime.Now in (now.AddDays(-7), now)

    let outputDir =
        args.TryGetResult DownloadIntradayArgs.Output_Dir
        |> Option.defaultValue "data/intraday"

    let parallelism = args.GetResult(DownloadIntradayArgs.Parallelism, defaultValue = 5)

    let timespan =
        match args.TryGetResult DownloadIntradayArgs.Timespan with
        | Some "second" -> AggregateTimespan.Second
        | Some "minute" | Some _ | None -> AggregateTimespan.Minute

    let timespanStr = timespan.ToApiString()

    // Determine ticker/date pairs to download
    let tickerDates =
        match args.TryGetResult DownloadIntradayArgs.Ticker with
        | Some ticker ->
            // Single ticker mode: download for date range
            let ticker = ticker.ToUpperInvariant()
            let days = getTradingDays startDate endDate
            days |> List.map (fun d -> (ticker, d))

        | None when args.Contains DownloadIntradayArgs.From_Sip ->
            // From SIP mode: query database for stocks in play
            let dbPath =
                args.TryGetResult DownloadIntradayArgs.Database
                |> Option.defaultValue "data/trading.db"

            let minRvol = args.GetResult(DownloadIntradayArgs.Min_Rvol, defaultValue = 3.0)
            let minGapPct = args.GetResult(DownloadIntradayArgs.Min_Gap_Pct, defaultValue = 0.05)
            let minDollarVolume = args.GetResult(DownloadIntradayArgs.Min_Dollar_Volume, defaultValue = 100.0) * 1_000_000.0

            printfn "Querying stocks in play from database..."
            printfn "Database: %s" (Path.GetFullPath dbPath)
            printfn "SIP Filters: RVOL >= %.1fx, Gap >= %.1f%%, Avg Dollar Volume >= $%.0fM" minRvol (minGapPct * 100.0) (minDollarVolume / 1_000_000.0)

            use connection = openConnection dbPath
            let stocks =
                getStocksInPlay
                    connection startDate endDate minRvol minGapPct minDollarVolume
                    0.95 0.05 true 20 5 0.5

            stocks
            |> Array.map (fun (s: StockInPlayRow) -> (s.ticker, s.date.ToDateTime(TimeOnly.MinValue)))
            |> Array.toList

        | None ->
            failwith "Either --ticker or --from-sip is required"

    if tickerDates.IsEmpty then
        printfn "No ticker/date pairs to download."
    else
        printfn ""
        printfn "Downloading %s aggregates for %d ticker/date pairs" timespanStr tickerDates.Length
        printfn "Date range: %s to %s" (formatDate startDate) (formatDate endDate)
        printfn "Output directory: %s" (Path.GetFullPath outputDir)
        printfn "Parallelism: %d" parallelism
        printfn ""

        Directory.CreateDirectory(outputDir) |> ignore

        use httpClient = new HttpClient()
        use cts = new CancellationTokenSource()

        let results =
            downloadIntradayBatch httpClient config.ApiKey outputDir tickerDates timespan parallelism (Some IntradayDownload.consoleProgress) cts.Token
            |> Async.RunSynchronously

        let downloaded = results |> List.filter (function IntradayDownloaded _ -> true | _ -> false) |> List.length
        let skipped = results |> List.filter (function IntradaySkipped _ -> true | _ -> false) |> List.length
        let failed = results |> List.filter (function IntradayFailed _ -> true | _ -> false) |> List.length

        printfn ""
        printfn "Download complete: %d downloaded, %d skipped, %d failed" downloaded skipped failed

let private handleDownloadTrades (config: MassiveConfig) (args: ParseResults<DownloadTradesArgs>) =
    let ticker =
        match args.TryGetResult DownloadTradesArgs.Ticker with
        | Some t -> t.ToUpperInvariant()
        | None -> failwith "Ticker is required. Use -t or --ticker to specify."

    let startDate =
        match args.TryGetResult DownloadTradesArgs.Start_Date with
        | Some d -> DateTime.Parse(d)
        | None -> failwith "Start date is required. Use -s or --start-date to specify."

    // If end date is omitted, use start date (single day download)
    let endDate =
        args.TryGetResult DownloadTradesArgs.End_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue startDate

    let outputDir =
        args.TryGetResult DownloadTradesArgs.Output_Dir
        |> Option.defaultValue "data/trades"

    let parallelism = args.GetResult(DownloadTradesArgs.Parallelism, defaultValue = 5)
    let prettyPrint = args.Contains DownloadTradesArgs.Pretty

    let days = getTradingDays startDate endDate
    let tickerDates = days |> List.map (fun d -> (ticker, d))

    if tickerDates.IsEmpty then
        printfn "No trading days in the specified range."
    else
        printfn "Downloading trades for %s" ticker
        printfn "Date range: %s to %s (%d days)" (formatDate startDate) (formatDate endDate) tickerDates.Length
        printfn "Output directory: %s" (Path.GetFullPath outputDir)
        printfn "Parallelism: %d" parallelism
        if prettyPrint then printfn "Pretty print: enabled"
        printfn ""

        Directory.CreateDirectory(outputDir) |> ignore

        use httpClient = new HttpClient()
        use cts = new CancellationTokenSource()

        let results =
            downloadTradesBatch httpClient config.ApiKey outputDir tickerDates prettyPrint parallelism (Some TradesDownload.consoleProgress) cts.Token
            |> Async.RunSynchronously

        let downloaded = results |> List.filter (function TradesDownloaded _ -> true | _ -> false) |> List.length
        let skipped = results |> List.filter (function TradesSkipped _ -> true | _ -> false) |> List.length
        let failed = results |> List.filter (function TradesFailed _ -> true | _ -> false) |> List.length

        printfn ""
        printfn "Download complete: %d downloaded, %d skipped, %d failed" downloaded skipped failed

let private handleDownloadQuotes (config: MassiveConfig) (args: ParseResults<DownloadQuotesArgs>) =
    let ticker =
        match args.TryGetResult DownloadQuotesArgs.Ticker with
        | Some t -> t.ToUpperInvariant()
        | None -> failwith "Ticker is required. Use -t or --ticker to specify."

    let startDate =
        match args.TryGetResult DownloadQuotesArgs.Start_Date with
        | Some d -> DateTime.Parse(d)
        | None -> failwith "Start date is required. Use -s or --start-date to specify."

    let endDate =
        args.TryGetResult DownloadQuotesArgs.End_Date
        |> Option.map DateTime.Parse
        |> Option.defaultValue startDate

    let outputDir =
        args.TryGetResult DownloadQuotesArgs.Output_Dir
        |> Option.defaultValue "data/quotes"

    let parallelism = args.GetResult(DownloadQuotesArgs.Parallelism, defaultValue = 5)
    let prettyPrint = args.Contains DownloadQuotesArgs.Pretty

    let days = getTradingDays startDate endDate
    let tickerDates = days |> List.map (fun d -> (ticker, d))

    if tickerDates.IsEmpty then
        printfn "No trading days in the specified range."
    else
        printfn "Downloading quotes for %s" ticker
        printfn "Date range: %s to %s (%d days)" (formatDate startDate) (formatDate endDate) tickerDates.Length
        printfn "Output directory: %s" (Path.GetFullPath outputDir)
        printfn "Parallelism: %d" parallelism
        if prettyPrint then printfn "Pretty print: enabled"
        printfn ""

        Directory.CreateDirectory(outputDir) |> ignore

        use httpClient = new HttpClient()
        use cts = new CancellationTokenSource()

        let results =
            downloadQuotesBatch httpClient config.ApiKey outputDir tickerDates prettyPrint parallelism (Some QuotesDownload.consoleProgress) cts.Token
            |> Async.RunSynchronously

        let downloaded = results |> List.filter (function QuotesDownloaded _ -> true | _ -> false) |> List.length
        let skipped = results |> List.filter (function QuotesSkipped _ -> true | _ -> false) |> List.length
        let failed = results |> List.filter (function QuotesFailed _ -> true | _ -> false) |> List.length

        printfn ""
        printfn "Download complete: %d downloaded, %d skipped, %d failed" downloaded skipped failed

let private handleDownloadNews (config: MassiveConfig) (args: ParseResults<DownloadNewsArgs>) =
    let ticker =
        match args.TryGetResult DownloadNewsArgs.Ticker with
        | Some t -> t.ToUpperInvariant()
        | None -> failwith "Ticker is required. Use -t or --ticker to specify."

    let endDateOpt = args.TryGetResult DownloadNewsArgs.End_Date |> Option.map DateTime.Parse
    let startDateOpt = args.TryGetResult DownloadNewsArgs.Start_Date |> Option.map DateTime.Parse

    let (startDate, endDate) =
        match startDateOpt, endDateOpt with
        | Some s, Some e -> (s, e)
        | Some s, None -> (s, s)
        | None, Some e -> (e.AddDays(-7), e)
        | None, None -> failwith "Either start date or end date is required."

    let outputDir =
        args.TryGetResult DownloadNewsArgs.Output_Dir
        |> Option.defaultValue "data/news"

    let parallelism = args.GetResult(DownloadNewsArgs.Parallelism, defaultValue = 5)

    let days = getTradingDays startDate endDate
    let tickerDates = days |> List.map (fun d -> (ticker, d))

    if tickerDates.IsEmpty then
        printfn "No trading days in the specified range."
    else
        printfn "Downloading news for %s" ticker
        printfn "Date range: %s to %s (%d days)" (formatDate startDate) (formatDate endDate) tickerDates.Length
        printfn "Output directory: %s" (Path.GetFullPath outputDir)
        printfn ""

        Directory.CreateDirectory(outputDir) |> ignore

        use httpClient = new HttpClient()
        use cts = new CancellationTokenSource()

        let results =
            downloadNewsBatch httpClient config.ApiKey outputDir tickerDates parallelism (Some NewsDownload.consoleProgress) cts.Token
            |> Async.RunSynchronously

        let downloaded = results |> List.filter (function NewsDownloaded _ -> true | _ -> false) |> List.length
        let skipped = results |> List.filter (function NewsSkipped _ -> true | _ -> false) |> List.length
        let failed = results |> List.filter (function NewsFailed _ -> true | _ -> false) |> List.length

        printfn ""
        printfn "Download complete: %d downloaded, %d skipped, %d failed" downloaded skipped failed

let private handleIngestIntraday (args: ParseResults<IngestIntradayArgs>) =
    let dbPath =
        args.TryGetResult IngestIntradayArgs.Database
        |> Option.defaultValue "data/trading.db"

    let inputDir =
        args.TryGetResult IngestIntradayArgs.Input_Dir
        |> Option.defaultValue "data/intraday"

    let timespan =
        args.TryGetResult IngestIntradayArgs.Timespan
        |> Option.defaultValue "all"

    printfn "Ingesting intraday data..."
    printfn "Database: %s" (Path.GetFullPath dbPath)
    printfn "Input directory: %s" (Path.GetFullPath inputDir)
    printfn "Timespan filter: %s" timespan
    printfn ""

    use connection = openConnection dbPath
    initializeSchema connection

    let ingestTimespan timespanName tableName ingestFn countFn =
        let dir = Path.Combine(inputDir, timespanName)
        if Directory.Exists dir then
            let globPattern = Path.Combine(dir, "*", "*.json")
            let fileCount =
                if Directory.Exists dir then
                    Directory.GetDirectories(dir)
                    |> Array.sumBy (fun d -> Directory.GetFiles(d, "*.json").Length)
                else 0

            if fileCount > 0 then
                printfn "Found %d %s JSON files" fileCount timespanName
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let countBefore = countFn connection
                let _ = ingestFn connection globPattern
                let countAfter = countFn connection
                let newRows = countAfter - countBefore
                sw.Stop()
                printfn "Ingested %d new %s bars (total: %d) in %.2fs" newRows timespanName countAfter sw.Elapsed.TotalSeconds
            else
                printfn "No %s JSON files found in %s" timespanName dir
        else
            printfn "Directory not found: %s" dir

    match timespan with
    | "minute" ->
        ingestTimespan "minute" "intraday_prices_minute" ingestIntradayMinuteFromGlob getIntradayMinuteCount
    | "second" ->
        ingestTimespan "second" "intraday_prices_second" ingestIntradaySecondFromGlob getIntradaySecondCount
    | "all" | _ ->
        ingestTimespan "minute" "intraday_prices_minute" ingestIntradayMinuteFromGlob getIntradayMinuteCount
        ingestTimespan "second" "intraday_prices_second" ingestIntradaySecondFromGlob getIntradaySecondCount

    printfn ""
    printfn "Intraday ingestion complete."

let private handleIngestTrades (args: ParseResults<IngestTradesArgs>) =
    let dbPath =
        args.TryGetResult IngestTradesArgs.Database
        |> Option.defaultValue "data/trading.db"

    let inputDir =
        args.TryGetResult IngestTradesArgs.Input_Dir
        |> Option.defaultValue "data/trades"

    printfn "Ingesting trades data..."
    printfn "Database: %s" (Path.GetFullPath dbPath)
    printfn "Input directory: %s" (Path.GetFullPath inputDir)
    printfn ""

    use connection = openConnection dbPath
    initializeSchema connection

    if Directory.Exists inputDir then
        let globPattern = Path.Combine(inputDir, "*/*.json")
        let countBefore = getTradesCount connection
        printfn "Trades before: %d" countBefore

        let inserted = ingestTradesFromGlob connection globPattern
        printfn "Rows processed: %d" inserted

        let countAfter = getTradesCount connection
        printfn "Trades after: %d" countAfter
        printfn "New trades added: %d" (countAfter - countBefore)
    else
        printfn "Directory not found: %s" inputDir

    printfn ""
    printfn "Trades ingestion complete."

let private handleIngestQuotes (args: ParseResults<IngestQuotesArgs>) =
    let dbPath =
        args.TryGetResult IngestQuotesArgs.Database
        |> Option.defaultValue "data/trading.db"

    let inputDir =
        args.TryGetResult IngestQuotesArgs.Input_Dir
        |> Option.defaultValue "data/quotes"

    printfn "Ingesting quotes data..."
    printfn "Database: %s" (Path.GetFullPath dbPath)
    printfn "Input directory: %s" (Path.GetFullPath inputDir)
    printfn ""

    use connection = openConnection dbPath
    initializeSchema connection

    if Directory.Exists inputDir then
        let globPattern = Path.Combine(inputDir, "*/*.json")
        let countBefore = getQuotesCount connection
        printfn "Quotes before: %d" countBefore

        let inserted = ingestQuotesFromGlob connection globPattern
        printfn "Rows processed: %d" inserted

        let countAfter = getQuotesCount connection
        printfn "Quotes after: %d" countAfter
        printfn "New quotes added: %d" (countAfter - countBefore)
    else
        printfn "Directory not found: %s" inputDir

    printfn ""
    printfn "Quotes ingestion complete."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Arguments>(programName = "TradingEdge")

    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

        let configPath = Path.Combine(Environment.CurrentDirectory, "api_key.json")

        for result in results.GetAllResults() do
            match result with
            | Download_Bulk args ->
                let config = loadConfigOrFail configPath
                handleDownloadBulk config args
            | Download_Splits args ->
                let config = loadConfigOrFail configPath
                handleDownloadSplits config args
            | Download_Dividends args ->
                let config = loadConfigOrFail configPath
                handleDownloadDividends config args
            | Download_Intraday args ->
                let config = loadConfigOrFail configPath
                handleDownloadIntraday config args
            | Download_Trades args ->
                let config = loadConfigOrFail configPath
                handleDownloadTrades config args
            | Download_Quotes args ->
                let config = loadConfigOrFail configPath
                handleDownloadQuotes config args
            | Download_News args ->
                let config = loadConfigOrFail configPath
                handleDownloadNews config args
            | Ingest_Data args ->
                handleIngestData args
            | Ingest_Intraday args ->
                handleIngestIntraday args
            | Ingest_Trades args ->
                handleIngestTrades args
            | Ingest_Quotes args ->
                handleIngestQuotes args
            | Refresh_Views args ->
                handleRefreshViews args
            | Stocks_In_Play args ->
                handleStocksInPlay args
            | Download_Tickers args ->
                let config = loadConfigOrFail configPath
                handleDownloadTickers config args

        0
    with
    | :? ArguParseException as ex ->
        printfn "%s" ex.Message
        1
    | ex ->
        printfn "Error: %s" ex.Message
        1
