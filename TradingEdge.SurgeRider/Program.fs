module TradingEdge.SurgeRider.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.SurgeRider.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultSecDir = "/home/mrakgr/Trading-Edge/data/intraday_1s_slim"
let private defaultOutDir = "/tmp/surgerider_trips"

/// SurgeRider CLI. Deliberately TINY (the DipRiderV6 discipline): this is a
/// SAMPLER, so the study happens in SQL over the emitted trip PARQUET, not by
/// re-running with different flags. If you find yourself wanting a new GATE
/// flag here, add a recorded FEATURE instead and slice it post-hoc.
type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-s")>] Sec_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out_Dir of string
    // ----- the system (all of it) -----
    | Entry_Channel_Bars of int
    | Exit_Channel_Bars of int
    | Exit_Z_Bars of int
    | Ezv of float
    | Ezt of float
    // ----- per-bar liquidity floors -----
    | Dv_Floor_60 of float
    | Tc_Floor_60 of float
    // ----- universe -----
    | Min_Dv_0945 of float
    // ----- sampler vs book -----
    | Max_Concurrent of int
    // ----- timing -----
    | Entry_Start_Sec of int
    | Entry_End_Sec of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db_Path _ -> "DuckDB database path."
            | Sec_Dir _ -> "Directory of 1-second slim parquet files (data/intraday_1s_slim)."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd)."
            | Out_Dir _ -> "Output DIRECTORY for the trip parquet part files (trips_pNNN.parquet). Post-hoc: read_parquet('<dir>/*.parquet')."
            | Entry_Channel_Bars _ -> "⭐ ENTRY: buy when the bar vwap exceeds the prior N-present-bar MAX of vwaps. One of {30,60,120,300,1200}. Default 300 (~5m on an active name). Breach counters for ALL six windows are recorded — post-hoc can TIGHTEN to a longer window (its breach implies every shorter one), not loosen."
            | Exit_Channel_Bars _ -> "EXIT (opposite side): vwap under the prior N-present-bar MIN. One of {30,60,120,300,1200}. Default 300."
            | Exit_Z_Bars _ -> "Which k-bar aggregate drives the acceleration-died exits. One of {1,15,30,60}. Default 60 (the 1m sums)."
            | Ezv _ -> "Exit when z(ln k-bar volume sum vs the 1200-bar baseline) < this. Default 0. -inf = off."
            | Ezt _ -> "Exit when z(ln k-bar trade-count sum) < this. Default 0. -inf = off."
            | Dv_Floor_60 _ -> "Hard entry gate: >= this many DOLLARS traded over the trailing 60 present bars at the signal. Default 100000. A breakout needs volume."
            | Tc_Floor_60 _ -> "Hard entry gate: >= this many TRADES over the same window. Default 60. A breakout needs activity — volume without trades is one block print."
            | Min_Dv_0945 _ -> "Universe floor: min 09:30-09:45 dollar volume (LIVE-SAFE). Default 10000000 — momentum wants names that trade every second (>= $500M/day names are active 67%% of seconds, $100-500M 33%%)."
            | Max_Concurrent _ -> "0 (DEFAULT) = the SAMPLER: unlimited concurrent positions — every breakout bar opens another trip, so it PYRAMIDS. Removes path dependency (every trip = an independent row) but PF is then ATTRIBUTION, not a portfolio number. 1 = a real book."
            | Entry_Start_Sec _ -> "Earliest ET second (since midnight) an entry may fire. Default 35100 = 09:45. ⚠ Must be >= 35100 — the knowability guard."
            | Entry_End_Sec _ -> "Latest ET second an entry may fire. Default 48600 = 13:30."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "TradingEdge.SurgeRider")
    let parsed =
        try parser.Parse(argv, raiseOnUsage = true)
        with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

    let dbPath = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let secDir = parsed.GetResult(Sec_Dir, defaultValue = defaultSecDir)
    let outDir = parsed.GetResult(Out_Dir, defaultValue = defaultOutDir)
    let parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")
    let startDate = parsed.GetResult(Start_Date, defaultValue = "2020-01-02") |> parseDate
    let endDate   = parsed.GetResult(End_Date,   defaultValue = "2026-07-17") |> parseDate

    let d = defaultConfig
    let cfg =
        { d with
            Intraday =
                { d.Intraday with
                    EntryChannelBars = parsed.GetResult(Entry_Channel_Bars, defaultValue = d.Intraday.EntryChannelBars)
                    ExitChannelBars  = parsed.GetResult(Exit_Channel_Bars,  defaultValue = d.Intraday.ExitChannelBars)
                    ExitZBars        = parsed.GetResult(Exit_Z_Bars,        defaultValue = d.Intraday.ExitZBars)
                    Ezv              = parsed.GetResult(Ezv,                defaultValue = d.Intraday.Ezv)
                    Ezt              = parsed.GetResult(Ezt,                defaultValue = d.Intraday.Ezt)
                    DvFloor60        = parsed.GetResult(Dv_Floor_60,        defaultValue = d.Intraday.DvFloor60)
                    TcFloor60        = parsed.GetResult(Tc_Floor_60,        defaultValue = d.Intraday.TcFloor60)
                    MaxConcurrent    = parsed.GetResult(Max_Concurrent,     defaultValue = d.Intraday.MaxConcurrent)
                    EntryStartSec    = parsed.GetResult(Entry_Start_Sec,    defaultValue = d.Intraday.EntryStartSec)
                    EntryEndSec      = parsed.GetResult(Entry_End_Sec,      defaultValue = d.Intraday.EntryEndSec) }
            MinDv0945 = parsed.GetResult(Min_Dv_0945, defaultValue = d.MinDv0945) }

    // ⚠ KNOWABILITY GUARD (docs/lookahead_protocol.md R4). The universe is GATED on
    // dv_0945 and every trip RECORDS dv_0945 / rvol_0945_honest — all three are only
    // determined at 09:45 ET. Legal ONLY because entries start at/after 09:45; lower
    // the window and the universe selection itself becomes conditioned on the future —
    // the exact bug class that killed three systems on 2026-07-16. Fail loudly.
    if cfg.Intraday.EntryStartSec < 35100 then
        eprintfn "FATAL: --entry-start-sec %d is before 09:45 (35100)." cfg.Intraday.EntryStartSec
        eprintfn "  The universe filter (dv_0945) and the recorded 09:45 context columns are only"
        eprintfn "  determined at 09:45. Entering before then makes ALL of them LOOKAHEADS."
        eprintfn "  See docs/lookahead_protocol.md (the knowability clock)."
        exit 1
    // Channel/z-window membership: the engine pre-builds exactly these windows and
    // aliases the entry/exit channel onto them — an off-menu value would otherwise
    // throw invalidArg mid-run, after the candidate query already ran.
    let chanSet = [ 30; 60; 120; 300; 1200 ]
    if not (List.contains cfg.Intraday.EntryChannelBars chanSet) then
        eprintfn "FATAL: --entry-channel-bars %d — must be one of %A." cfg.Intraday.EntryChannelBars chanSet
        exit 1
    if not (List.contains cfg.Intraday.ExitChannelBars chanSet) then
        eprintfn "FATAL: --exit-channel-bars %d — must be one of %A." cfg.Intraday.ExitChannelBars chanSet
        exit 1
    if not (List.contains cfg.Intraday.ExitZBars [ 1; 15; 30; 60 ]) then
        eprintfn "FATAL: --exit-z-bars %d — must be one of [1; 15; 30; 60]." cfg.Intraday.ExitZBars
        exit 1

    let ic = cfg.Intraday
    let hhmmss s = sprintf "%02d:%02d:%02d" (s / 3600) (s % 3600 / 60) (s % 60)
    printfn "SurgeRider — 1s MOMENTUM (ride the %d-bar vwap-channel breakout; LONG)" ic.EntryChannelBars
    printfn "  db          = %s" dbPath
    printfn "  1s bars     = %s" secDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  universe    = dv_0945 >= $%.1fM   [LIVE-SAFE; rvol_0945_honest RECORDED, not gated]" (cfg.MinDv0945 / 1e6)
    printfn "  ENTRY       = vwap > prior %d-bar MAX   AND dv60 >= $%.0fk AND tc60 >= %.0f   (fill: NEXT bar vwap)"
        ic.EntryChannelBars (ic.DvFloor60 / 1e3) ic.TcFloor60
    printfn "  EXIT        = z%d(ln vol) < %g  |  z%d(ln tc) < %g  |  vwap < prior %d-bar MIN  |  MOC   (fill: NEXT bar vwap)"
        ic.ExitZBars ic.Ezv ic.ExitZBars ic.Ezt ic.ExitChannelBars
    printfn "  entry window= %s-%s ET   features fold from %s ET" (hhmmss ic.EntryStartSec) (hhmmss ic.EntryEndSec) (hhmmss ic.SessionStartSec)
    if ic.MaxConcurrent <= 0 then
        printfn "  mode        = ⭐ SAMPLER (mc=0 unlimited → every breakout bar opens another trip)"
        printfn "                PF/net below are ATTRIBUTION ONLY, not portfolio numbers."
    else
        printfn "  mode        = BOOK (max-concurrent %d)" ic.MaxConcurrent

    let sw = Stopwatch.StartNew()
    let progress = Some (fun (date: DateOnly) (total: int64) ->
        eprintf "\r  %O  trips so far: %d   (%.0fs)      " date total sw.Elapsed.TotalSeconds)
    let nCand, daysRun, stats = run dbPath secDir outDir cfg startDate endDate progress
    sw.Stop()
    eprintfn ""

    let pf = if stats.GrossLoss = 0.0 then nan else stats.GrossWin / stats.GrossLoss
    printfn ""
    printfn "  candidates = %d  (ticker-days; %d had a 1s tape)" nCand daysRun
    printfn "  trips      = %d  (%.1f s)" stats.Total sw.Elapsed.TotalSeconds
    printfn "  win rate   = %.1f%%  (%d / %d)"
        (100.0 * float stats.Wins / float (max 1L stats.Total)) stats.Wins stats.Total
    printfn "  net P&L    = %s   ⚠ costs not modeled" ((stats.GrossWin - stats.GrossLoss).ToString "N0")
    printfn "  PF         = %.3f%s" pf (if ic.MaxConcurrent <= 0 then "   [ATTRIBUTION ONLY — mc=0]" else "")
    printfn "  wrote      = %s/trips_p*.parquet" outDir
    0
