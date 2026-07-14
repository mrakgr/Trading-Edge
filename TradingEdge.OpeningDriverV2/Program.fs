module TradingEdge.OpeningDriverV2.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.OpeningDriverV2.Intraday
open TradingEdge.OpeningDriverV2.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/opening_driver_v2_trips.csv"

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
    // ----- the settled arm gates -----
    | Min_Chg_1d of float
    | Min_Chg_3d of float
    | Max_Chg_3d of float
    | Min_Log_Atr of float
    | Min_Stop_Dist_Pct of float
    | Bl_Max of int
    | Bh_Min of int
    | Min_Vol_Slope of float
    | Vol_Slope_As_Gate
    // ----- the blow-off exhaustion kill-switch -----
    | Exhaust_Brv20d of float
    | Exhaust_Min_Atr_Pct of float
    | Exhaust_Exit

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files. Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2003-09-10 (data min)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (minute-data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/opening_driver_v2_trips.csv."
            | Entry_Start_Min _ -> "Earliest ET minute the arm scan starts (585 = 09:45 = open+15, default)."
            | Entry_End_Min _ -> "Latest ET minute the arm scan fires (630 = 10:30 = open+60, default). 0 or >=MOC = all-day."
            | Feature_Start_Min _ -> "ET minute the trailing features start folding (570 = 09:30 default, the RTH open)."
            | Vol_Window _ -> "Trailing ATR/OLS-slope lookback in 1m bars (default 20)."
            | Ema_Period _ -> "The 9-EMA period (stop reference + slope base; default 9)."
            | Stop_Mode _ -> "9-EMA stop reference: sess-ema-low (default) | vwap. Stop fires when the live 9-EMA drops below the 9-EMA session-min frozen at entry, or below the live session VWAP."
            | Min_Chg_1d _ -> "Day-direction floor: entry/close_1d-1 >= this (default 0.20 = up >=20%)."
            | Min_Chg_3d _ -> "3-day-trend band floor: entry/close_3d-1 >= this (default 0.0)."
            | Max_Chg_3d _ -> "3-day-trend band ceiling: entry/close_3d-1 <= this (default 1.5)."
            | Min_Log_Atr _ -> "20m log-ATR jumpiness guard: log_atr_20 >= this (default 0.013)."
            | Min_Stop_Dist_Pct _ -> "Room to the stop: (entry-stop)/entry >= this (default 0.03 = 3%)."
            | Bl_Max _ -> "Freshness cap: bars-since-EMA-low < this (default 15). 0 disables."
            | Bh_Min _ -> "Pullback floor: bars-since-EMA-high >= this (default 1). 0 disables."
            | Min_Vol_Slope _ -> "20m OLS log-volume slope floor: vol_slope_20 >= this (default 0.0)."
            | Vol_Slope_As_Gate -> "Treat vol_slope as a GATE (keep scanning past failures) instead of the default SKIP filter (first arm bar disarms the day; no position if vol_slope fails there)."
            | Exhaust_Brv20d _ -> "Blow-off kill-switch (default 0 = off): once a bar prints a new session high with brv20d >= this (1m bar volume / (avgvol20·adj/390), the per-minute 20d ADV — 100 = the MaxFlyerV3 short-arm value) AND ATR% >= --exhaust-min-atr-pct, the day is latched exhausted and no arm fires."
            | Exhaust_Min_Atr_Pct _ -> "ATR% floor the climax bar must meet for the exhaustion latch (default 0.03; only used when --exhaust-brv20d > 0)."
            | Exhaust_Exit -> "When the blow-off latch fires, also CLOSE any open position at that bar (an exhaustion EXIT), not just cut new arms. Needs --exhaust-brv20d > 0."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "opening-driver-v2")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let minuteDir = parsed.GetResult(Minute_Dir, defaultValue = defaultMinuteDir)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2003-09-10"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-06-25"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let stopMode =
        match parsed.GetResult(Stop_Mode, defaultValue = "sess-ema-low") with
        | "vwap"         -> BelowVwap
        | "sess-ema-low" -> BelowSessEmaLow
        | bad -> failwithf "Invalid --stop-mode %A (vwap | sess-ema-low)" bad

    let dic = defaultConfig.Intraday
    let cfg =
        { defaultConfig with
            Intraday =
              { dic with
                  EntryStartMin   = parsed.GetResult(Entry_Start_Min,   defaultValue = dic.EntryStartMin)
                  EntryEndMin     = parsed.GetResult(Entry_End_Min,     defaultValue = dic.EntryEndMin)
                  FeatureStartMin = parsed.GetResult(Feature_Start_Min, defaultValue = dic.FeatureStartMin)
                  VolWindow       = parsed.GetResult(Vol_Window,        defaultValue = dic.VolWindow)
                  EmaPeriod       = parsed.GetResult(Ema_Period,        defaultValue = dic.EmaPeriod)
                  StopMode        = stopMode
                  MinChg1d        = parsed.GetResult(Min_Chg_1d,        defaultValue = dic.MinChg1d)
                  MinChg3d        = parsed.GetResult(Min_Chg_3d,        defaultValue = dic.MinChg3d)
                  MaxChg3d        = parsed.GetResult(Max_Chg_3d,        defaultValue = dic.MaxChg3d)
                  MinLogAtr       = parsed.GetResult(Min_Log_Atr,       defaultValue = dic.MinLogAtr)
                  MinStopDistPct  = parsed.GetResult(Min_Stop_Dist_Pct, defaultValue = dic.MinStopDistPct)
                  BlMax           = parsed.GetResult(Bl_Max,            defaultValue = dic.BlMax)
                  BhMin           = parsed.GetResult(Bh_Min,            defaultValue = dic.BhMin)
                  MinVolSlope     = parsed.GetResult(Min_Vol_Slope,     defaultValue = dic.MinVolSlope)
                  VolSlopeAsGate  = parsed.Contains Vol_Slope_As_Gate
                  ExhaustBrv20d    = parsed.GetResult(Exhaust_Brv20d,      defaultValue = dic.ExhaustBrv20d)
                  ExhaustMinAtrPct = parsed.GetResult(Exhaust_Min_Atr_Pct, defaultValue = dic.ExhaustMinAtrPct)
                  ExhaustExit      = parsed.Contains Exhaust_Exit } }

    let ic = cfg.Intraday
    let hhmm m = sprintf "%02d:%02d" (m / 60) (m % 60)
    printfn "OpeningDriverV2 — arm/disarm once per day: first bar past the settled gates opens ONE trip (LONG intraday)"
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  features    = fold from %s ET (VWAP / OLS price&vol slope / log-ATR / 9-EMA / cum-vol)" (hhmm ic.FeatureStartMin)
    printfn "  arm window  = %s–%s ET   (first qualifying bar arms, then disarm)" (hhmm ic.EntryStartMin) (hhmm ic.EntryEndMin)
    printfn "  gates       = chg_1d>=%.2f  chg_3d in [%.2f,%.2f]  log_atr>=%.4f  stop_dist>=%.3f  bl<%d  bh>=%d"
        ic.MinChg1d ic.MinChg3d ic.MaxChg3d ic.MinLogAtr ic.MinStopDistPct ic.BlMax ic.BhMin
    printfn "  vol_slope   = >=%.4f as a %s" ic.MinVolSlope (if ic.VolSlopeAsGate then "GATE (keep scanning on fail)" else "SKIP filter (burn the day on fail)")
    printfn "  exhaustion  = %s"
        (if ic.ExhaustBrv20d > 0.0 then sprintf "ON — latch if a new-high bar hits brv20d>=%.0f & ATR%%>=%.3f; %s" ic.ExhaustBrv20d ic.ExhaustMinAtrPct (if ic.ExhaustExit then "CUT arms + EXIT held" else "CUT arms only") else "off")
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
