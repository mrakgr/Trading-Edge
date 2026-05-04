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
let defaultFundingRoot = "/mnt/d/trading-edge-bulk/crypto/binance/perps_funding"
let defaultStart = DateTime(2024, 5, 1)
let defaultEnd   = DateTime(2026, 4, 30)
let defaultResultsCsv = "data/crypto/backtest_results.csv"
let defaultSummaryCsv = "data/crypto/backtest_summary.csv"

let defaultTimeframes = [| "1m"; "5m"; "15m"; "1h"; "4h" |]
// Default MA windows in hours. 24h = 1 day; 240h = 10 days.
let defaultMaHours    = [| 24; 72; 168; 240 |]

// =============================================================================
// Argu DU
// =============================================================================

type RunArgs =
    | [<Mandatory; AltCommandLine "-t">] Timeframe of string
    | [<Mandatory; AltCommandLine "-m">] Ma_Hours of int
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
            | Ma_Hours _    -> "Rolling-sum signal window length, in HOURS. Timeframe-independent (240h = same span at 1h or 4h)."
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
    | Ma_Hours of string
    | Notional of float
    | Taker_Fee of float
    | Allow_Short
    | Use_Trades
    | Min_Daily_Volume of float
    | Min_Bar_Quote_Volume of float
    | Max_Adverse_Pct of float
    | Reference_Vol_Pct of float
    | Min_Long_Adv of float
    | Min_Short_Adv of float
    | Vol_Window_Days of int
    | Funding_Root of string
    | No_Funding
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
            | SweepArgs.Ma_Hours _ -> "Comma-separated MA signal-window lengths in HOURS. Default 24,72,168,240 (=1d, 3d, 7d, 10d). Independent of timeframe."
            | Notional _    -> "Per-trade notional. Default 1000."
            | Taker_Fee _   -> "Per-fill taker fee fraction. Default 0.0004."
            | Allow_Short   -> "When set, sweep includes short legs."
            | Use_Trades    -> "Force the trade-stream backtest path even if pre-aggregated bars exist."
            | Min_Daily_Volume _ -> "Minimum average daily quote-volume (USDT) for a symbol-timeframe to be included. Default 500000 (skip cells where a $1000 taker order is unrealistic)."
            | Min_Bar_Quote_Volume _ -> "Per-bar absolute quote-volume floor (USDT, entries only). Catches stub-data bars (e.g. pre-redenomination tail with $5-30/hour trading). Independent of --min-daily-volume. Default 0 (disabled)."
            | Max_Adverse_Pct _ -> "Stop-loss as percent of notional (e.g. 10.0 = stop at -10% MAE). Default 0 (disabled)."
            | Reference_Vol_Pct _ -> "Reference per-bar log-return std as percent (e.g. 1.0 = 1%/bar). Drives vol-based position sizing: high-vol entries are downsized, low-vol entries get full notional. Set per timeframe (1h~1.0, 2h~1.4, 4h~2.0). Default 0 (disabled)."
            | Min_Long_Adv _  -> "Minimum trailing-90d ADV (USDT/day) required for a long entry. Evaluated at signal-fire time using the engine's leak-free rolling window. Below threshold the signal is consumed (no retry on next bar) and the engine stays flat. Default 0 (disabled)."
            | Min_Short_Adv _ -> "Minimum trailing-90d ADV (USDT/day) required for a short entry. Same semantics as --min-long-adv. Default 0 (disabled)."
            | Vol_Window_Days _ -> "Vol-window length in days for the rolling log-return std used by vol-based position sizing. Independent of --ma-hours. Default 90; lower values (e.g. 7) make the vol estimate more responsive to recent regime shifts."
            | Funding_Root _ -> sprintf "Funding-rate parquet root. Default: %s" defaultFundingRoot
            | No_Funding -> "Disable funding-rate accounting even if data is available."
            | Results_Csv _ -> "Per-(symbol,timeframe,ma) results CSV path."
            | Summary_Csv _ -> "Aggregate per-(timeframe,ma) summary CSV path."
            | Parallelism _ -> "Max symbols processed concurrently. Default 4."

type CliArgs =
    | [<CliPrefix(CliPrefix.None)>] Run of ParseResults<RunArgs>
    | [<CliPrefix(CliPrefix.None)>] Sweep of ParseResults<SweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Vwma_Sweep of ParseResults<SweepArgs>
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Run _        -> "Run one (symbol, timeframe, ma) configuration."
            | Sweep _      -> "Run the full grid: timeframes × MA hours × symbols."
            | Vwma_Sweep _ -> "Research baseline: same grid, but signal is close-vs-VWMA crossover instead of orderflow ratio."

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
    let maHours = args.GetResult RunArgs.Ma_Hours
    let symbol = args.GetResult(RunArgs.Symbol, defaultValue = "BTCUSDT")
    let dataRoot = args.GetResult(RunArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(RunArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains RunArgs.Use_Trades
    let startDate = args.TryGetResult RunArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult RunArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let cfg =
        { defaultConfig maHours with
            Notional = args.GetResult(RunArgs.Notional, defaultValue = 1000.0)
            TakerFee = args.GetResult(RunArgs.Taker_Fee, defaultValue = 0.0004)
            AllowShort = args.Contains RunArgs.Allow_Short }
    let useBarPath = not useTrades && BarLoader.exists barsRoot timeframe symbol
    let pathLabel = if useBarPath then "bars" else "trades"
    printfn "[run] symbol=%s timeframe=%s ma=%dh short=%b range=%s..%s notional=%g fee=%g path=%s"
        symbol timeframe maHours cfg.AllowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        cfg.Notional cfg.TakerFee pathLabel
    let sw = Stopwatch.StartNew()
    let metrics, trips =
        if useBarPath then
            let cell = Cell(symbol, timeframe, cfg)
            // run subcommand always reads funding when available; pass --use-trades
            // to disable both the bar path and funding accounting.
            let fundingRoot = Some defaultFundingRoot
            let metrics, _adv = runCellsFromBars barsRoot symbol startDate endDate [| cell |] fundingRoot
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
                sprintf "%s-%s-ma%dh%s.csv"
                    symbol timeframe maHours (if cfg.AllowShort then "-short" else ""))
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
    let maHoursList =
        args.TryGetResult SweepArgs.Ma_Hours
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue defaultMaHours
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
    let minDailyVolume = args.GetResult(Min_Daily_Volume, defaultValue = 500_000.0)
    let minBarQuoteVolume = args.GetResult(Min_Bar_Quote_Volume, defaultValue = 0.0)
    let maxAdversePct = args.GetResult(Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(Vol_Window_Days, defaultValue = 90)
    let fundingRoot =
        if args.Contains No_Funding then None
        else Some (args.GetResult(Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(Results_Csv, defaultValue = defaultResultsCsv)
    let summaryCsv = args.GetResult(Summary_Csv, defaultValue = defaultSummaryCsv)
    let parallelism = args.GetResult(Parallelism, defaultValue = 4)

    printfn "[sweep] symbols=%d timeframes=[%s] mas=[%s] short=%b range=%s..%s parallelism=%d path=%s minDailyVol=$%s minBarQVol=$%s maxAdversePct=%g referenceVolPct=%g minLongAdv=$%s minShortAdv=$%s volWindowDays=%d"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (maHoursList |> Array.map string))
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (if useTrades then "trades" else "bars")
        (minDailyVolume.ToString("N0"))
        (minBarQuoteVolume.ToString("N0"))
        maxAdversePct
        referenceVolPct
        (minLongAdv.ToString("N0"))
        (minShortAdv.ToString("N0"))
        volWindowDays

    // Stream results: one row per (symbol, timeframe, ma) appended as it
    // finishes, so a partial run still leaves a usable file. Header is
    // written once on first append.
    if File.Exists resultsCsv then File.Delete resultsCsv
    // Clear any per-cell-config trips files from a prior run so this run
    // starts fresh. Pattern: {results-stem}_trips_{tf}_ma{N}_{ls|long}.csv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    // Round-trips are captured per (timeframe, ma_hours, allow_short) so the
    // breakdown report can pool them within each cell-config without mixing
    // across configs.
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (ma: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, ma, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    // Per-symbol average daily quote volume (USDT). Computed once per symbol
    // during runCellsFromBars and kept here for the breakdown's volume-decile
    // stratification.
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * maHoursList.Length
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
                        for maHours in maHoursList do
                            let cfg =
                                { defaultConfig maHours with
                                    Notional = notional
                                    TakerFee = takerFee
                                    AllowShort = allowShort
                                    MinDailyQuoteVolume = minDailyVolume
                                    MinBarQuoteVolume = minBarQuoteVolume
                                    MaxAdverseFraction = maxAdversePct / 100.0
                                    ReferenceVol = referenceVolPct / 100.0
                                    MinLongAdv = minLongAdv
                                    MinShortAdv = minShortAdv
                                    VolWindowDays = volWindowDays }
                            yield Cell(symbol, tf, cfg) |]
                let metrics, adv =
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
                        // Trades path doesn't compute ADV (would require a
                        // separate pass); leave it 0 and the breakdown will
                        // see all symbols in the bottom decile.
                        runCells dataRoot symbol startDate endDate cells (Some onDay), 0.0
                    else
                        runCellsFromBars barsRoot symbol startDate endDate cells fundingRoot
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    // Capture round-trips per (tf, ma, allowShort) bucket so the
                    // breakdown can pool them later. Cells are 1:1 with metrics.
                    for cell in cells do
                        let trips = cell.Trips
                        let bag = getTripBag cell.Timeframe cell.Config.MaWindowHours cell.Config.AllowShort
                        for t in trips do bag.Add t
                        // Also stream this cell's trips to a per-cell-config CSV
                        // (one file per (timeframe, ma_hours, allow_short)).
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let shortTag = if cell.Config.AllowShort then "ls" else "long"
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_ma%dh_%s.csv"
                                        stem cell.Timeframe cell.Config.MaWindowHours shortTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
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

    // Orb-style breakdown — one section per (timeframe, ma_hours, allow_short)
    // group, written to both stdout and a log file alongside the CSVs.
    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    // Sort groups by their summary order so the most interesting cells come
    // first when scrolling through.
    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[sweep] breakdown -> %s" breakdownLogPath
    printfn "[sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
    0

// =============================================================================
// vwma-sweep — research baseline (close-vs-VWMA crossover signal)
// =============================================================================
//
// Same flags as `sweep`, same gates, same output schema. Default output paths
// suffixed with `_vwma` so the two backtests don't clobber each other when
// the user runs both. Default results dir is `data/crypto/vwma/`.
//
// Wraps `Backtest.VwmaCell` instead of `Backtest.Cell` — the only difference
// from `cmdSweep`. We deliberately did NOT factor the two into a generic
// helper: this command exists to verify a one-off research hypothesis (does
// VWMA-crossover beat orderflow-MA?) and will be deleted once that question
// is answered. Keeping it self-contained makes the deletion easy.

let cmdVwmaSweep (args: ParseResults<SweepArgs>) : int =
    let dataRoot = args.GetResult(SweepArgs.Data_Root, defaultValue = defaultDataRoot)
    let barsRoot = args.GetResult(SweepArgs.Bars_Root, defaultValue = defaultBarsRoot)
    let useTrades = args.Contains SweepArgs.Use_Trades
    let startDate = args.TryGetResult SweepArgs.Start_Date |> Option.map parseDate |> Option.defaultValue defaultStart
    let endDate   = args.TryGetResult SweepArgs.End_Date   |> Option.map parseDate |> Option.defaultValue defaultEnd
    let timeframes =
        args.TryGetResult SweepArgs.Timeframes
        |> Option.map (parseList ',')
        |> Option.defaultValue defaultTimeframes
    let maHoursList =
        args.TryGetResult SweepArgs.Ma_Hours
        |> Option.map (parseList ',')
        |> Option.map (Array.map Int32.Parse)
        |> Option.defaultValue defaultMaHours
    let symbols =
        match args.TryGetResult SweepArgs.Symbol with
        | Some s -> parseList ',' s
        | None ->
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
    let minDailyVolume = args.GetResult(Min_Daily_Volume, defaultValue = 500_000.0)
    let minBarQuoteVolume = args.GetResult(Min_Bar_Quote_Volume, defaultValue = 0.0)
    let maxAdversePct = args.GetResult(Max_Adverse_Pct, defaultValue = 0.0)
    let referenceVolPct = args.GetResult(Reference_Vol_Pct, defaultValue = 0.0)
    let minLongAdv = args.GetResult(Min_Long_Adv, defaultValue = 0.0)
    let minShortAdv = args.GetResult(Min_Short_Adv, defaultValue = 0.0)
    let volWindowDays = args.GetResult(Vol_Window_Days, defaultValue = 90)
    let fundingRoot =
        if args.Contains No_Funding then None
        else Some (args.GetResult(Funding_Root, defaultValue = defaultFundingRoot))
    let resultsCsv = args.GetResult(Results_Csv, defaultValue = "data/crypto/vwma/backtest_results.csv")
    let summaryCsv = args.GetResult(Summary_Csv, defaultValue = "data/crypto/vwma/backtest_summary.csv")
    let parallelism = args.GetResult(Parallelism, defaultValue = 4)

    printfn "[vwma-sweep] symbols=%d timeframes=[%s] mas=[%s] short=%b range=%s..%s parallelism=%d path=%s minDailyVol=$%s minBarQVol=$%s maxAdversePct=%g referenceVolPct=%g minLongAdv=$%s minShortAdv=$%s volWindowDays=%d"
        symbols.Length
        (String.concat "," timeframes)
        (String.concat "," (maHoursList |> Array.map string))
        allowShort
        (startDate.ToString "yyyy-MM-dd") (endDate.ToString "yyyy-MM-dd")
        parallelism
        (if useTrades then "trades" else "bars")
        (minDailyVolume.ToString("N0"))
        (minBarQuoteVolume.ToString("N0"))
        maxAdversePct
        referenceVolPct
        (minLongAdv.ToString("N0"))
        (minShortAdv.ToString("N0"))
        volWindowDays

    if File.Exists resultsCsv then File.Delete resultsCsv
    let resultsDir = Path.GetDirectoryName resultsCsv
    let resultsStem = Path.GetFileNameWithoutExtension resultsCsv
    if Directory.Exists resultsDir then
        for f in Directory.EnumerateFiles(resultsDir, sprintf "%s_trips_*.csv" resultsStem) do
            File.Delete f

    let allMetrics = System.Collections.Concurrent.ConcurrentBag<Metrics>()
    let allTripsByGroup =
        System.Collections.Concurrent.ConcurrentDictionary<string * int * bool, System.Collections.Concurrent.ConcurrentBag<RoundTrip>>()
    let getTripBag (tf: string) (ma: int) (sh: bool) =
        allTripsByGroup.GetOrAdd((tf, ma, sh), fun _ -> System.Collections.Concurrent.ConcurrentBag<RoundTrip>())
    let advBySymbol =
        System.Collections.Concurrent.ConcurrentDictionary<string, float>()
    let writeLock = obj()
    let totalCells = symbols.Length * timeframes.Length * maHoursList.Length
    let mutable doneCells = 0

    // VWMA-cell variant of runCellsFromBars. Inline because there's only one
    // call site and we don't want a generic helper for one-off research code.
    let runVwmaCellsFromBars
        (symbol: string)
        (cells: VwmaCell[])
        : Metrics[] * float =
        let fundingEvents =
            match fundingRoot with
            | Some root when FundingLoader.exists root symbol ->
                FundingLoader.loadByDate root symbol startDate endDate
                |> Array.map (fun e -> e.TimestampUs, e.Rate)
            | _ -> [||]
        if fundingEvents.Length > 0 then
            for cell in cells do
                cell.SetFundingEvents fundingEvents
        let byTf = cells |> Array.groupBy (fun c -> c.Timeframe)
        let mutable adv = 0.0
        let mutable advSet = false
        for (tf, group) in byTf do
            let bars = BarLoader.loadByDate barsRoot tf symbol startDate endDate
            if not advSet && bars.Length > 0 then
                adv <- avgDailyVolume bars
                advSet <- true
            for cell in group do
                cell.PushBars bars
        for cell in cells do
            cell.Close()
        let metrics = cells |> Array.map (fun c -> c.BuildMetrics())
        metrics, adv

    let swAll = Stopwatch.StartNew()
    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        symbols,
        opts,
        fun symbol ->
            let swSym = Stopwatch.StartNew()
            try
                let cells =
                    [| for tf in timeframes do
                        for maHours in maHoursList do
                            let cfg =
                                { defaultConfig maHours with
                                    Notional = notional
                                    TakerFee = takerFee
                                    AllowShort = allowShort
                                    MinDailyQuoteVolume = minDailyVolume
                                    MinBarQuoteVolume = minBarQuoteVolume
                                    MaxAdverseFraction = maxAdversePct / 100.0
                                    ReferenceVol = referenceVolPct / 100.0
                                    MinLongAdv = minLongAdv
                                    MinShortAdv = minShortAdv
                                    VolWindowDays = volWindowDays }
                            yield VwmaCell(symbol, tf, cfg) |]
                let metrics, adv =
                    if useTrades then
                        // Trades-path is unsupported for vwma-sweep — the
                        // research command only runs against pre-aggregated
                        // bars. Fall back to bars regardless of --use-trades.
                        runVwmaCellsFromBars symbol cells
                    else
                        runVwmaCellsFromBars symbol cells
                let nonEmpty = metrics |> Array.filter (fun m -> m.BarsTotal > 0)
                if nonEmpty.Length = 0 then
                    lock writeLock (fun () ->
                        printfn "[vwma-sweep] %s: no bars in window" symbol)
                else
                    if adv > 0.0 then advBySymbol.[symbol] <- adv
                    for cell in cells do
                        let trips = cell.Trips
                        let bag = getTripBag cell.Timeframe cell.Config.MaWindowHours cell.Config.AllowShort
                        for t in trips do bag.Add t
                        if trips.Length > 0 then
                            let tripsPath =
                                let dir = Path.GetDirectoryName resultsCsv
                                let stem = Path.GetFileNameWithoutExtension resultsCsv
                                let shortTag = if cell.Config.AllowShort then "ls" else "long"
                                Path.Combine(dir,
                                    sprintf "%s_trips_%s_ma%dh_%s.csv"
                                        stem cell.Timeframe cell.Config.MaWindowHours shortTag)
                            lock writeLock (fun () ->
                                appendTrips tripsPath cell.Symbol cell.Timeframe cell.Config trips)
                    lock writeLock (fun () ->
                        appendResults resultsCsv metrics
                        for m in metrics do allMetrics.Add m
                        doneCells <- doneCells + metrics.Length)
                    swSym.Stop()
                    lock writeLock (fun () ->
                        printfn "[vwma-sweep] %s done in %.1fs (%d/%d cells)"
                            symbol swSym.Elapsed.TotalSeconds doneCells totalCells)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[vwma-sweep] %s FAILED: %s" symbol ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore
    swAll.Stop()

    let metricsArr = allMetrics.ToArray()
    let summary = summarize metricsArr
    let summarySorted =
        summary |> Array.sortByDescending (fun s -> s.MedianSharpe)
    writeSummary summaryCsv summarySorted
    printfn "[vwma-sweep] wrote %d result rows -> %s" metricsArr.Length resultsCsv
    printfn "[vwma-sweep] wrote %d summary rows -> %s" summarySorted.Length summaryCsv

    let breakdownLogPath =
        let dir = Path.GetDirectoryName resultsCsv
        let stem = Path.GetFileNameWithoutExtension resultsCsv
        Path.Combine(dir, sprintf "%s_breakdown.log" stem)
    Directory.CreateDirectory(Path.GetDirectoryName breakdownLogPath) |> ignore
    use logWriter = new StreamWriter(breakdownLogPath, false)
    let logWrite (s: string) =
        logWriter.WriteLine s
        logWriter.Flush()
    let consoleWrite (s: string) = Console.WriteLine s

    for s in summarySorted do
        let cellMetrics =
            metricsArr
            |> Array.filter (fun m ->
                m.Timeframe = s.Timeframe && m.MaWindowHours = s.MaWindowHours && m.AllowShort = s.AllowShort
                && m.BarsTotal > 0)
        let trips =
            match allTripsByGroup.TryGetValue((s.Timeframe, s.MaWindowHours, s.AllowShort)) with
            | true, bag -> bag.ToArray()
            | _ -> [||]
        printGroupBreakdown logWrite consoleWrite s.Timeframe s.MaWindowHours s.AllowShort notional cellMetrics trips advBySymbol

    printfn ""
    printfn "[vwma-sweep] breakdown -> %s" breakdownLogPath
    printfn "[vwma-sweep] total wall %.1fs" swAll.Elapsed.TotalSeconds
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
        | Vwma_Sweep a -> cmdVwmaSweep a
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        2
