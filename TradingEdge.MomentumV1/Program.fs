module TradingEdge.MomentumV1.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.MomentumV1.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultCsv = "/tmp/momentum_v1_trips.csv"

type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    | Stop_Low_Window of int
    | Trail_Window of int
    | Exit_Time_Cap of int

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Db_Path _ -> "Path to trading.db (DuckDB). Default: the shared data/trading.db."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd). Default 2005-01-01."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd). Default 2026-05-13 (data max)."
            | Out _ -> "Output trips CSV path. Default /tmp/momentum_v1_trips.csv."
            | Stop_Low_Window _ -> "Trailing-stop low window in bars. Default 4."
            | Trail_Window _ -> "Trailing-limit N-day-high window (the resting sell-limit reference). Default 1 (N=1)."
            | Exit_Time_Cap _ -> "Bars the sell limit may rest before exiting at the next open. Default 5. 0 = exit at next open immediately (N ignored)."

let private parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "momentum-v1")
    let parsed = parser.Parse argv

    let dbPath    = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let startDate = parseDate (parsed.GetResult(Start_Date, defaultValue = "2005-01-01"))
    let endDate   = parseDate (parsed.GetResult(End_Date,   defaultValue = "2026-05-13"))
    let outPath   = parsed.GetResult(Out, defaultValue = defaultCsv)

    let cfg =
        { defaultConfig with
            StopLowWindow = parsed.GetResult(Stop_Low_Window, defaultValue = defaultConfig.StopLowWindow)
            TrailWindow   = parsed.GetResult(Trail_Window,    defaultValue = defaultConfig.TrailWindow)
            ExitTimeCap   = parsed.GetResult(Exit_Time_Cap,   defaultValue = defaultConfig.ExitTimeCap) }

    printfn "MomentumV1 backtest"
    printfn "  db        = %s" dbPath
    printfn "  range     = %O .. %O" startDate endDate
    printfn "  stop win  = %d   trail N = %d   exit cap = %d   expansion = %.2f"
        cfg.StopLowWindow cfg.TrailWindow cfg.ExitTimeCap cfg.ExpansionThr
    printfn "  entry     = up>=%.2f rvol[%.0f,%.0f] adv>=%.0f price>=%.0f 52w>=%.2f tight<%.2f atr%%<%.2f"
        cfg.Entry.UpThreshold cfg.Entry.RvolMin cfg.Entry.RvolMax cfg.Entry.MinAvgDollarVolume
        cfg.Entry.MinPrice cfg.Entry.Min52wPct cfg.Entry.MaxTightness cfg.Entry.MaxAtrPct

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
