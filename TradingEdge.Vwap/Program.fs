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

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Ticker _ -> "Ticker (default: SPY)"
            | Start_Date _ -> "First date inclusive (yyyy-mm-dd; default: 2024-01-01)"
            | End_Date _ -> "Last date inclusive (yyyy-mm-dd; default: today)"
            | Bars_Dir _ -> "1m bars dir (default: data/minute_bars_1m)"
            | Output _ -> "Trade-log CSV output (default: data/vwap/trades_{ticker}_{start}_{end}.csv)"

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

        let trades = Backtest.runMany bars
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
