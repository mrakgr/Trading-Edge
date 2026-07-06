module TradingEdge.VwapReclaim.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.VwapReclaim.Intraday
open TradingEdge.VwapReclaim.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/lowflyer_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    // intraday engine knobs (the strategy defaults are locked in Backtest.defaultConfig)
    | Entry_Start_Min of int
    | Vol_Window of int
    | Max_Concurrent of int
    | Min_Low_Ref
    | No_Vol_High
    | Vol_High_Frac of float
    | Min_Bar_Flush of float
    | Min_Bar_Flush_Floor of float
    | Max_Intraday_Atr_Pct of float
    // kept-inert levers (off by default) for later sweeps
    // (the SHORT pop-fade + short-breakdown modes were forked out to TradingEdge.MaxFlyerV2;
    //  LowFlyer is now the LONG flush-fade book only.)
    | Pct_Stop of float
    | Time_Stop_Min of int

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files. Default: data/minute_aggs."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2003-09-10 (data min)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-06-25 (minute-data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/lowflyer_trips.csv."
            | Entry_Start_Min _ -> "Earliest ET minute an entry may fire (585 = 09:45 default). 09:30-EntryStart warms the running low/vol-high."
            | Vol_Window _ -> "Intraday ATR/tightness lookback in 1m bars (default 20). Both quality gates are OFF (+inf) by default, so this only affects the recorded *_at_entry snapshots."
            | Max_Concurrent _ -> "Cap on concurrently-open positions per day (0 = unlimited, default)."
            | Min_Low_Ref -> "Switch the breakout reference back to the running min-LOW channel. Default is min-CLOSE (wick-immune, closes-only; +~29%% trips at ~same PF — Run 12)."
            | No_Vol_High -> "DROP the volume-confirmation gate entirely (= --vol-high-frac 0): enter on the FIRST new-session-extreme bar regardless of its 1m volume (default requires the bar to make a new session 1m-volume high). The recorded `new_vol_high` column flags whether each entry bar DID make a strict new vol high."
            | Vol_High_Frac _ -> "RELAX the volume-confirmation gate to a FRACTION of the running session 1m-vol high: require bar volume >= frac * vol_high. Default 1.0 = must EXCEED it (original). e.g. 0.95 / 0.90 = within 5%%/10%% of the high (recovers trips near, but not over, the vol high). 0 = off (same as --no-vol-high)."
            | Min_Bar_Flush _ -> "Entry-bar FLUSH gate: require the breakout bar's 1m move (close/prevClose-1) <= this. Default 0.0 = OFF. e.g. -0.007 rejects bars softer than -0.7% (a real flush candle, not a one-tick poke)."
            | Min_Bar_Flush_Floor _ -> "Entry-bar flush-DEPTH floor (the falling-knife cut, Run 26): reject a flush DEEPER than this. Default 0.0 = OFF. e.g. -0.12 rejects flushes deeper than -12%%. Pairs with --min-bar-flush to band the entry move; PF 3.25->3.45 on the production long."
            | Max_Intraday_Atr_Pct _ -> "Intraday log-ATR CAP at entry: require the 1m log-ATR < this. Default +inf = OFF. e.g. 0.02 rejects names in genuine chaos (per Run 9)."
            | Pct_Stop _ -> "SWEEP LEVER: wide catastrophe %%-stop, a fixed adverse excursion from entry (0 = off, default)."
            | Time_Stop_Min _ -> "SWEEP LEVER: flatten this many minutes after entry, capped at MOC (0 = off, default = hold to MOC)."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "lowflyer")
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
                  VolWindow     = parsed.GetResult(Vol_Window,      defaultValue = defaultConfig.Intraday.VolWindow)
                  MaxConcurrent = parsed.GetResult(Max_Concurrent,  defaultValue = defaultConfig.Intraday.MaxConcurrent)
                  MinCloseRef   = not (parsed.Contains Min_Low_Ref)
                  VolHighFrac =
                      if parsed.Contains No_Vol_High then 0.0
                      else parsed.GetResult(Vol_High_Frac, defaultValue = defaultConfig.Intraday.VolHighFrac)
                  MinBarFlush   = parsed.GetResult(Min_Bar_Flush,   defaultValue = defaultConfig.Intraday.MinBarFlush)
                  MinBarFlushFloor = parsed.GetResult(Min_Bar_Flush_Floor, defaultValue = defaultConfig.Intraday.MinBarFlushFloor)
                  MaxAtrPct     = parsed.GetResult(Max_Intraday_Atr_Pct, defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  // LONG-only: the short pop-fade + short-breakdown forked to TradingEdge.MaxFlyerV2.
                  // Downside=true / Short=false come straight from defaultConfig (no override).
                  PctStop       = parsed.GetResult(Pct_Stop,        defaultValue = defaultConfig.Intraday.PctStop)
                  TimeStopMin   = parsed.GetResult(Time_Stop_Min,   defaultValue = defaultConfig.Intraday.TimeStopMin) } }

    printfn "LowFlyer backtest — market-wide intraday (%s)"
        (match cfg.Intraday.Downside, cfg.Intraday.Short with
         | true,  false -> "LONG the new-session-low flush (mean-reversion)"
         | false, true  -> "SHORT the new-session-HIGH pop (mean-reversion)"
         | true,  true  -> "SHORT the new-session-LOW breakdown (momentum continuation)"
         | false, false -> "LONG the new-session-high pop")
    printfn "  db          = %s" dbPath
    printfn "  minute_aggs = %s" minuteDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  entry from  = %02d:%02d ET   vol window = %d   side = %s   %s"
        (cfg.Intraday.EntryStartMin / 60) (cfg.Intraday.EntryStartMin % 60) cfg.Intraday.VolWindow
        (if cfg.Intraday.Short then "SHORT" else "long")
        (if cfg.Intraday.TimeStopMin > 0 then sprintf "time-stop %dm" cfg.Intraday.TimeStopMin
         elif cfg.Intraday.PctStop > 0.0 then sprintf "pct-stop %.0f%%" (cfg.Intraday.PctStop * 100.0)
         else "hold-to-MOC")
    printfn "  breakout    = %s (%s ref)"
        (if cfg.Intraday.Downside then "new session LOW" else "new session HIGH")
        (if cfg.Intraday.MinCloseRef then "CLOSE, wick-immune" else "low/high")
    printfn "  gates       = flush %s   flush-floor %s   log-ATR %s"
        (if cfg.Intraday.MinBarFlush = 0.0 then "off"
         elif cfg.Intraday.Downside then sprintf "<=%.3f" cfg.Intraday.MinBarFlush
         else sprintf ">=%.3f" (-cfg.Intraday.MinBarFlush))
        (if cfg.Intraday.MinBarFlushFloor = 0.0 then "off"
         elif cfg.Intraday.Downside then sprintf ">=%.3f" cfg.Intraday.MinBarFlushFloor
         else sprintf "<=%.3f" (-cfg.Intraday.MinBarFlushFloor))
        (if Double.IsInfinity cfg.Intraday.MaxAtrPct then "off" else sprintf "<%.3f" cfg.Intraday.MaxAtrPct)

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
