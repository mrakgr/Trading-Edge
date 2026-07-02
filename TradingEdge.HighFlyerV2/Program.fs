module TradingEdge.HighFlyerV2.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.HighFlyerV2.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultCsv = "/tmp/highflyerv2_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    | Stop_Low_Window of int
    | Volatility_Window of int
    | Volume_Days of int
    | Max_Hold_Bars of int
    | Partial_Entry
    | Cutoff_Min of int
    | Up_Threshold of float
    | Max_Up_Threshold of float
    | Rvol_Min of float
    | Rvol_Max of float
    | Min_Price of float
    | Min_52w_Pct of float
    | Use_52w_High
    | Max_Tightness of float
    | Max_Atr_Pct of float
    | Min_Intraday_Ret of float
    | Min_Max_Atr_Log of float

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2005-01-01."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-05-13 (data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/highflyerv2_trips.csv."
            | Stop_Low_Window _ -> "Prior-window low for the stop_low_at_entry CSV snapshot. Default 4. (No stop logic consumes it.)"
            | Volatility_Window _ -> "Lookback window (bars) for BOTH the ATR%% and tightness measures. Default 14."
            | Volume_Days _ -> "Lookback window (BARS) for the rvol/ADV volume baseline (AvgMa). Default 20."
            | Max_Hold_Bars _ -> "Time-stop: exit at the next open after this many Holding bars (0 = off = hold to MTM). Default 5."
            | Partial_Entry -> "EXPERIMENT: decide + fill the entry on the PARTIAL checkpoint candle (partial_candle_HHMM) instead of the full daily close. Exits stay on the daily series. Days with no usable checkpoint candle are not entered. Default off (parity path)."
            | Cutoff_Min _ -> "With --partial-entry: which checkpoint table to read, by ET minutes-since-midnight (600=10:00 ET default, 630=10:30, 660=11:00). Maps to partial_candle_HHMM. The table must already be built (scripts/equity/build_partial_candle.fsx --cutoff-min N)."
            | Up_Threshold _ -> "Min entry-day move (close/prevClose-1). Default 0.10."
            | Max_Up_Threshold _ -> "MAX entry-day move (close/prevClose-1). Default 0.30 — caps the 30%+ blow-off. Pass a large value to disable."
            | Rvol_Min _ -> "Minimum relative volume at entry. Default 5.0."
            | Rvol_Max _ -> "Maximum relative volume at entry. Default +inf (uncapped)."
            | Min_Price _ -> "Min entry close price. Default 1.0. Pass 0 to admit sub-$1 names."
            | Min_52w_Pct _ -> "52-week-high proximity: require close >= this * prior-252d-high. Default 0.95."
            | Use_52w_High -> "Gate the 52w-proximity band on the prior-252d INTRADAY HIGH instead of the closing high. Default off."
            | Max_Tightness _ -> "Max entry tightness (LINEAR scale). Default 4.5. Pass a large value to disable."
            | Max_Atr_Pct _ -> "Max entry ATR%% (log scale). Default 0.10. Pass a large value to disable."
            | Min_Intraday_Ret _ -> "Min entry-day intraday return (close/open-1). Default -0.07 — rejects deep intraday FADES. Pass a large negative value to disable."
            | Min_Max_Atr_Log _ -> "Min 'max log ATR' = 126-bar max of the 14-bar log-ATR (past-runner FLOOR). Default 0.04. 0 disables."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "highflyerv2")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2005-01-01"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-05-13"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    // --cutoff-min selects the checkpoint table (600 -> partial_candle_1000, 630 -> _1030).
    let cutoffMin = parsed.GetResult(Cutoff_Min, defaultValue = 600)
    let partialTable = sprintf "partial_candle_%02d%02d" (cutoffMin / 60) (cutoffMin % 60)

    let cfg =
        { defaultConfig with
            // --volatility-window sets BOTH the ATR%% and tightness lookback.
            AtrWindow       = parsed.GetResult(Volatility_Window, defaultValue = defaultConfig.AtrWindow)
            TightnessWindow = parsed.GetResult(Volatility_Window, defaultValue = defaultConfig.TightnessWindow)
            StopLowWindow = parsed.GetResult(Stop_Low_Window, defaultValue = defaultConfig.StopLowWindow)
            VolDays = parsed.GetResult(Volume_Days, defaultValue = defaultConfig.VolDays)
            MaxHoldBars = parsed.GetResult(Max_Hold_Bars, defaultValue = defaultConfig.MaxHoldBars)
            UsePartialEntry = parsed.Contains Partial_Entry
            PartialTable = partialTable
            Entry =
              { defaultConfig.Entry with
                  UpThreshold    = parsed.GetResult(Up_Threshold,     defaultValue = defaultConfig.Entry.UpThreshold)
                  MaxUpThreshold = parsed.GetResult(Max_Up_Threshold, defaultValue = defaultConfig.Entry.MaxUpThreshold)
                  RvolMin        = parsed.GetResult(Rvol_Min,         defaultValue = defaultConfig.Entry.RvolMin)
                  RvolMax        = parsed.GetResult(Rvol_Max,         defaultValue = defaultConfig.Entry.RvolMax)
                  MinPrice       = parsed.GetResult(Min_Price,        defaultValue = defaultConfig.Entry.MinPrice)
                  Min52wPct      = parsed.GetResult(Min_52w_Pct,      defaultValue = defaultConfig.Entry.Min52wPct)
                  Use52wHigh     = parsed.Contains Use_52w_High
                  MaxTightness   = parsed.GetResult(Max_Tightness,    defaultValue = defaultConfig.Entry.MaxTightness)
                  MaxAtrPct      = parsed.GetResult(Max_Atr_Pct,      defaultValue = defaultConfig.Entry.MaxAtrPct)
                  MinIntradayRet = parsed.GetResult(Min_Intraday_Ret, defaultValue = defaultConfig.Entry.MinIntradayRet)
                  MinMaxAtrLog   = parsed.GetResult(Min_Max_Atr_Log,  defaultValue = defaultConfig.Entry.MinMaxAtrLog) } }

    printfn "HighFlyerV2 backtest"
    printfn "  db        = %s" dbPath
    printfn "  range     = %O .. %O" startDate endDate
    printfn "  stop win (csv) = %d   vol window = %d   time-stop = %s"
        cfg.StopLowWindow cfg.AtrWindow
        (if cfg.MaxHoldBars > 0 then sprintf "%dd" cfg.MaxHoldBars else "off (MTM)")
    printfn "  entry basis = %s" (if cfg.UsePartialEntry then sprintf "%02d:%02d ET partial candle (%s)" (cutoffMin/60) (cutoffMin%60) cfg.PartialTable else "daily close (parity)")
    let rvolHi = if Double.IsInfinity cfg.Entry.RvolMax then "inf" else sprintf "%.0f" cfg.Entry.RvolMax
    printfn "  entry     = up[%.2f,%.2f) rvol[%.0f,%s] adv>=%.0f price>=%.0f 52w>=%.2f tight<%.2f atr%%<%.2f intraday>=%.2f maxlogatr>=%.3f"
        cfg.Entry.UpThreshold cfg.Entry.MaxUpThreshold cfg.Entry.RvolMin rvolHi cfg.Entry.MinAvgDollarVolume
        cfg.Entry.MinPrice cfg.Entry.Min52wPct cfg.Entry.MaxTightness cfg.Entry.MaxAtrPct cfg.Entry.MinIntradayRet cfg.Entry.MinMaxAtrLog

    let sw = Stopwatch.StartNew()
    let trips = run dbPath cfg startDate endDate
    sw.Stop()

    writeCsv outPath trips

    let wins = trips |> Array.filter (fun t -> t.NetPnL > 0.0)
    let losses = trips |> Array.filter (fun t -> t.NetPnL < 0.0)
    let sumW = wins |> Array.sumBy (fun t -> t.NetPnL)
    let sumL = losses |> Array.sumBy (fun t -> t.NetPnL)
    let pf = if sumL = 0.0 then nan else sumW / -sumL
    let netPnl = trips |> Array.sumBy (fun t -> t.NetPnL)

    printfn ""
    printfn "  trips     = %d  (%.1f s)" trips.Length sw.Elapsed.TotalSeconds
    printfn "  win rate  = %.1f%%  (%d / %d)"
        (100.0 * float wins.Length / float (max 1 trips.Length)) wins.Length trips.Length
    printfn "  net P&L   = %s" (netPnl.ToString("N0"))
    printfn "  PF        = %.3f" pf
    printfn "  wrote     = %s" outPath
    0
