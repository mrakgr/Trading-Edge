module TradingEdge.LowFlyer.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.LowFlyer.Intraday
open TradingEdge.LowFlyer.Backtest

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
    | Min_Bar_Flush of float
    | Max_Intraday_Atr_Pct of float
    // kept-inert levers (off by default) for later sweeps
    | Short
    | Short_Breakdown
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
            | Min_Bar_Flush _ -> "Entry-bar FLUSH gate: require the breakout bar's 1m move (close/prevClose-1) <= this. Default 0.0 = OFF. e.g. -0.007 rejects bars softer than -0.7% (a real flush candle, not a one-tick poke)."
            | Max_Intraday_Atr_Pct _ -> "Intraday log-ATR CAP at entry: require the 1m log-ATR < this. Default +inf = OFF. e.g. 0.02 rejects names in genuine chaos (per Run 9)."
            | Short -> "Trade the MIRRORED setup: fade the new-session-HIGH pop on high volume SHORT (Downside=false, Short=true) — the true mirror of the default long-the-flush. Breakout ref, flush gate and 2-bar stop all flip. Default off (long the flush)."
            | Short_Breakdown -> "Trade the SAME new-session-LOW breakdown as the default (Downside=true) but SHORT it (Short=true) instead of long — momentum continuation of the flush. +EV when the name is extended (high 1d): the usual flush-fade INVERTS. Mutually exclusive with --short."
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
                  MinBarFlush   = parsed.GetResult(Min_Bar_Flush,   defaultValue = defaultConfig.Intraday.MinBarFlush)
                  MaxAtrPct     = parsed.GetResult(Max_Intraday_Atr_Pct, defaultValue = defaultConfig.Intraday.MaxAtrPct)
                  // Three exclusive modes:
                  //   default            — LONG the new-session-LOW flush (Downside=true, Short=false)
                  //   --short            — MIRROR: fade the new-session-HIGH pop (Downside=false, Short=true)
                  //   --short-breakdown  — SHORT the new-session-LOW breakdown (Downside=true, Short=true),
                  //                        momentum continuation; inverts +EV when the name is extended.
                  // (--short flips Downside to the high side; --short-breakdown keeps the low side.)
                  Downside      = not (parsed.Contains Short)
                  Short         = parsed.Contains Short || parsed.Contains Short_Breakdown
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
    printfn "  gates       = flush %s   log-ATR %s"
        (if cfg.Intraday.MinBarFlush = 0.0 then "off"
         elif cfg.Intraday.Downside then sprintf "<=%.3f" cfg.Intraday.MinBarFlush
         else sprintf ">=%.3f" (-cfg.Intraday.MinBarFlush))
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
