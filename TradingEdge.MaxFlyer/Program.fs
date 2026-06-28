module TradingEdge.MaxFlyer.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.MaxFlyer.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/maxflyer_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    // Gate 1 — daily selection (D-1)
    | Min_Price of float
    | Min_52w_Pct of float
    | Use_52w_High
    | Max_Tightness of float
    | Max_Atr_Pct of float
    | Min_Max_Atr_Log of float
    | Min_Avg_Dollar_Volume of float
    | Vol_Window of int
    | Min_Prior_Days of int
    // Gate 2 — premarket (D)
    | Min_Gap_Pct of float
    | Max_Gap_Pct of float
    | Min_Premkt_Vol of int64
    // Gate 3 — intraday engine
    | Intraday_Vol_Window of int
    | Intraday_Max_Tightness of float
    | Intraday_Max_Atr_Pct of float
    | Min_High_Bars of int
    | Intraday_Stop
    | Moc_Min of int
    | Notional of float
    | Max_Concurrent of int

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs/{date}.parquet (1m bars). Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2021-06-17 (dense 1m coverage)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/maxflyer_trips.csv."
            // Gate 1
            | Min_Price _ -> "Gate 1: min D-1 close price. Default 1.0."
            | Min_52w_Pct _ -> "Gate 1: 52w-high proximity — require close >= this * prior-252d-high. Default 0.95."
            | Use_52w_High -> "Gate 1: gate the 52w band on the prior-252d INTRADAY high instead of the closing high. Default off."
            | Max_Tightness _ -> "Gate 1: max daily consolidation tightness (linear). Default 4.5."
            | Max_Atr_Pct _ -> "Gate 1: max daily ATR%% (log-space). Default 0.10."
            | Min_Max_Atr_Log _ -> "Gate 1: min 'max log ATR' (126-bar max of 14-bar log-ATR) — past-runner floor. Default 0.04. 0 disables."
            | Min_Avg_Dollar_Volume _ -> "Gate 1: min 4-week avg dollar volume. Default 100000."
            | Vol_Window _ -> "Gate 1: lookback (bars) for BOTH daily ATR%% and tightness. Default 14."
            | Min_Prior_Days _ -> "Gate 1: require this many prior daily bars (warmup). Default 21."
            // Gate 2
            | Min_Gap_Pct _ -> "Gate 2: min day-D gap%% = rth_open/prev_adj_close-1. Default 0.0. WIDE by design."
            | Max_Gap_Pct _ -> "Gate 2: max day-D gap%%. Default 2.0 (200%%). Pass a large value to disable the cap."
            | Min_Premkt_Vol _ -> "Gate 2: min premarket volume (shares, 04:00-09:29 ET). Default 0."
            // Gate 3
            | Intraday_Vol_Window _ -> "Gate 3: intraday ATR/tightness lookback in 1m BARS. Default 20."
            | Intraday_Max_Tightness _ -> "Gate 3: max intraday tightness (linear) at entry. Default +inf (off). Sweep this in."
            | Intraday_Max_Atr_Pct _ -> "Gate 3: max intraday ATR%% (log-space) at entry. Default +inf (off). Sweep this in."
            | Min_High_Bars _ -> "Gate 3: require this many prior RTH 1m bars before a new-high breakout counts (warmup). Default 15."
            | Intraday_Stop -> "Gate 3: arm the protective intraday-low stop (the session low at entry). Default off (hold to MOC)."
            | Moc_Min _ -> "Gate 3: MOC cutoff in ET minutes-since-midnight. Default 960 (16:00). RTH bars are 09:30..MOC."
            | Notional _ -> "Per-trip notional ($). Default 10000."
            | Max_Concurrent _ -> "Gate 3: cap on currently-OPEN positions per day (0 = unlimited). Default 0."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "maxflyer")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let minuteDir = parsed.GetResult(Minute_Dir, defaultValue = defaultMinuteDir)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2021-06-17"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-06-25"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let dc = defaultConfig
    let cfg =
        { dc with
            AtrWindow       = parsed.GetResult(Vol_Window, defaultValue = dc.AtrWindow)
            TightnessWindow = parsed.GetResult(Vol_Window, defaultValue = dc.TightnessWindow)
            MinGapPct    = parsed.GetResult(Min_Gap_Pct,    defaultValue = dc.MinGapPct)
            MaxGapPct    = parsed.GetResult(Max_Gap_Pct,    defaultValue = dc.MaxGapPct)
            MinPremktVol = parsed.GetResult(Min_Premkt_Vol, defaultValue = dc.MinPremktVol)
            Notional     = parsed.GetResult(Notional,       defaultValue = dc.Notional)
            Daily =
              { dc.Daily with
                  MinPrice           = parsed.GetResult(Min_Price,             defaultValue = dc.Daily.MinPrice)
                  Min52wPct          = parsed.GetResult(Min_52w_Pct,           defaultValue = dc.Daily.Min52wPct)
                  Use52wHigh         = parsed.Contains Use_52w_High
                  MaxTightness       = parsed.GetResult(Max_Tightness,         defaultValue = dc.Daily.MaxTightness)
                  MaxAtrPct          = parsed.GetResult(Max_Atr_Pct,           defaultValue = dc.Daily.MaxAtrPct)
                  MinMaxAtrLog       = parsed.GetResult(Min_Max_Atr_Log,       defaultValue = dc.Daily.MinMaxAtrLog)
                  MinAvgDollarVolume = parsed.GetResult(Min_Avg_Dollar_Volume, defaultValue = dc.Daily.MinAvgDollarVolume)
                  MinPriorDays       = parsed.GetResult(Min_Prior_Days,        defaultValue = dc.Daily.MinPriorDays) }
            Intraday =
              { dc.Intraday with
                  VolWindow     = parsed.GetResult(Intraday_Vol_Window,    defaultValue = dc.Intraday.VolWindow)
                  MaxTightness  = parsed.GetResult(Intraday_Max_Tightness, defaultValue = dc.Intraday.MaxTightness)
                  MaxAtrPct     = parsed.GetResult(Intraday_Max_Atr_Pct,   defaultValue = dc.Intraday.MaxAtrPct)
                  MinHighBars   = parsed.GetResult(Min_High_Bars,          defaultValue = dc.Intraday.MinHighBars)
                  UseStop       = parsed.Contains Intraday_Stop
                  MocMin        = parsed.GetResult(Moc_Min,                defaultValue = dc.Intraday.MocMin)
                  MaxConcurrent = parsed.GetResult(Max_Concurrent,         defaultValue = dc.Intraday.MaxConcurrent) } }

    let inf (x: float) = if Double.IsInfinity x then "inf" else sprintf "%.3f" x

    printfn "MaxFlyer backtest"
    printfn "  db        = %s" dbPath
    printfn "  minute    = %s" minuteDir
    printfn "  range     = %O .. %O" startDate endDate
    printfn "  Gate1 (daily): price>=%.2f 52w>=%.2f%s tight<%.2f atr%%<%.3f maxlogatr>=%.3f adv>=%.0f volwin=%d warmup>%d"
        cfg.Daily.MinPrice cfg.Daily.Min52wPct (if cfg.Daily.Use52wHigh then "(intraday-high)" else "")
        cfg.Daily.MaxTightness cfg.Daily.MaxAtrPct cfg.Daily.MinMaxAtrLog cfg.Daily.MinAvgDollarVolume
        cfg.AtrWindow cfg.Daily.MinPriorDays
    printfn "  Gate2 (premarket): gap[%.2f,%.2f] premkt_vol>=%d" cfg.MinGapPct cfg.MaxGapPct cfg.MinPremktVol
    printfn "  Gate3 (intraday): volwin=%d tight<%s atr%%<%s min_high_bars=%d stop=%b moc=%s max_concurrent=%d notional=%.0f"
        cfg.Intraday.VolWindow (inf cfg.Intraday.MaxTightness) (inf cfg.Intraday.MaxAtrPct)
        cfg.Intraday.MinHighBars cfg.Intraday.UseStop
        (sprintf "%02d:%02d" (cfg.Intraday.MocMin / 60) (cfg.Intraday.MocMin % 60))
        cfg.Intraday.MaxConcurrent cfg.Notional

    let sw = Stopwatch.StartNew()
    let trips, nCandidates = run dbPath minuteDir cfg startDate endDate
    sw.Stop()

    writeCsv outPath trips

    let wins = trips |> Array.filter (fun t -> t.NetPnL > 0.0)
    let losses = trips |> Array.filter (fun t -> t.NetPnL < 0.0)
    let sumW = wins |> Array.sumBy (fun t -> t.NetPnL)
    let sumL = losses |> Array.sumBy (fun t -> t.NetPnL)
    let pf = if sumL = 0.0 then nan else sumW / -sumL
    let netPnl = trips |> Array.sumBy (fun t -> t.NetPnL)

    printfn ""
    printfn "  candidates= %d  (Gate1 & Gate2 passed)" nCandidates
    printfn "  trips     = %d  (%.1f s)" trips.Length sw.Elapsed.TotalSeconds
    printfn "  win rate  = %.1f%%  (%d / %d)"
        (100.0 * float wins.Length / float (max 1 trips.Length)) wins.Length trips.Length
    printfn "  net P&L   = %s" (netPnl.ToString("N0"))
    printfn "  PF        = %.3f" pf
    printfn "  wrote     = %s" outPath
    0
