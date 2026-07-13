module TradingEdge.VwapReclaimV3.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.VwapReclaimV3.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/vwap_reclaim_v3_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    // intraday engine knobs (the strategy defaults are locked in Backtest.defaultConfig)
    | Entry_Start_Min of int
    | Entry_End_Min of int
    | Vol_Window of int
    | Max_Concurrent of int
    | Max_Intraday_Atr_Pct of float
    | Max_Intraday_Tightness of float
    | Time_Stop_Min of int
    // ----- SMB VWAP x 9-EMA reclaim knobs (long-only) -----
    | Ema_Period of int
    | Below_Vwap_Frac of float
    | Min_Run_Below_Vwap of int
    | Min_Tightness of float
    | Stop_Buffer of float

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files. Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2003-09-10 (data min)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (minute-data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/vwap_reclaim_v3_trips.csv."
            | Entry_Start_Min _ -> "Earliest ET minute an entry may fire (600 = 10:00 default). 09:30-EntryStart warms VWAP/EMA + the weakness run."
            | Entry_End_Min _ -> "LATEST ET minute an entry may fire (810 = 13:30 default morning window). 0 or >=MOC = all-day (no upper bound)."
            | Vol_Window _ -> "Intraday ATR/tightness lookback in 1m bars (default 20). Feeds the tightness gate + the recorded *_at_entry snapshots."
            | Max_Concurrent _ -> "Cap on concurrently-open positions per day (0 = unlimited, default)."
            | Max_Intraday_Atr_Pct _ -> "Intraday log-ATR CAP at entry: require the 1m log-ATR < this. Default +inf = OFF."
            | Max_Intraday_Tightness _ -> "Intraday tightness CAP at entry: require tightness < this. Default +inf = OFF."
            | Time_Stop_Min _ -> "SWEEP LEVER: flatten this many minutes after entry, capped at MOC (0 = off, default = hold to MOC / EMA stop)."
            | Ema_Period _ -> "VWAP-reclaim: the fast EMA period that must cross above VWAP (default 9). Also backs the pullback stop."
            | Below_Vwap_Frac _ -> "VWAP-reclaim: require the EMA below VWAP for > this fraction of the pre-cross session (0 = off, default). The consecutive-run gate is the weakness filter now."
            | Min_Run_Below_Vwap _ -> "VWAP-reclaim: require >= this many CONSECUTIVE bars EMA<VWAP right before the cross (default 11; 0 = off). The IMMEDIACY of the weakness."
            | Min_Tightness _ -> "VWAP-reclaim: MIN intraday tightness at entry (default 3.0): require a name with real range, not a dead-flat chop. 0 = off."
            | Stop_Buffer _ -> "9-EMA pullback stop BUFFER (fraction): the stop fires when the 9-EMA falls below run-min*(1-buffer). Default 0.0 = fire the instant the EMA dips under the run-low."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "vwap-reclaim-v3")
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
                  EntryStartMin = parsed.GetResult(Entry_Start_Min, defaultValue = defaultConfig.Intraday.EntryStartMin)
                  EntryEndMin   = parsed.GetResult(Entry_End_Min,   defaultValue = defaultConfig.Intraday.EntryEndMin)
                  VolWindow     = parsed.GetResult(Vol_Window,      defaultValue = defaultConfig.Intraday.VolWindow)
                  MaxConcurrent = parsed.GetResult(Max_Concurrent,  defaultValue = defaultConfig.Intraday.MaxConcurrent)
                  MaxAtrPct     = parsed.GetResult(Max_Intraday_Atr_Pct, defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  MaxTightness  = parsed.GetResult(Max_Intraday_Tightness, defaultValue = defaultConfig.Intraday.MaxTightness)
                  TimeStopMin   = parsed.GetResult(Time_Stop_Min,   defaultValue = defaultConfig.Intraday.TimeStopMin)
                  EmaPeriod      = parsed.GetResult(Ema_Period,       defaultValue = defaultConfig.Intraday.EmaPeriod)
                  BelowVwapFrac  = parsed.GetResult(Below_Vwap_Frac,  defaultValue = defaultConfig.Intraday.BelowVwapFrac)
                  MinRunBelowVwap = parsed.GetResult(Min_Run_Below_Vwap, defaultValue = defaultConfig.Intraday.MinRunBelowVwap)
                  MinTightness   = parsed.GetResult(Min_Tightness,   defaultValue = defaultConfig.Intraday.MinTightness)
                  StopBuffer     = parsed.GetResult(Stop_Buffer,     defaultValue = defaultConfig.Intraday.StopBuffer) } }

    printfn "VwapReclaimV3 backtest — SMB VWAP x %d-EMA reclaim (LONG only), 9-EMA pullback-low stop" cfg.Intraday.EmaPeriod
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  entry window= %02d:%02d–%02d:%02d ET   below-run >= %d bars   %s"
        (cfg.Intraday.EntryStartMin / 60) (cfg.Intraday.EntryStartMin % 60)
        (cfg.Intraday.EntryEndMin / 60) (cfg.Intraday.EntryEndMin % 60)
        cfg.Intraday.MinRunBelowVwap
        (if cfg.Intraday.TimeStopMin > 0 then sprintf "time-stop %dm" cfg.Intraday.TimeStopMin else "hold-to-MOC")
    printfn "  stop        = 9-EMA pullback low (run-min of the 9-EMA), buffer %.3f" cfg.Intraday.StopBuffer
    printfn "  gates       = tightness >= %.1f   below-frac %s"
        cfg.Intraday.MinTightness
        (if cfg.Intraday.BelowVwapFrac > 0.0 then sprintf "> %.2f" cfg.Intraday.BelowVwapFrac else "off")

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
