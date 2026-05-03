module TradingEdge.CryptoBacktest.Program

open System
open System.Diagnostics
open System.IO
open Argu
open TradingEdge.CryptoBacktest.OrderflowMA
open TradingEdge.CryptoBacktest.Backtest
open TradingEdge.CryptoBacktest.TradeLoader
open TradingEdge.CryptoBacktest.Reporting

let defaultDataRoot = "/mnt/d/trading-edge-bulk/crypto/binance/perps"
let defaultBarsRoot = "/mnt/d/trading-edge-bulk/crypto/binance/perps_bars"
let defaultStart = DateTime(2024, 5, 1)
let defaultEnd   = DateTime(2026, 4, 30)
let defaultResultsCsv = "data/crypto/backtest_results.csv"
let defaultSummaryCsv = "data/crypto/backtest_summary.csv"

let defaultTimeframes = [| "1m"; "5m"; "15m"; "1h"; "4h" |]
let defaultMaLengths  = [| 20; 50; 100; 200 |]

// =============================================================================
// Argu DU
// =============================================================================

type RunArgs =
    | [<Mandatory; AltCommandLine "-t">] Timeframe of string
    | [<Mandatory; AltCommandLine "-m">] Ma_Length of int
    | [<AltCommandLine "-s">] Symbol of string
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | Start_Date of string
    | End_Date of string
    | Notional of float
    | Taker_Fee of float
    | Allow_Short
    | Use_Trades
    | Trips_Dir of string
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Timeframe _   -> "Bar timeframe (e.g. 1m, 5m, 15m, 1h, 4h)."
            | Ma_Length _   -> "Rolling-sum window length, in bars."
            | Symbol _      -> "Symbol to backtest. Defaults to BTCUSDT."
            | Data_Root _   -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _   -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Start_Date _  -> "Inclusive start date YYYY-MM-DD."
            | End_Date _    -> "Inclusive end date YYYY-MM-DD."
            | Notional _    -> "Per-trade notional in quote currency. Default 1000."
            | Taker_Fee _   -> "Per-fill taker fee fraction. Default 0.0004 (4 bps)."
            | Allow_Short   -> "When set, take a short on bear signal instead of going flat."
            | Use_Trades    -> "Force the trade-stream backtest path even if pre-aggregated bars exist."
            | Trips_Dir _   -> "If set, write per-trade round-trips CSV to this directory."

type SweepArgs =
    | [<AltCommandLine "-d">] Data_Root of string
    | [<AltCommandLine "-b">] Bars_Root of string
    | [<AltCommandLine "-s">] Symbol of string
    | Start_Date of string
    | End_Date of string
    | Timeframes of string
    | Ma_Lengths of string
    | Notional of float
    | Taker_Fee of float
    | Allow_Short
    | Use_Trades
    | Results_Csv of string
    | Summary_Csv of string
    | [<AltCommandLine "-p">] Parallelism of int
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Data_Root _   -> "Trade-parquet root. Default: " + defaultDataRoot
            | Bars_Root _   -> "Pre-aggregated bar-parquet root. Default: " + defaultBarsRoot
            | Symbol _      -> "Restrict sweep to a comma-separated symbol list (default: all symbols with bar parquets)."
            | Start_Date _  -> "Inclusive start date YYYY-MM-DD. Default 2024-05-01."
            | End_Date _    -> "Inclusive end date YYYY-MM-DD. Default 2026-04-30."
            | Timeframes _  -> "Comma-separated timeframes. Default 1m,5m,15m,1h,4h."
            | Ma_Lengths _  -> "Comma-separated MA window lengths. Default 20,50,100,200."
            | Notional _    -> "Per-trade notional. Default 1000."
            | Taker_Fee _   -> "Per-fill taker fee fraction. Default 0.0004."
            | Allow_Short   -> "When set, sweep includes short legs."
            | Use_Trades    -> "Force the trade-stream backtest path even if pre-aggregated bars exist."
            | Results_Csv _ -> "Per-(symbol,timeframe,ma) results CSV path."
            | Summary_Csv _ -> "Aggregate per-(timeframe,ma) summary CSV path."
            | Parallelism _ -> "Max symbols processed concurrently. Default 4."

type CliArgs =
    | [<CliPrefix(CliPrefix.None)>] Run of ParseResults<RunArgs>
    | [<CliPrefix(CliPrefix.None)>] Sweep of ParseResults<SweepArgs>
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Run _   -> "Run one (symbol, timeframe, ma) configuration."
            | Sweep _ -> "Run the full grid: timeframes × MA lengths × symbols."

// =============================================================================
// Helpers
// =============================================================================

let parseDate (s: string) : DateTime =
    DateTime.ParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)

let parseList (sep: char) (s: string) : string[] =
    s.Split(sep, StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

// =============================================================================
// run
// =============================================================================

let cmdRun (args: ParseResults<RunArgs>) : int =
    let timeframe = args.GetResult Timeframe
    let maLength = args.GetResult Ma_Length
    let symbol = args.GetResult(RunArgs.Symbol, defaultValue = "BTCUSDT")
    let dataRoot = args.GetResult(RunArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(RunArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains RunArgs.Use_Trades
    let startDate = args.TryGetResult RunArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult RunArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let cfg =
        { defaultConfig maLength with
            Notional = args.GetResult(RunArgs.Notional, defaultValue = 1000.0)
            TakerFee = args.GetResult(RunArgs.Taker_Fee, defaultValue = 0.0004)
            AllowShort = args.Contains RunArgs.Allow_Short }
    let useBarPath = not useTrades && BarLoader.exists barsRoot timeframe symbol
    let pathLabel = if useBarPath then "bars" else "trades"
    printfn "[run] symbol=%s timeframe=%s ma=%d short=%b range=%s..%s notional=%g fee=%g path=%s"
        symbol timeframe maLength cfg.AllowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        cfg.Notional cfg.TakerFee pathLabel
    let sw = Stopwatch.StartNew()
    let metrics, trips =
        if useBarPath then
            let cell = Cell(symbol, timeframe, cfg)
            let metrics = runCellsFromBars barsRoot symbol startDate endDate [| cell |]
            metrics.[0], cell.Trips
        else
            let inp = {
                DataRoot = dataRoot
                Symbol = symbol
                Timeframe = timeframe
                StartDate = startDate
                EndDate = endDate
                Config = cfg
            }
            let mutable daysSeen = 0
            let mutable tradesAccum = 0L
            let progressEveryDays = 30
            let onDay (d: DateTime) (n: int) =
                daysSeen <- daysSeen + 1
                tradesAccum <- tradesAccum + int64 n
                if daysSeen % progressEveryDays = 0 then
                    printfn "[run] %s @ %s: %d days, %s trades, %.1fs"
                        symbol (d.ToString "yyyy-MM-dd")
                        daysSeen (tradesAccum.ToString "N0")
                        sw.Elapsed.TotalSeconds
            runOne inp (Some onDay)
    sw.Stop()
    printfn "[run] bars=%d trades=%d win_rate=%.3f pf=%.3f net_pnl=%.2f sharpe=%.3f maxDD=%.2f wall=%.1fs"
        metrics.BarsTotal metrics.Trades metrics.WinRate
        metrics.ProfitFactor metrics.NetPnL metrics.Sharpe metrics.MaxDrawdown
        sw.Elapsed.TotalSeconds
    match args.TryGetResult Trips_Dir with
    | Some dir ->
        let path =
            Path.Combine(
                dir,
                sprintf "%s-%s-ma%d%s.csv"
                    symbol timeframe maLength (if cfg.AllowShort then "-short" else ""))
        writeTrips path symbol timeframe cfg trips
        printfn "[run] wrote %d trips to %s" trips.Length path
    | None -> ()
    0

// =============================================================================
// sweep
// =============================================================================

let cmdSweep (args: ParseResults<SweepArgs>) : int =
    let dataRoot = args.GetResult(SweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(SweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains SweepArgs.Use_Trades
    let startDate = args.TryGetResult SweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult SweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult SweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue defaultTimeframes
    let maLengths =
        args.TryGetResult SweepArgs.Ma_Lengths
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue defaultMaLengths
    let symbols =
        match args.TryGetResult SweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
            // If we're going through bars, list bar symbols (intersection of
            // all requested timeframes); otherwise fall back to trade symbols.
            if useTrades then listSymbols dataRoot
            else
                let perTf = timeframes |> Array.map (fun tf -> Set.ofArray (BarLoader.listSymbols barsRoot tf))
                if perTf.Length = 0 then [||]
                else
                    perTf
                    |> Array.reduce Set.intersect
                    |> Set.toArray
                    |> Array.sort
    let notional = args.GetResult(SweepArgs.Notional, defaultValue = 1000.0)
    let takerFee = args.GetResult(SweepArgs.Taker_Fee, defaultValue = 0.0004)
    let allowShort = args.Contains SweepArgs.Allow_Short
    let resultsCsv = args.GetResult(Results_Csv, defaultValue = defaultResultsCsv)
    let summaryCsv = args.GetResult(Summary_Csv, defaultValue = defaultSummaryCsv)
    let parallelism = args.GetResult(Parallelism, defaultValue = 4)

    printfn "[sweep] symbols=%d timeframes=[%s] mas=[%s] short=%b range=%s..%s parallelism=%d path=%s"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (maLengths |> Array.map string))
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (if useTrades then "trades" else "bars")

    // Stream results: one row per (symbol, timeframe, ma) appended as it
    // finishes, so a partial run still leaves a usable file. Header is
    // written once on first append.
    if File.Exists resultsCsv then File.Delete resultsCsv

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * maLengths.Length
    let mutable doneCells = 0

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    let progressEveryDays = 30
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for ma in maLengths do
                            let cfg =
                                { defaultConfig ma with
                                    Notional = notional
                                    TakerFee = takerFee
                                    AllowShort = allowShort }
                            yield Cell(symbol, tf, cfg) |]
                let metrics =
                    if useTrades then
                        let mutable daysSeen = 0
                        let mutable tradesAccum = 0L
                        let onDay (d: DateTime) (n: int) =
                            daysSeen <- daysSeen + 1
                            tradesAccum <- tradesAccum + int64 n
                            if daysSeen % progressEveryDays = 0 then
                                lock writeLock (fun () ->
                                    printfn "[sweep] %s @ %s: %d days, %s trades, %.1fs"
                                        symbol (d.ToString "yyyy-MM-dd")
                                        daysSeen (tradesAccum.ToString "N0")
                                        swSym.Elapsed.TotalSeconds)
                        runCells dataRoot symbol startDate endDate cells (Some onDay)
                    else
                        runCellsFromBars barsRoot symbol startDate endDate cells
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[sweep] %s: no bars in window" symbol)
                else
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv
    printfn "[sweep] top 5 cells by median Sharpe:"
    for s in summarySorted |> Array.truncate 5 do
        printfn "  tf=%s ma=%d short=%b symbols=%d medSharpe=%.3f meanSharpe=%.3f pctProf=%.2f"
            s.Timeframe s.MaLength s.AllowShort s.Symbols
            s.MedianSharpe s.MeanSharpe s.PctProfitable
    // Per-cell long/short breakdown — helps tell directional bias from real
    // orderflow edge. If allow_short=false the short side reports as 0/0,
    // but the long-side numbers still let us compare to buy-and-hold.
    printfn "[sweep] per-cell long/short breakdown:"
    let sortedMetrics =
        metricsArr |> Array.sortBy (fun m -> m.Symbol, m.Timeframe, m.MaLength, m.AllowShort)
    for m in sortedMetrics do
        let pctLong =
            if m.LongTrades > 0 then float m.LongWins / float m.LongTrades else 0.0
        let pctShort =
            if m.ShortTrades > 0 then float m.ShortWins / float m.ShortTrades else 0.0
        printfn "  %s %s ma=%d short=%b | trades=%d sharpe=%.3f net=%.2f | LONG: n=%d wr=%.2f pf=%.2f net=%.2f | SHORT: n=%d wr=%.2f pf=%.2f net=%.2f"
            m.Symbol m.Timeframe m.MaLength m.AllowShort
            m.Trades m.Sharpe m.NetPnL
            m.LongTrades pctLong m.LongProfitFactor m.LongNetPnL
            m.ShortTrades pctShort m.ShortProfitFactor m.ShortNetPnL
    printfn "[sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// Entry point
// =============================================================================

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CliArgs>(programName = "TradingEdge.CryptoBacktest")
    try
        let res = parser.Parse(argv, raiseOnUsage = true)
        match res.GetSubCommand() with
        | Run a -> cmdRun a
        | Sweep a -> cmdSweep a
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        2
