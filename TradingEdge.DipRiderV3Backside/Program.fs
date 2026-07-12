module TradingEdge.DipRiderV3Backside.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.DipRiderV3Backside.Intraday
open TradingEdge.DipRiderV3Backside.Backtest

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
    | Max_Concurrent of int
    // ----- entry gates -----
    | Min_Vol_Slope of float
    | Max_Vol_Slope of float
    | Min_Vol_Climb of float
    | Min_Price_Slope of float
    | Min_Tightness of float
    | Max_Intraday_Tightness of float
    | Min_Intraday_Atr_Pct of float
    | Max_Intraday_Atr_Pct of float
    | Min_Close_Above_6 of int
    | Min_Slope_Per_Atr of float
    | Min_Sess_Max_Log_Atr of float
    | Max_Rvol_5m_20d of float
    | Min_Entry_Vs_Vwap of float
    | Min_Chg_1d of float
    | Min_Chg_3d of float
    | Require_Ema_Above_Vwap
    | Min_Ema_Vs_Vwap of float
    | Max_Sum_Above_40 of int
    | Max_Sum_Above_60 of int
    // ----- stop / exits -----
    | No_Geom_Stop
    | Stop_Floor_20m
    | Stop_Dist_Frac of float
    | Wick_Stop
    | Pct_Stop of float
    | Time_Stop_Min of int
    | Exhaust_Exit
    | Exhaust_Vol_Mult of float
    | Vwap_Exit_Bars of int

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
            | Feature_Start_Min _ -> "ET minute the trailing features start folding (570 = 09:30 default, the RTH open). Session extremes still fold from SessionStartMin (08:30)."
            | Vol_Window _ -> "Trailing ATR/tightness/OLS-slope lookback in 1m bars (default 20)."
            | Ema_Period _ -> "The EMA period for the closes-above-EMA count (default 9)."
            | Max_Concurrent _ -> "Cap on concurrently-open positions per day (default 1; 0 = unlimited)."
            | Min_Vol_Slope _ -> "LEGACY GATE (OFF by default, superseded by --min-vol-climb): 20m OLS log-volume slope >= this."
            | Min_Vol_Climb _ -> "⭐ MAIN VOLUME GATE (F32): require vol_climb = (volEma-volEmaMin)/volEma >= this. Default 0.5."
            | Max_Vol_Slope _ -> "BLOW-OFF CEILING (F16): reject the 20m OLS log-volume slope >= this (a volume explosion into entry). Default 0.25. inf = off."
            | Min_Price_Slope _ -> "GATE: require the 20m OLS log-price slope > this. Default 0.0 (sweep for a higher floor)."
            | Min_Tightness _ -> "GATE: require tightness >= this (real range, not lethargic). Default 3.0. 0 = off."
            | Max_Intraday_Tightness _ -> "Tightness CAP at entry: require tightness < this. Default +inf = OFF."
            | Min_Intraday_Atr_Pct _ -> "Log-ATR FLOOR at entry (THE MAIN LEVER, F3): require the 20m log-ATR >= this. Default 0.013. 0 = off."
            | Max_Intraday_Atr_Pct _ -> "Log-ATR CAP at entry: require the 20m log-ATR < this. Default +inf = OFF."
            | Min_Close_Above_6 _ -> "GATE: require >= N of the last 6 bars closed above the 9-EMA. Default 0 = OFF (start disabled; tune later)."
            | Min_Slope_Per_Atr _ -> "GATE: require the (price-slope / log-ATR) ratio >= this. Default OFF (gated after breakdown)."
            | Min_Sess_Max_Log_Atr _ -> "GATE: require the session-max 20m log-ATR >= this (a name that HAS had a volatility explosion). Default 0 = off."
            | Max_Rvol_5m_20d _ -> "EXHAUSTION CUT (F11): reject if the trailing-5m avg volume >= this × the 20d per-minute pace (a blow-off = late entry). Default 100. 0 = off."
            | Min_Entry_Vs_Vwap _ -> "VWAP-LOCATION FLOOR (F14): reject entries more than |this| below the session VWAP (entry/vwap-1 < this = a sold-off falling knife). Default -0.03. Large-negative = off."
            | Min_Chg_1d _ -> "DAY-DIRECTION FLOOR (F17): require the stock green on the day — reject if entry/prevClose-1 < this. Default 0.0 (must be >= prev close). Large-negative = off."
            | Min_Chg_3d _ -> "3-DAY TREND FLOOR (F28): require the stock up over 3 days — reject if entry/close3d-1 < this. Default 0.0. Large-negative = off."
            | Require_Ema_Above_Vwap -> "ABOVE-VWAP ENTRY GATE (F21): require the 9-EMA STRICTLY above the session VWAP (superseded by --min-ema-vs-vwap). Default off."
            | Min_Ema_Vs_Vwap _ -> "9-EMA-vs-VWAP FLOOR (F27, replaces --min-entry-vs-vwap): reject if the 9-EMA is more than |this| below VWAP (ema/vwap-1 < this). Default -0.02. Large-negative = off."
            | Max_Sum_Above_40 _ -> "CAP: reject if >= N of the last 40 bars were above the 9-EMA (trend went on too long). Default 0 = off."
            | Max_Sum_Above_60 _ -> "CAP: reject if >= N of the last 60 bars were above the 9-EMA. Default 0 = off."
            | No_Geom_Stop -> "Disable the geometry stop (hold stopless to MOC + optional --pct-stop/--time-stop)."
            | Stop_Floor_20m -> "Use the 20m-min-close as the geometry-stop floor instead of the default SESSION-min-close (a tighter floor; F2: the session floor wins on win-rate & PF)."
            | Stop_Dist_Frac _ -> "Geometry-stop distance as a fraction of d = (entry - 20m-min-close). Default 0.667 (=d*2/3)."
            | Wick_Stop -> "Revert to the WICK stop (bar.low <= level) instead of the default CLOSE-based stop."
            | Pct_Stop _ -> "Wide catastrophe %-stop: bar.low <= entry*(1-x). Default 0 = off."
            | Time_Stop_Min _ -> "Flatten this many minutes after entry, capped at MOC. Default 0 = off (hold-to-MOC)."
            | Exhaust_Exit -> "EXHAUSTION EXIT: while holding, sell into a NEW-SESSION-HIGH bar on a VOLUME BLOW-OFF (>= mult × both per-minute baselines). Default OFF."
            | Exhaust_Vol_Mult _ -> "Exhaustion blow-off multiplier (default 10). Only with --exhaust-exit."
            | Vwap_Exit_Bars _ -> "LOSS-OF-VWAP EXIT: once the 9-EMA has been above VWAP for >= this many bars, close the long when the 9-EMA crosses below VWAP. 0 = off (default)."

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

    let cfg =
        { defaultConfig with
            Intraday =
              { defaultConfig.Intraday with
                  EntryStartMin   = parsed.GetResult(Entry_Start_Min,   defaultValue = defaultConfig.Intraday.EntryStartMin)
                  EntryEndMin     = parsed.GetResult(Entry_End_Min,     defaultValue = defaultConfig.Intraday.EntryEndMin)
                  FeatureStartMin = parsed.GetResult(Feature_Start_Min, defaultValue = defaultConfig.Intraday.FeatureStartMin)
                  VolWindow       = parsed.GetResult(Vol_Window,        defaultValue = defaultConfig.Intraday.VolWindow)
                  EmaPeriod       = parsed.GetResult(Ema_Period,        defaultValue = defaultConfig.Intraday.EmaPeriod)
                  MaxConcurrent   = parsed.GetResult(Max_Concurrent,    defaultValue = defaultConfig.Intraday.MaxConcurrent)
                  MinVolSlope     = parsed.GetResult(Min_Vol_Slope,     defaultValue = defaultConfig.Intraday.MinVolSlope)
                  MinVolClimb     = parsed.GetResult(Min_Vol_Climb,     defaultValue = defaultConfig.Intraday.MinVolClimb)
                  MaxVolSlope     = parsed.GetResult(Max_Vol_Slope,     defaultValue = defaultConfig.Intraday.MaxVolSlope)
                  MinPriceSlope   = parsed.GetResult(Min_Price_Slope,   defaultValue = defaultConfig.Intraday.MinPriceSlope)
                  MinTightness    = parsed.GetResult(Min_Tightness,     defaultValue = defaultConfig.Intraday.MinTightness)
                  MaxTightness    = parsed.GetResult(Max_Intraday_Tightness, defaultValue = defaultConfig.Intraday.MaxTightness)
                  MinAtrPct       = parsed.GetResult(Min_Intraday_Atr_Pct,   defaultValue = defaultConfig.Intraday.MinAtrPct)
                  MaxAtrPct       = parsed.GetResult(Max_Intraday_Atr_Pct,   defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  MinCloseAbove6  = parsed.GetResult(Min_Close_Above_6, defaultValue = defaultConfig.Intraday.MinCloseAbove6)
                  MinSlopePerAtr  = parsed.GetResult(Min_Slope_Per_Atr, defaultValue = defaultConfig.Intraday.MinSlopePerAtr)
                  MinSessMaxLogAtr = parsed.GetResult(Min_Sess_Max_Log_Atr, defaultValue = defaultConfig.Intraday.MinSessMaxLogAtr)
                  MaxRvol5m20d    = parsed.GetResult(Max_Rvol_5m_20d,   defaultValue = defaultConfig.Intraday.MaxRvol5m20d)
                  MinEntryVsVwap  = parsed.GetResult(Min_Entry_Vs_Vwap, defaultValue = defaultConfig.Intraday.MinEntryVsVwap)
                  MinChg1d        = parsed.GetResult(Min_Chg_1d,        defaultValue = defaultConfig.Intraday.MinChg1d)
                  MinChg3d        = parsed.GetResult(Min_Chg_3d,        defaultValue = defaultConfig.Intraday.MinChg3d)
                  RequireEmaAboveVwap = parsed.Contains Require_Ema_Above_Vwap || defaultConfig.Intraday.RequireEmaAboveVwap
                  MinEmaVsVwap    = parsed.GetResult(Min_Ema_Vs_Vwap,   defaultValue = defaultConfig.Intraday.MinEmaVsVwap)
                  MaxSumAbove40   = parsed.GetResult(Max_Sum_Above_40,  defaultValue = defaultConfig.Intraday.MaxSumAbove40)
                  MaxSumAbove60   = parsed.GetResult(Max_Sum_Above_60,  defaultValue = defaultConfig.Intraday.MaxSumAbove60)
                  GeomStop        = not (parsed.Contains No_Geom_Stop)
                  StopFloorSessMin = not (parsed.Contains Stop_Floor_20m)   // default ON (F2); opt out to the 20m floor
                  StopDistFrac    = parsed.GetResult(Stop_Dist_Frac,    defaultValue = defaultConfig.Intraday.StopDistFrac)
                  StopOnClose     = not (parsed.Contains Wick_Stop)
                  PctStop         = parsed.GetResult(Pct_Stop,          defaultValue = defaultConfig.Intraday.PctStop)
                  TimeStopMin     = parsed.GetResult(Time_Stop_Min,     defaultValue = defaultConfig.Intraday.TimeStopMin)
                  ExhaustExit     = parsed.Contains Exhaust_Exit
                  ExhaustVolMult  = parsed.GetResult(Exhaust_Vol_Mult,  defaultValue = defaultConfig.Intraday.ExhaustVolMult)
                  VwapExitBars    = parsed.GetResult(Vwap_Exit_Bars,    defaultValue = defaultConfig.Intraday.VwapExitBars) } }

    let ic = cfg.Intraday
    let hhmm m = sprintf "%02d:%02d" (m / 60) (m % 60)
    let onOff b = if b then "ON" else "OFF"
    printfn "DipRiderV3 backtest — pure trailing-window momentum (LONG intraday)"
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  anchors     = session extremes from %s ET   features from %s ET (VWAP/OLS/ATR/tightness/EMA/init-vol)"
        (hhmm ic.SessionStartMin) (hhmm ic.FeatureStartMin)
    printfn "  entry window= %s–%s ET   max-concurrent %d" (hhmm ic.EntryStartMin) (hhmm ic.EntryEndMin) ic.MaxConcurrent
    printfn "  gates       = log-ATR20 >= %.3f   vol-slope20 >= %.3f   price-slope20 > %.3f   tightness20 >= %.1f%s%s%s"
        ic.MinAtrPct ic.MinVolSlope ic.MinPriceSlope ic.MinTightness
        (if ic.MinVolClimb > 0.0 then sprintf "   vol-climb >= %.2f" ic.MinVolClimb else "")
        (if ic.MinCloseAbove6 > 0 then sprintf "   sum-above-6 >= %d" ic.MinCloseAbove6 else "")
        (if not (Double.IsNegativeInfinity ic.MinSlopePerAtr) then sprintf "   slope/atr >= %.2f" ic.MinSlopePerAtr else "")
    let caps =
        [ if not (Double.IsInfinity ic.MaxVolSlope) then yield sprintf "vol-slope < %.2f" ic.MaxVolSlope
          if ic.MaxRvol5m20d > 0.0 then yield sprintf "rvol5m20d < %.0f" ic.MaxRvol5m20d
          if not (Double.IsNegativeInfinity ic.MinEntryVsVwap || Double.IsNaN ic.MinEntryVsVwap) then yield sprintf "entry-vs-vwap >= %.0f%%" (100.0*ic.MinEntryVsVwap)
          if not (Double.IsNegativeInfinity ic.MinEmaVsVwap || Double.IsNaN ic.MinEmaVsVwap) then yield sprintf "ema-vs-vwap >= %.0f%%" (100.0*ic.MinEmaVsVwap)
          if ic.RequireEmaAboveVwap then yield "9ema>vwap"
          if not (Double.IsNegativeInfinity ic.MinChg1d || Double.IsNaN ic.MinChg1d) then yield sprintf "chg1d >= %.0f%%" (100.0*ic.MinChg1d)
          if not (Double.IsNegativeInfinity ic.MinChg3d || Double.IsNaN ic.MinChg3d) then yield sprintf "chg3d >= %.0f%%" (100.0*ic.MinChg3d)
          if ic.MaxSumAbove40 > 0 then yield sprintf "sum40 < %d" ic.MaxSumAbove40
          if ic.MaxSumAbove60 > 0 then yield sprintf "sum60 < %d" ic.MaxSumAbove60
          if ic.MinSessMaxLogAtr > 0.0 then yield sprintf "sess-max-logATR >= %.3f" ic.MinSessMaxLogAtr
          if not (Double.IsInfinity ic.MaxTightness) then yield sprintf "tight < %.1f" ic.MaxTightness
          if not (Double.IsInfinity ic.MaxAtrPct) then yield sprintf "logATR < %.3f" ic.MaxAtrPct ]
    if not (List.isEmpty caps) then printfn "  caps        = %s" (String.concat "   " caps)
    let stopDesc =
        if ic.GeomStop then
            sprintf "geom: d = entry - %s; stop = entry - d*%.3f (%s)"
                (if ic.StopFloorSessMin then "sess-min-close" else "20m-min-close") ic.StopDistFrac
                (if ic.StopOnClose then "close-based" else "wick")
        else "stopless"
    let exits =
        [ yield "hold-to-MOC"
          if ic.TimeStopMin > 0 then yield sprintf "time-stop %dm" ic.TimeStopMin
          if ic.PctStop > 0.0 then yield sprintf "pct-stop %.0f%%" (100.0 * ic.PctStop)
          if ic.ExhaustExit then yield sprintf "exhaust(%.0f×)" ic.ExhaustVolMult
          if ic.VwapExitBars > 0 then yield sprintf "loss-of-VWAP(%d)" ic.VwapExitBars ]
    printfn "  stop        = %s" stopDesc
    printfn "  exits       = %s" (String.concat " + " exits)

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
