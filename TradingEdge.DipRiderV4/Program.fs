module TradingEdge.DipRiderV4.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.DipRiderV4.Intraday
open TradingEdge.DipRiderV4.Backtest

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
                  MinVolClimb     = parsed.GetResult(Min_Vol_Climb,     defaultValue = defaultConfig.Intraday.MinVolClimb)
                  MinAtrPct       = parsed.GetResult(Min_Intraday_Atr_Pct,   defaultValue = defaultConfig.Intraday.MinAtrPct)
                  MaxAtrPct       = parsed.GetResult(Max_Intraday_Atr_Pct,   defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  MinCloseAbove6  = parsed.GetResult(Min_Close_Above_6, defaultValue = defaultConfig.Intraday.MinCloseAbove6)
                  MinChg1d        = parsed.GetResult(Min_Chg_1d,        defaultValue = defaultConfig.Intraday.MinChg1d)
                  MinChg3d        = parsed.GetResult(Min_Chg_3d,        defaultValue = defaultConfig.Intraday.MinChg3d)
                  MinEmaVsVwap    = parsed.GetResult(Min_Ema_Vs_Vwap,   defaultValue = defaultConfig.Intraday.MinEmaVsVwap) } }

    let ic = cfg.Intraday
    let hhmm m = sprintf "%02d:%02d" (m / 60) (m % 60)
    printfn "DipRiderV4 backtest — pure trailing-window momentum, SMB-backside arm/re-arm (LONG intraday)"
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  anchors     = session from %s ET   features from %s ET (VWAP/OLS/ATR/EMA/sum6)"
        (hhmm ic.SessionStartMin) (hhmm ic.FeatureStartMin)
    printfn "  entry window= %s–%s ET" (hhmm ic.EntryStartMin) (hhmm ic.EntryEndMin)
    printfn "  price gate  = price-slope20 > 0   log-ATR20 >= %.3f%s   sum6 >= %d%s%s%s"
        ic.MinAtrPct
        (if Double.IsInfinity ic.MaxAtrPct then "" else sprintf " (< %.3f)" ic.MaxAtrPct)
        ic.MinCloseAbove6
        (if not (Double.IsNegativeInfinity ic.MinChg1d || Double.IsNaN ic.MinChg1d) then sprintf "   chg1d >= %.0f%%" (100.0*ic.MinChg1d) else "")
        (if not (Double.IsNegativeInfinity ic.MinChg3d || Double.IsNaN ic.MinChg3d) then sprintf "   chg3d >= %.0f%%" (100.0*ic.MinChg3d) else "")
        (if not (Double.IsNegativeInfinity ic.MinEmaVsVwap || Double.IsNaN ic.MinEmaVsVwap) then sprintf "   ema-vs-vwap >= %.0f%%" (100.0*ic.MinEmaVsVwap) else "")
    printfn "  vol check   = %s" (if ic.MinVolClimb > 0.0 then sprintf "vol-climb >= %.2f (take-or-disarm)" ic.MinVolClimb else "OFF (always take)")
    printfn "  stop        = ema: CURRENT 20m-min-9EMA (this-bar-inclusive), triggered by the live 9-EMA below it"
    printfn "  exits       = stop + hold-to-MOC"

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
