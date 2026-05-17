module TradingEdge.Vwap.Program

open System
open Argu
open TradingEdge.Vwap

type BacktestArgs =
    | [<AltCommandLine("-t")>] Ticker of string
    | [<AltCommandLine("-s")>] Start_Date of string
    | [<AltCommandLine("-e")>] End_Date of string
    | [<AltCommandLine("-b")>] Bars_Dir of string
    | [<AltCommandLine("-o")>] Output of string
    | Breadth of string
    | Long_Min_Imb of float
    | Short_Max_Imb of float
    | Filter_Mode of string
    | Database of string
    | Min_Adr_Pct_5d of float
    | Regime_Ticker of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Ticker _ -> "Ticker (default: SPY)"
            | Start_Date _ -> "First date inclusive (yyyy-mm-dd; default: 2024-01-01)"
            | End_Date _ -> "Last date inclusive (yyyy-mm-dd; default: today)"
            | Bars_Dir _ -> "1m bars dir (default: data/minute_bars_1m)"
            | Output _ -> "Trade-log CSV output (default: data/vwap/trades_{ticker}_{start}_{end}.csv)"
            | Breadth _ -> "Imbalance parquet path. If set, enables the filtered strategy. Example: data/breadth/imbalance_vwma_30m.parquet"
            | Long_Min_Imb _ -> "Allow longs only when imbalance >= this value (default: 0.0). Ignored unless --breadth is set."
            | Short_Max_Imb _ -> "Allow shorts only when imbalance <= this value (default: 0.0). Ignored unless --breadth is set."
            | Filter_Mode _ -> "Filter semantics: 'entry' = gate only checked at each VWAP cross, hold through. 'always' = gate re-checked every bar, flatten when gate goes red. Default: entry."
            | Database _ -> "DuckDB path for the regime gate (default: data/trading.db)."
            | Min_Adr_Pct_5d _ -> "Regime gate: only trade days where the prior 5d ADR (high-low) as %% of mean close exceeds this value. 0.0 = off (default)."
            | Regime_Ticker _ -> "Symbol used to compute the ADR regime gate (default: SPY)."

type Arguments =
    | [<CliPrefix(CliPrefix.None)>] Backtest of ParseResults<BacktestArgs>

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Backtest _ -> "Run the always-in VWAP-flip backtest over the given (ticker, date range)"

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

let private handleBacktest (args: ParseResults<BacktestArgs>) : int =
    let ticker = args.GetResult(BacktestArgs.Ticker, defaultValue = "SPY")
    let startDate =
        args.TryGetResult BacktestArgs.Start_Date
        |> Option.map parseDate
        |> Option.defaultValue (DateOnly(2024, 1, 1))
    let endDate =
        args.TryGetResult BacktestArgs.End_Date
        |> Option.map parseDate
        |> Option.defaultValue (DateOnly.FromDateTime DateTime.Today)
    let barsDir = args.GetResult(BacktestArgs.Bars_Dir, defaultValue = "data/minute_bars_1m")
    let outputPath =
        args.TryGetResult BacktestArgs.Output
        |> Option.defaultValue (
            sprintf "data/vwap/trades_%s_%s_%s.csv"
                ticker
                (startDate.ToString("yyyy-MM-dd"))
                (endDate.ToString("yyyy-MM-dd")))

    printfn "Loading %s 1m RTH bars %s..%s from %s"
        ticker (startDate.ToString("yyyy-MM-dd"))
        (endDate.ToString("yyyy-MM-dd")) barsDir

    let bars = BarLoader.loadRth barsDir ticker startDate endDate
    if bars.Length = 0 then
        eprintfn "No bars found. Check ticker/date range/bars_dir."
        1
    else
        let days = bars |> Array.map (fun b -> b.Date) |> Array.distinct
        printfn "Loaded %d bars across %d trading days" bars.Length days.Length

        // Build optional regime gate (ADR-based, lookahead-clean).
        let regimeOn : DateOnly -> bool =
            let minAdrPct = args.GetResult(BacktestArgs.Min_Adr_Pct_5d, defaultValue = 0.0)
            if minAdrPct <= 0.0 then fun _ -> true
            else
                let dbPath = args.GetResult(BacktestArgs.Database, defaultValue = "data/trading.db")
                let regimeTicker = args.GetResult(BacktestArgs.Regime_Ticker, defaultValue = "SPY")
                printfn "Regime gate: %s 5d-ADR-pct >= %g (db=%s)" regimeTicker minAdrPct dbPath
                let idx = AdrGate.build dbPath regimeTicker startDate endDate 5 minAdrPct
                fun d -> idx.IsOn d

        // Choose strategy mode: unfiltered if --breadth not set; filtered if it is.
        let trades =
            match args.TryGetResult BacktestArgs.Breadth with
            | None ->
                Backtest.runManyWithRegime bars regimeOn
            | Some breadthPath ->
                let longMin = args.GetResult(BacktestArgs.Long_Min_Imb, defaultValue = 0.0)
                let shortMax = args.GetResult(BacktestArgs.Short_Max_Imb, defaultValue = 0.0)
                let mode = args.GetResult(BacktestArgs.Filter_Mode, defaultValue = "entry")
                printfn "Filtered mode: breadth=%s  long_min_imb=%g  short_max_imb=%g  filter_mode=%s"
                    breadthPath longMin shortMax mode
                let breadthBars = BreadthLoader.load breadthPath startDate endDate
                printfn "Loaded %d breadth rows" breadthBars.Length
                let index = BreadthLoader.BreadthIndex(breadthBars)
                let longGate imb  = imb >= longMin
                let shortGate imb = imb <= shortMax
                let imbalanceOf d b = index.TryGet(d, b)
                match mode with
                | "entry" ->
                    Backtest.runManyEntryFilteredWithRegime bars imbalanceOf longGate shortGate regimeOn
                | "always" ->
                    Backtest.runManyFilteredWithRegime bars imbalanceOf longGate shortGate regimeOn
                | other ->
                    failwithf "Unknown --filter-mode '%s'. Expected 'entry' or 'always'." other
        let metrics = Backtest.computeMetrics trades

        Reporting.printSummary metrics
        Reporting.writeTrades outputPath trades
        printfn ""
        printfn "Wrote %d trades to %s" trades.Length outputPath
        0

[<EntryPoint>]
let main argv =
    let parser =
        ArgumentParser.Create<Arguments>(programName = "TradingEdge.Vwap")
    try
        let results = parser.Parse(argv)
        match results.GetSubCommand() with
        | Backtest args -> handleBacktest args
    with
    | :? ArguParseException as ex ->
        printfn "%s" ex.Message
        1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1
