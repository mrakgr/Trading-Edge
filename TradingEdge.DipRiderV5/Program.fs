module TradingEdge.DipRiderV5.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.DipRiderV5.Intraday
open TradingEdge.DipRiderV5.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/diprider_v3_trips.csv"

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
    // ----- entry gates -----
    | Min_Vol_Climb of float
    | Min_Intraday_Atr_Pct of float
    | Max_Intraday_Atr_Pct of float
    | Min_Close_Above_6 of int
    | Min_Chg_1d of float
    | Min_Chg_3d of float
    | Min_Ema_Vs_Vwap of float
    | Min_Tightness of float
    | Max_Rvol_5m_15m of float
    | Min_Dv_0945 of float
    | Mean_Reversion
    | Mr_No_Vwap
    | Mr_Use_Stop
    | Vol_Stop_Frac of float
    | Vol_Stop_Use_Avg20
    | Rvol_Use_Max
    // ----- breakout mode -----
    | Max_Bars_Since_Breakout of int
    | Max_Bars_Since_20m_Breakout of int
    | Max_Bars_Since_60m_Breakout of int
    | Breakout_Or
    | Breakout_Vc_Session of float
    | Breakout_Vc_60m of float
    | Breakout_Vc_20m of float
    | No_Price_Slope
    | No_Sum6
    // ----- stop-distance floor -----
    | Min_Stop_Dist_Pct of float
    | Stop_Dist_As_Gate
    // ----- arm/re-arm state machine -----
    | Re_Arm of string
    | Max_Concurrent of int
    | Vol_As_Gate

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files. Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2003-09-10 (data min)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (minute-data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/diprider_v3_trips.csv."
            | Entry_Start_Min _ -> "Earliest ET minute an entry may fire (600 = 10:00 default)."
            | Entry_End_Min _ -> "LATEST ET minute an entry may fire (810 = 13:30 default). 0 or >=MOC = all-day."
            | Feature_Start_Min _ -> "ET minute the trailing features start folding (570 = 09:30 default, the RTH open)."
            | Vol_Window _ -> "Trailing ATR/OLS-slope lookback in 1m bars (default 20)."
            | Ema_Period _ -> "The EMA period for the closes-above-EMA count (default 9)."
            | Min_Vol_Climb _ -> "⭐ MAIN VOLUME CHECK (F32): require vol_climb = (volEma-volEmaMin)/volEma >= this. Default 0.5. 0 = off."
            | Min_Intraday_Atr_Pct _ -> "Log-ATR FLOOR at entry (THE MAIN LEVER, F3): require the 20m log-ATR >= this. Default 0.013. 0 = off."
            | Max_Intraday_Atr_Pct _ -> "Log-ATR CAP at entry: require the 20m log-ATR < this. Default +inf = OFF."
            | Min_Close_Above_6 _ -> "GATE: require >= N of the last 6 bars closed above the 9-EMA. Default 5."
            | Min_Chg_1d _ -> "DAY-DIRECTION FLOOR (F17): require the stock green on the day — reject if entry/prevClose-1 < this. Default 0.10. Large-negative = off."
            | Min_Chg_3d _ -> "3-DAY TREND FLOOR (F28): require the stock up over 3 days — reject if entry/close3d-1 < this. Default 0.0. Large-negative = off."
            | Min_Ema_Vs_Vwap _ -> "9-EMA-vs-VWAP FLOOR (F27): reject if the 9-EMA is more than |this| below VWAP (ema/vwap-1 < this). Default -0.02. Large-negative = off."
            | Min_Tightness _ -> "TIGHTNESS FLOOR: require (rangeHigh-rangeLow)/atrLin >= this (a real range, not lethargic). Default 3.0. 0 = off."
            | Max_Rvol_5m_15m _ -> "EXHAUSTION CUT (LIVE-SAFE V5 rebuild): reject if the trailing-5m vol numerator >= this × permin15m (D's OWN 09:30-09:45 mean 1m volume). V4's --max-rvol-5m-20d divided by avgvol20/390, a LOOKAHEAD (avgvol20 includes D's own session volume). Different scale, so V4's 100 does NOT carry over. Default 0 = OFF; re-tune from scratch."
            | Mean_Reversion -> "⭐ MEAN-REVERSION MODE: invert the system. ENTRY = close makes a new 20m LOW of 1m-bar CLOSES **and** close > VWAP. EXIT = close reaches the 20m HIGH of closes (target), or MOC. Bypasses the momentum arm/re-arm, breakout timers, price-slope, sum6 and the stop-distance floor (all of which want new HIGHS)."
            | Mr_No_Vwap -> "MR mode ABLATION: drop the above-VWAP entry condition (buy EVERY 20m low regardless of where price sits vs VWAP). Control for whether the VWAP filter is load-bearing."
            | Mr_Use_Stop -> "MR mode: ALSO apply the 9-EMA stop. Default OFF — the stop arms off the 20m-EMA-LOW, which is what MR buys into, so it fires at entry. For testing only."
            | Vol_Stop_Frac _ -> "⭐ SCALP EXIT: while holding, exit at the bar close once the live volume MA falls below this fraction of the volume MA AS OF ENTRY (0.667 = ⅔). Ported from BreakoutTimer F14. 0 (default) = OFF = hold to MOC. Lookahead-free (D's own bars only)."
            | Vol_Stop_Use_Avg20 -> "Vol-stop BASIS = the 20m AvgMa of raw volume (smoother) instead of the default 9-EMA of volume (faster). Both are warmup-safe averages."
            | Min_Dv_0945 _ -> "IN-PLAY UNIVERSE FLOOR: minimum 09:30-09:45 dollar volume (dv_0945 = vol_0945 * avgprice_0945 * adj_ratio). REPLACES V4's leaked `avgvol20 * day_close >= $30M` ADV floor. Default 5000000 ($5M). 0 = no floor."
            | Rvol_Use_Max -> "Exhaustion-cut numerator = trailing-5m MAX 1m-vol (the short book's spiky signal) instead of the 5m AVG. Since max>=avg, cuts MORE at the same threshold."
            | Max_Bars_Since_Breakout _ -> "BREAKOUT GATE: require 0 <= bars-since-initial-breakout < this (the 9-EMA broke to a new session high within the last N bars, reset by the 20m-low re-arm). 0 = off. BreakoutTimer used 10."
            | Max_Bars_Since_20m_Breakout _ -> "20m-EMA-BREAKOUT GATE: require 0 <= bars-since-20m-EMA-breakout < this (the 9-EMA broke above its trailing-20m max within the last N bars, reset by the 20m-low re-arm). 0 = off. Sweep [1,10]."
            | Max_Bars_Since_60m_Breakout _ -> "60m-EMA-BREAKOUT GATE: require 0 <= bars-since-60m-EMA-breakout < this (the 9-EMA broke above its trailing-60m max within the last N bars, reset by the 20m-low re-arm). 0 = off."
            | Breakout_Or -> "OR the enabled breakout gates (session/20m/60m) instead of ANDing them: pass if ANY enabled window is within its countdown. Default AND."
            | Breakout_Vc_Session _ -> "OR-mode (F14): the session-high branch also requires vol_climb >= this. Default 0. Set global --min-vol-climb 0 to avoid double-gating."
            | Breakout_Vc_60m _ -> "OR-mode (F14): the 60m-high branch also requires vol_climb >= this. Default 0."
            | Breakout_Vc_20m _ -> "OR-mode (F14): the 20m-high branch also requires vol_climb >= this. Default 0."
            | No_Price_Slope -> "Drop the price-slope>0 gate (BreakoutTimer didn't use it)."
            | No_Sum6 -> "Drop the sum6 gate (BreakoutTimer didn't use it)."
            | Min_Stop_Dist_Pct _ -> "STOP-DISTANCE FLOOR (F17): require the entry to sit >= this fraction above its 20m-EMA-low (the frozen stop is >= this below entry). 0 = OFF (default). Knee ~0.03: larger stops = stronger established moves."
            | Stop_Dist_As_Gate -> "Apply the stop-distance floor as a GATE (a too-tight setup does NOT fire and does NOT disarm — it waits for the distance to widen). Default OFF = SKIP (the setup fires + disarms but opens no position — passes on the trade)."
            | Re_Arm _ -> "RE-ARM reference level: rolling-ema-low (default) | session-ema-low | stop-level. The live 9-EMA must drop below this to re-arm a consumed setup."
            | Max_Concurrent _ -> "Cap on concurrently-OPEN positions. 0 = unlimited (default; the pure arm/re-arm book). 1 = block entry+re-arm while a position is Holding (V3Backside slot-lifetime discipline)."
            | Vol_As_Gate -> "GATE mode: AND vol_climb into the price trigger (a vol-fail neither opens NOR disarms). Default OFF = SKIP mode (vol decides real-vs-skip; a vol-fail still disarms)."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "diprider-v3")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let minuteDir = parsed.GetResult(Minute_Dir, defaultValue = defaultMinuteDir)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2003-09-10"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-06-25"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let reArm =
        match parsed.GetResult(Re_Arm, defaultValue = "rolling-ema-low") with
        | "rolling-ema-low" -> RollingEmaLow
        | "session-ema-low" -> SessionEmaLow
        | "stop-level"      -> LastStopLevel
        | bad -> failwithf "Invalid --re-arm %A (rolling-ema-low | session-ema-low | stop-level)" bad

    let cfg =
        { defaultConfig with
            Intraday =
              { defaultConfig.Intraday with
                  ReArm           = reArm
                  MaxConcurrent   = parsed.GetResult(Max_Concurrent, defaultValue = defaultConfig.Intraday.MaxConcurrent)
                  VolAsGate       = defaultConfig.Intraday.VolAsGate || parsed.Contains Vol_As_Gate
                  EntryStartMin   = parsed.GetResult(Entry_Start_Min,   defaultValue = defaultConfig.Intraday.EntryStartMin)
                  EntryEndMin     = parsed.GetResult(Entry_End_Min,     defaultValue = defaultConfig.Intraday.EntryEndMin)
                  FeatureStartMin = parsed.GetResult(Feature_Start_Min, defaultValue = defaultConfig.Intraday.FeatureStartMin)
                  VolWindow       = parsed.GetResult(Vol_Window,        defaultValue = defaultConfig.Intraday.VolWindow)
                  EmaPeriod       = parsed.GetResult(Ema_Period,        defaultValue = defaultConfig.Intraday.EmaPeriod)
                  MinVolClimb     = parsed.GetResult(Min_Vol_Climb,     defaultValue = defaultConfig.Intraday.MinVolClimb)
                  MinAtrPct       = parsed.GetResult(Min_Intraday_Atr_Pct,   defaultValue = defaultConfig.Intraday.MinAtrPct)
                  MaxAtrPct       = parsed.GetResult(Max_Intraday_Atr_Pct,   defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  MinCloseAbove6  = parsed.GetResult(Min_Close_Above_6, defaultValue = defaultConfig.Intraday.MinCloseAbove6)
                  MinChg1d        = parsed.GetResult(Min_Chg_1d,        defaultValue = defaultConfig.Intraday.MinChg1d)
                  MinChg3d        = parsed.GetResult(Min_Chg_3d,        defaultValue = defaultConfig.Intraday.MinChg3d)
                  MinEmaVsVwap    = parsed.GetResult(Min_Ema_Vs_Vwap,   defaultValue = defaultConfig.Intraday.MinEmaVsVwap)
                  MinTightness    = parsed.GetResult(Min_Tightness,     defaultValue = defaultConfig.Intraday.MinTightness)
                  MaxRvol5m15m    = parsed.GetResult(Max_Rvol_5m_15m,   defaultValue = defaultConfig.Intraday.MaxRvol5m15m)
                  MeanReversion   = defaultConfig.Intraday.MeanReversion || parsed.Contains Mean_Reversion
                  MeanReversionNoVwap  = defaultConfig.Intraday.MeanReversionNoVwap || parsed.Contains Mr_No_Vwap
                  MeanReversionUseStop = defaultConfig.Intraday.MeanReversionUseStop || parsed.Contains Mr_Use_Stop
                  Rvol5mUseMax    = parsed.Contains Rvol_Use_Max
                  VolStopFrac     = parsed.GetResult(Vol_Stop_Frac, defaultValue = defaultConfig.Intraday.VolStopFrac)
                  VolStopUseAvg20 = defaultConfig.Intraday.VolStopUseAvg20 || parsed.Contains Vol_Stop_Use_Avg20
                  MaxBarsSinceBreakout = parsed.GetResult(Max_Bars_Since_Breakout, defaultValue = defaultConfig.Intraday.MaxBarsSinceBreakout)
                  MaxBarsSince20mBreakout = parsed.GetResult(Max_Bars_Since_20m_Breakout, defaultValue = defaultConfig.Intraday.MaxBarsSince20mBreakout)
                  MaxBarsSince60mBreakout = parsed.GetResult(Max_Bars_Since_60m_Breakout, defaultValue = defaultConfig.Intraday.MaxBarsSince60mBreakout)
                  BreakoutOr = defaultConfig.Intraday.BreakoutOr || parsed.Contains Breakout_Or
                  BreakoutVcSession = parsed.GetResult(Breakout_Vc_Session, defaultValue = defaultConfig.Intraday.BreakoutVcSession)
                  BreakoutVc60m = parsed.GetResult(Breakout_Vc_60m, defaultValue = defaultConfig.Intraday.BreakoutVc60m)
                  BreakoutVc20m = parsed.GetResult(Breakout_Vc_20m, defaultValue = defaultConfig.Intraday.BreakoutVc20m)
                  DisablePriceSlope = defaultConfig.Intraday.DisablePriceSlope || parsed.Contains No_Price_Slope
                  DisableSum6     = defaultConfig.Intraday.DisableSum6 || parsed.Contains No_Sum6
                  MinStopDistPct  = parsed.GetResult(Min_Stop_Dist_Pct, defaultValue = defaultConfig.Intraday.MinStopDistPct)
                  StopDistAsGate  = defaultConfig.Intraday.StopDistAsGate || parsed.Contains Stop_Dist_As_Gate }
            MinDv0945 = parsed.GetResult(Min_Dv_0945, defaultValue = defaultConfig.MinDv0945) }

    let ic = cfg.Intraday
    let hhmm m = sprintf "%02d:%02d" (m / 60) (m % 60)
    printfn "DipRiderV5 backtest — LIVE-SAFE fork of V4 (no ADV lookahead, no contaminated exhaustion cut)"
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  universe    = dv_0945 (09:30-09:45 $vol) >= $%.1fM  AND  rvol_0945_honest >= 1  [LIVE-SAFE]" (cfg.MinDv0945 / 1e6)
    printfn "  exit        = %s" (if ic.VolStopFrac > 0.0 then sprintf "SCALP: vol-stop @ %.0f%% x entry-basis (%s) | 9EMA stop | MOC" (100.0*ic.VolStopFrac) (if ic.VolStopUseAvg20 then "20m-avg" else "vol-9EMA") else "hold-to-MOC | 9EMA stop")
    printfn "  anchors     = session from %s ET   features from %s ET (VWAP/OLS/ATR/EMA/sum6)"
        (hhmm ic.SessionStartMin) (hhmm ic.FeatureStartMin)
    printfn "  entry window= %s–%s ET" (hhmm ic.EntryStartMin) (hhmm ic.EntryEndMin)
    printfn "  price gate  = %s   log-ATR20 >= %.3f%s   %s%s%s%s"
        (if ic.DisablePriceSlope then "price-slope OFF" else "price-slope20 > 0")
        ic.MinAtrPct
        (if Double.IsInfinity ic.MaxAtrPct then "" else sprintf " (< %.3f)" ic.MaxAtrPct)
        (if ic.DisableSum6 then "sum6 OFF" else sprintf "sum6 >= %d" ic.MinCloseAbove6)
        (if not (Double.IsNegativeInfinity ic.MinChg1d || Double.IsNaN ic.MinChg1d) then sprintf "   chg1d >= %.0f%%" (100.0*ic.MinChg1d) else "")
        (if not (Double.IsNegativeInfinity ic.MinChg3d || Double.IsNaN ic.MinChg3d) then sprintf "   chg3d >= %.0f%%" (100.0*ic.MinChg3d) else "")
        (if not (Double.IsNegativeInfinity ic.MinEmaVsVwap || Double.IsNaN ic.MinEmaVsVwap) then sprintf "   ema-vs-vwap >= %.0f%%" (100.0*ic.MinEmaVsVwap) else "")
    if ic.MaxBarsSinceBreakout > 0 then
        printfn "  breakout    = 0 <= bars-since-9EMA-session-high < %d (reset by the 20m-low re-arm)" ic.MaxBarsSinceBreakout
    if ic.MaxBarsSince20mBreakout > 0 then
        printfn "  20m-breakout= 0 <= bars-since-9EMA-20m-high < %d (reset by the 20m-low re-arm)" ic.MaxBarsSince20mBreakout
    if ic.MaxBarsSince60mBreakout > 0 then
        printfn "  60m-breakout= 0 <= bars-since-9EMA-60m-high < %d (reset by the 20m-low re-arm)" ic.MaxBarsSince60mBreakout
    if ic.BreakoutOr then
        printfn "  breakout-cmb= OR (pass if ANY enabled window within countdown & its vc floor: sess>=%.2f 60m>=%.2f 20m>=%.2f)"
            ic.BreakoutVcSession ic.BreakoutVc60m ic.BreakoutVc20m
    printfn "  extra gates = %s%s"
        (if ic.MinTightness > 0.0 then sprintf "tightness >= %.1f" ic.MinTightness else "tightness OFF")
        (if ic.MaxRvol5m15m > 0.0 then sprintf "   rvol5m15m < %.0f (%s, LIVE-SAFE)" ic.MaxRvol5m15m (if ic.Rvol5mUseMax then "5m-MAX" else "5m-avg") else "   exhaust-cut OFF")
    let volMode = if ic.VolAsGate then "GATE (ANDed into the trigger)" else "SKIP (real-vs-skip; still disarms)"
    printfn "  vol check   = %s   mode = %s"
        (if ic.MinVolClimb > 0.0 then sprintf "vol-climb >= %.2f" ic.MinVolClimb else "OFF") volMode
    let reArmDesc =
        match ic.ReArm with
        | RollingEmaLow -> "rolling 20m-min-9EMA"
        | SessionEmaLow -> "session-min 9EMA"
        | LastStopLevel -> "last consumed setup's stop level"
    printfn "  re-arm      = live 9-EMA drops below the %s   max-concurrent %s"
        reArmDesc (if ic.MaxConcurrent <= 0 then "unlimited" else string ic.MaxConcurrent)
    printfn "  stop        = ema: CURRENT 20m-min-9EMA (this-bar-inclusive), triggered by the live 9-EMA below it"
    if ic.MinStopDistPct > 0.0 then
        printfn "  stop-dist   = require entry >= %.1f%% above the 20m-EMA-low   (%s)"
            (100.0 * ic.MinStopDistPct) (if ic.StopDistAsGate then "GATE: too-tight waits, re-fires" else "SKIP: disarm, no position")
    printfn "  exits       = %s" (if ic.VolStopFrac > 0.0 then sprintf "stop + VOL-STOP(%.0f%%x entry %s) + MOC" (100.0*ic.VolStopFrac) (if ic.VolStopUseAvg20 then "20m-avg" else "vol-9EMA") else "stop + hold-to-MOC")

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
