module TradingEdge.OpeningDriver.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.OpeningDriver.Intraday
open TradingEdge.OpeningDriver.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/opening_driver_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    // ----- timing -----
    | Entry_Start_Min of int
    | Entry_End_Min of int
    | Feature_Start_Min of int
    | Vol_Window of int
    | Ema_Period of int
    // ----- stop -----
    | Stop_Mode of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files. Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2003-09-10 (data min)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (minute-data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/opening_driver_trips.csv."
            | Entry_Start_Min _ -> "Earliest ET minute a window entry fires (585 = 09:45 = open+15, default). Every bar in [start,end] emits a trip."
            | Entry_End_Min _ -> "Latest ET minute a window entry fires (630 = 10:30 = open+60, default). 0 or >=MOC = all-day."
            | Feature_Start_Min _ -> "ET minute the trailing features start folding (570 = 09:30 default, the RTH open)."
            | Vol_Window _ -> "Trailing ATR/OLS-slope lookback in 1m bars (default 20)."
            | Ema_Period _ -> "The 9-EMA period (stop reference + slope base; default 9)."
            | Stop_Mode _ -> "9-EMA stop reference: vwap (default) | sess-ema-low. Stop fires when the live 9-EMA drops below the live session VWAP, or below the 9-EMA session-min frozen at entry."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "opening-driver")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let minuteDir = parsed.GetResult(Minute_Dir, defaultValue = defaultMinuteDir)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2003-09-10"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-06-25"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let stopMode =
        match parsed.GetResult(Stop_Mode, defaultValue = "vwap") with
        | "vwap"         -> BelowVwap
        | "sess-ema-low" -> BelowSessEmaLow
        | bad -> failwithf "Invalid --stop-mode %A (vwap | sess-ema-low)" bad

    let cfg =
        { defaultConfig with
            Intraday =
              { defaultConfig.Intraday with
                  EntryStartMin   = parsed.GetResult(Entry_Start_Min,   defaultValue = defaultConfig.Intraday.EntryStartMin)
                  EntryEndMin     = parsed.GetResult(Entry_End_Min,     defaultValue = defaultConfig.Intraday.EntryEndMin)
                  FeatureStartMin = parsed.GetResult(Feature_Start_Min, defaultValue = defaultConfig.Intraday.FeatureStartMin)
                  VolWindow       = parsed.GetResult(Vol_Window,        defaultValue = defaultConfig.Intraday.VolWindow)
                  EmaPeriod       = parsed.GetResult(Ema_Period,        defaultValue = defaultConfig.Intraday.EmaPeriod)
                  StopMode        = stopMode } }

    let ic = cfg.Intraday
    let hhmm m = sprintf "%02d:%02d" (m / 60) (m % 60)
    printfn "OpeningDriver study — every window bar emits a trip, held to MOC or a 9-EMA stop (LONG intraday)"
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  features    = fold from %s ET (VWAP / OLS price&vol slope / log-ATR / 9-EMA / cum-vol)" (hhmm ic.FeatureStartMin)
    printfn "  entry window= %s–%s ET   (every bar in the window = one trip)" (hhmm ic.EntryStartMin) (hhmm ic.EntryEndMin)
    printfn "  stop        = 9-EMA below %s"
        (match ic.StopMode with BelowVwap -> "the live session VWAP" | BelowSessEmaLow -> "the 9-EMA session-min (frozen at entry)")
    printfn "  exits       = 9-EMA stop + hold-to-MOC"

    let sw = Stopwatch.StartNew()
    let trips, nCand = run dbPath minuteDir cfg startDate endDate
    sw.Stop()

    writeCsv outPath trips

    let wins   = trips |> Array.filter (fun t -> t.NetPnL > 0.0)
    let losses = trips |> Array.filter (fun t -> t.NetPnL < 0.0)
    let sumW = wins   |> Array.sumBy (fun t -> t.NetPnL)
    let sumL = losses |> Array.sumBy (fun t -> t.NetPnL)
    let pf = if sumL = 0.0 then nan else sumW / -sumL
    let netPnl = trips |> Array.sumBy (fun t -> t.NetPnL)

    printfn ""
    printfn "  candidates = %d  (ticker-days that cleared the preconditions)" nCand
    printfn "  trips      = %d  (%.1f s)" trips.Length sw.Elapsed.TotalSeconds
    printfn "  win rate   = %.1f%%  (%d / %d)"
        (100.0 * float wins.Length / float (max 1 trips.Length)) wins.Length trips.Length
    printfn "  net P&L    = %s" (netPnl.ToString("N0"))
    printfn "  PF (MOC)   = %.3f" pf
    printfn "  wrote      = %s" outPath
    0
