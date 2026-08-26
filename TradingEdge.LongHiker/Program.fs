module TradingEdge.LongHiker.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.LongHiker.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/research/data/trading.db"
let private defaultSecDir = "/home/mrakgr/Trading-Edge/research/data/intraday_1s_slim"
let private defaultOutDir = "/tmp/longhiker_trips"

/// LongHiker CLI. Deliberately TINY (the FlushFader discipline): this is a
/// SAMPLER, so the study happens in SQL over the emitted trip PARQUET, not by
/// re-running with different flags. If you want a new GATE here, add a recorded
/// FEATURE instead and slice it post-hoc.
type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-s")>] Sec_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out_Dir of string
    // ----- the system (all of it) -----
    | Min_Eff_Open_Slots of int
    | Hold_Bars of int
    | Signal_On_Extremes_Only of bool
    | Signal_Stride of int
    // ----- per-bar liquidity floors -----
    | Dv_Floor_60 of float
    | Tc_Floor_60 of float
    // ----- record-first regime band (default off) -----
    | Min_Volat_20m of float
    | Max_Volat_20m of float
    // ----- universe -----
    | Min_Dv_0945 of float
    | Min_Rvol_0945 of float
    | Min_Prev_Close of float
    | Min_Barnum of int
    // ----- sampler vs book -----
    | Max_Concurrent of int
    | Workers of int
    // ----- timing -----
    | Entry_Start_Sec of int
    | Entry_End_Sec of int
    | Entry_End_Sec_Short of int
    | Moc_Sec of int
    | Moc_Sec_Short of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db_Path _ -> "DuckDB database path."
            | Sec_Dir _ -> "Directory of 1-second slim parquet files (data/intraday_1s_slim)."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd)."
            | Out_Dir _ -> "Output DIRECTORY for the trip parquet part files. Post-hoc: read_parquet('<dir>/*.parquet')."
            | Min_Eff_Open_Slots _ -> "Completed 30-bar slots before the eff_open FEATURE is warm (the v7 sampler has NO eff gate). Default 4."
            | Hold_Bars _ -> "⭐ THE EXIT: a pure TIMESTOP — exit this many PRESENT bars after the fill bar, at that bar's vwap. Default 30. The fwd_vwap_* columns answer every other horizon post-hoc, so do NOT sweep this by re-running."
            | Signal_On_Extremes_Only _ -> "⭐ Fire ONLY on bars printing a NEW EXTREME in a tracked channel (a new {1,2,5,10,20}m high OR low). Default true — the intermediate bars are ~88%% of the book. ⚠ This shrinks but does not remove the trip-count weighting problem (S15): a day making more new highs still yields more trips."
            | Signal_Stride _ -> "Fire only every Nth qualifying bar per (ticker,day). Default 1 = every bar (the design). > 1 is a UNIFORM SUBSAMPLE — unbiased for means, but ⚠ never report a stride run as a book."
            | Dv_Floor_60 _ -> "Hard entry gate: >= this many DOLLARS traded over the trailing 60 present bars. Default 100000. 0 = off."
            | Tc_Floor_60 _ -> "Hard entry gate: >= this many TRADES over the same window. Default 60 — volume without trades is one block print. 0 = off."
            | Min_Volat_20m _ -> "volat_20m floor at the signal (raw mean-|r| per 30s slot; a cold volat FAILS a positive floor). Default 0 = off — band it post-hoc."
            | Max_Volat_20m _ -> "volat_20m ceiling. Default inf = off."
            | Min_Dv_0945 _ -> "Universe pre-filter on the candidate dv_0945 column. Default 0 = off. ⚠ 09:45-class."
            | Min_Rvol_0945 _ -> "Universe pre-filter: rvol_0945_honest >= this. Default 0 = off (sampler breadth). ⚠ 09:45-class."
            | Min_Prev_Close _ -> "Universe gate: PRIOR day's close in day-D RAW scale >= this. Knowable BEFORE the open. Default 0 = off."
            | Min_Barnum _ -> "Episode warmup: candidate barnum (prior-only ROW_NUMBER, live-knowable) >= this. Default 22. 0 = off. Column-guarded."
            | Max_Concurrent _ -> "0 (DEFAULT) = the SAMPLER: every qualifying bar opens an independent trip. PF is then ATTRIBUTION, not a portfolio number. 1 = a real book."
            | Workers _ -> "Parallel day-workers (default: cores - 2). Trip SET is identical at any worker count; parquet row ORDER is not."
            | Entry_Start_Sec _ -> "Earliest ET second an entry may fire. Default 34800 = 09:40 (user). ⚠⚠ BELOW 35100 the candidate universe (dv_0945_tape / n_bars_1s over [09:30,09:45)) is a LOOKAHEAD — the run is still emitted, with a loud banner, because `signal_sec` is a recorded column and `WHERE signal_sec >= 35100` IS the free control. Run every headline both ways."
            | Entry_End_Sec _ -> "Latest ET second an entry may fire. Default 57000 = 15:50."
            | Entry_End_Sec_Short _ -> "The same bound on NYSE early-close days. Default 46200 = 12:50."
            | Moc_Sec _ -> "Latest ET second a position may be held; holders force-exit at the first bar >= this. Default 57600 = 16:00. ⭐ ALSO CAPS THE BAR QUERY."
            | Moc_Sec_Short _ -> "The same bound on NYSE early-close days. Default 46800 = 13:00."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "TradingEdge.LongHiker")
    let parsed =
        try parser.Parse(argv, raiseOnUsage = true)
        with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

    let dbPath = parsed.GetResult(Db_Path, defaultValue = defaultDb)
    let secDir = parsed.GetResult(Sec_Dir, defaultValue = defaultSecDir)
    let outDir = parsed.GetResult(Out_Dir, defaultValue = defaultOutDir)
    let parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")
    let startDate = parsed.GetResult(Start_Date, defaultValue = "2020-01-02") |> parseDate
    let endDate   = parsed.GetResult(End_Date,   defaultValue = "2026-08-21") |> parseDate

    let d = defaultConfig
    let cfg =
        { d with
            Intraday =
                { d.Intraday with
                    MinEffOpenSlots  = parsed.GetResult(Min_Eff_Open_Slots, defaultValue = d.Intraday.MinEffOpenSlots)
                    HoldBars         = parsed.GetResult(Hold_Bars,          defaultValue = d.Intraday.HoldBars)
                    SignalOnExtremesOnly = parsed.GetResult(Signal_On_Extremes_Only, defaultValue = d.Intraday.SignalOnExtremesOnly)
                    SignalStride     = parsed.GetResult(Signal_Stride,      defaultValue = d.Intraday.SignalStride)
                    DvFloor60        = parsed.GetResult(Dv_Floor_60,        defaultValue = d.Intraday.DvFloor60)
                    TcFloor60        = parsed.GetResult(Tc_Floor_60,        defaultValue = d.Intraday.TcFloor60)
                    MinVolat20m      = parsed.GetResult(Min_Volat_20m,      defaultValue = d.Intraday.MinVolat20m)
                    MaxVolat20m      = parsed.GetResult(Max_Volat_20m,      defaultValue = d.Intraday.MaxVolat20m)
                    MaxConcurrent    = parsed.GetResult(Max_Concurrent,     defaultValue = d.Intraday.MaxConcurrent)
                    EntryStartSec    = parsed.GetResult(Entry_Start_Sec,    defaultValue = d.Intraday.EntryStartSec)
                    EntryEndSec      = parsed.GetResult(Entry_End_Sec,      defaultValue = d.Intraday.EntryEndSec)
                    EntryEndSecShort = parsed.GetResult(Entry_End_Sec_Short, defaultValue = d.Intraday.EntryEndSecShort)
                    MocSec           = parsed.GetResult(Moc_Sec,            defaultValue = d.Intraday.MocSec)
                    MocSecShort      = parsed.GetResult(Moc_Sec_Short,      defaultValue = d.Intraday.MocSecShort) }
            MinDv0945 = parsed.GetResult(Min_Dv_0945, defaultValue = d.MinDv0945)
            MinRvol0945 = parsed.GetResult(Min_Rvol_0945, defaultValue = d.MinRvol0945)
            MinPrevClose = parsed.GetResult(Min_Prev_Close, defaultValue = d.MinPrevClose)
            MinBarnum = parsed.GetResult(Min_Barnum, defaultValue = d.MinBarnum)
            Workers = parsed.GetResult(Workers, defaultValue = d.Workers) }

    let ic = cfg.Intraday
    if ic.HoldBars < 1 then
        eprintfn "FATAL: --hold-bars %d — the timestop must be at least 1 present bar." ic.HoldBars
        exit 1
    if ic.SignalStride < 1 then
        eprintfn "FATAL: --signal-stride %d — must be >= 1." ic.SignalStride
        exit 1
    if ic.EntryStartSec < ic.SessionStartSec then
        eprintfn "FATAL: --entry-start-sec %d is before the session start %d." ic.EntryStartSec ic.SessionStartSec
        exit 1

    let hhmmss s = sprintf "%02d:%02d:%02d" (s / 3600) (s % 3600 / 60) (s % 60)
    printfn "LongHiker v7 — 1s BOTH-SIDES momentum: breakouts from tight consolidations"
    printfn "  db          = %s" dbPath
    printfn "  candidates  = %s%s" Backtest.candidateTable
        (match Environment.GetEnvironmentVariable "LH_CANDIDATE_TABLE" with
         | null | "" -> "  (default)" | _ -> "  [LH_CANDIDATE_TABLE override]")
    printfn "  1s bars     = %s" secDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  ENTRY       = EVERY new-20m-extreme bar (side +1 hi / -1 lo, NO eff gate)   AND dv60 >= $%.0fk AND tc60 >= %.0f   (fill: NEXT bar vwap)"
        (ic.DvFloor60 / 1e3) ic.TcFloor60
    printfn "  EXIT        = TIMESTOP %d present bars after the fill, at that bar's vwap  |  MOC backstop" ic.HoldBars
    printfn "  exit marks  = trailing stops lo/hi {1,2,5}m break | ts 60/90/120b   (RECORDED, not enforced)"
    printfn "  signal bars = %s" (if ic.SignalOnExtremesOnly then "⭐ NEW 20m EXTREMES ONLY (v7: new 1200-bar high or low)" else "every qualifying bar")
    if ic.SignalStride > 1 then
        printfn "  ⚠ STRIDE    = every %dth qualifying bar (UNIFORM SUBSAMPLE — not a book)" ic.SignalStride
    printfn "  volat band  = volat_20m ∈ [%s, %s) bp/30s   [record-first]"
        (if ic.MinVolat20m <= 0.0 then "0=off" else sprintf "%.0f" (ic.MinVolat20m * 1e4))
        (if Double.IsPositiveInfinity ic.MaxVolat20m then "inf" else sprintf "%.0f" (ic.MaxVolat20m * 1e4))
    printfn "  entry window= %s-%s ET   features fold from %s ET" (hhmmss ic.EntryStartSec) (hhmmss ic.EntryEndSec) (hhmmss ic.SessionStartSec)
    printfn "  close       = %s ET (%s on early closes)" (hhmmss ic.MocSec) (hhmmss ic.MocSecShort)
    if ic.MaxConcurrent <= 0 then
        printfn "  mode        = ⭐ SAMPLER (mc=0 unlimited → every qualifying bar opens a trip)"
        printfn "                PF/net below are ATTRIBUTION ONLY, not portfolio numbers."
    else
        printfn "  mode        = BOOK (max-concurrent %d)" ic.MaxConcurrent
    // ⚠⚠ THE KNOWABILITY BANNER (docs/lookahead_protocol.md R5). Unlike FlushFader
    // this is a WARNING, not a fatal: the 09:40 window is the system's whole
    // premise (user), and because `signal_sec` is recorded the clean book is one
    // WHERE clause away. Loud, every run, so no headline is ever quoted without it.
    if ic.EntryStartSec < 35100 then
        printfn ""
        printfn "  ⚠⚠ LOOKAHEAD NOTICE — entries start at %s, before the 09:45 knowability floor." (hhmmss ic.EntryStartSec)
        printfn "     The universe (%s) gates on dv_0945_tape / n_bars_1s measured over" Backtest.candidateTable
        printfn "     [09:30, 09:45), so every trip with signal_sec < 35100 was SELECTED using tape"
        printfn "     that had not happened yet. This is deliberate and quarantined, not ignored:"
        printfn "     ⭐ the control is    WHERE signal_sec >= 35100    — free, no re-run."
        printfn "     Quote every headline BOTH ways. If the 09:40-09:45 slice carries the edge,"
        printfn "     the edge is the lookahead. See docs/lookahead_protocol.md."

    let sw = Stopwatch.StartNew()
    let mutable lastPrint = -1.0
    let progress = Some (fun (date: DateOnly) (proc: int) (totalTkd: int) (trips: int64) ->
        let el = sw.Elapsed.TotalSeconds
        if el - lastPrint >= 1.0 || proc = totalTkd then
            lastPrint <- el
            let etaMin =
                if proc > 0 then el / float proc * float (totalTkd - proc) / 60.0 else nan
            eprintf "\r  %O  tkd %d/%d (%.1f%%)  trips %d  %.0fs elapsed  ~%.0fm left      "
                date proc totalTkd (100.0 * float proc / float (max 1 totalTkd)) trips el etaMin)
    let nCand, daysRun, stats = run dbPath secDir outDir cfg startDate endDate progress
    sw.Stop()
    eprintfn ""

    let pf = if stats.GrossLoss = 0.0 then nan else stats.GrossWin / stats.GrossLoss
    printfn ""
    printfn "  candidates = %d  (ticker-days; %d had a 1s tape)" nCand daysRun
    printfn "  trips      = %d  (%.1f s)" stats.Total sw.Elapsed.TotalSeconds
    printfn "  trips/tkd  = %.1f" (float stats.Total / float (max 1 daysRun))
    printfn "  win rate   = %.1f%%  (%d / %d)"
        (100.0 * float stats.Wins / float (max 1L stats.Total)) stats.Wins stats.Total
    printfn "  net P&L    = %s   ⚠ costs not modeled" ((stats.GrossWin - stats.GrossLoss).ToString "N0")
    printfn "  PF         = %.3f%s" pf (if ic.MaxConcurrent <= 0 then "   [ATTRIBUTION ONLY — mc=0]" else "")
    printfn "  wrote      = %s/trips_p*.parquet" outDir
    0
