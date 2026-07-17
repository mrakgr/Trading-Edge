module TradingEdge.DipRiderV6.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.DipRiderV6.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultMinuteDir = "/home/mrakgr/Trading-Edge/data/minute_aggs"
let private defaultCsv = "/tmp/diprider_v6_trips.csv"

/// DipRiderV6 CLI. Deliberately TINY: V6 is a SAMPLER, so the study happens in SQL over
/// the emitted CSV, NOT by re-running with different flags. If you find yourself wanting a
/// new GATE flag here, add a recorded FEATURE instead and slice it post-hoc.
type Args =
    | [<AltCommandLine("-d")>] Db_Path of string
    | [<AltCommandLine("-m")>] Minute_Dir of string
    | Start_Date of string
    | End_Date of string
    | [<AltCommandLine("-o")>] Out of string
    // ----- the system (all of it) -----
    | Entry_Low_Window of int
    | Exit_High_Window of int
    | Require_Above_Vwap
    // ----- the one surviving gate -----
    | Min_Lows_Into_Leg of int
    | Min_Intraday_Atr_Pct of float
    // ----- universe -----
    | Min_Dv_0945 of float
    // ----- sampler vs book -----
    | Max_Concurrent of int
    // ----- timing -----
    | Entry_Start_Min of int
    | Entry_End_Min of int
    | Vol_Window of int
    | Adx_Period of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Db_Path _ -> "DuckDB database path."
            | Minute_Dir _ -> "Directory of minute_aggs parquet files."
            | Start_Date _ -> "Backtest start date (yyyy-MM-dd)."
            | End_Date _ -> "Backtest end date (yyyy-MM-dd)."
            | Out _ -> "Output trips CSV path."
            | Entry_Low_Window _ -> "⭐ ENTRY: buy when the close makes a new N-minute LOW of 1m-bar CLOSES. Default 20. Try 60 for a deeper, rarer dip."
            | Exit_High_Window _ -> "⭐ EXIT: sell when the close reaches the N-minute HIGH of 1m-bar CLOSES. Default 20."
            | Require_Above_Vwap -> "Drop the `close > VWAP` entry condition. ⚠ V5 F6 proved it LOAD-BEARING (PF 1.429->1.319 AND avg/trade 0.670%%->0.561%% across 8x the trips). Ablation only."
            | Min_Lows_Into_Leg _ -> "⭐ WAIT FOR THE Kth LOW (F3): only enter once this down-leg has made >= K consecutive new lows, and take ONE position per leg. 0 (default) = the sampler (every low). K=5 + --max-concurrent 1 = the production book: PF 1.968 / +1.48%%/trade / 1068 trips, positive every year. The edge is in the BLEEDERS (F1), and this harvests it WITHOUT averaging down."
            | Min_Intraday_Atr_Pct _ -> "The other gate in V6: 20m log-ATR >= this (MR needs range to revert across). Default 0.013. 0 = off."
            | Min_Dv_0945 _ -> "Universe floor: min 09:30-09:45 dollar volume (LIVE-SAFE; replaces V4's leaked ADV floor). Default 5000000."
            | Max_Concurrent _ -> "0 (DEFAULT) = the SAMPLER: unlimited concurrent positions, i.e. it AVERAGES DOWN. Removes path dependency so every trip is an independent row for post-hoc SQL — but PF is then ATTRIBUTION, NOT a portfolio number. Use 1 for a real tradable book (V5 F6: mc=0 PF 1.429 vs mc=1 1.285)."
            | Entry_Start_Min _ -> "Earliest ET minute an entry may fire (default 600 = 10:00). ⚠ Must be >= 585 (09:45) — see the knowability guard."
            | Entry_End_Min _ -> "Latest ET minute an entry may fire (default 810 = 13:30)."
            | Vol_Window _ -> "Trailing window in 1m bars for ATR / the 20m slopes / vol_climb (default 20)."
            | Adx_Period _ -> "Wilder ADX/DI period on 1m bars (default 14)."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "TradingEdge.DipRiderV6")
    let parsed =
        try parser.Parse(argv, raiseOnUsage = true)
        with :? ArguParseException as ex -> eprintfn "%s" ex.Message; exit 1

    let dbPath    = parsed.GetResult(Db_Path,    defaultValue = defaultDb)
    let minuteDir = parsed.GetResult(Minute_Dir, defaultValue = defaultMinuteDir)
    let outPath   = parsed.GetResult(Out,        defaultValue = defaultCsv)
    let parseDate (s: string) = DateOnly.ParseExact(s, "yyyy-MM-dd")
    let startDate = parsed.GetResult(Start_Date, defaultValue = "2020-01-01") |> parseDate
    let endDate   = parsed.GetResult(End_Date,   defaultValue = "2026-06-30") |> parseDate

    let d = defaultConfig
    let cfg =
        { d with
            Intraday =
                { d.Intraday with
                    EntryLowWindow   = parsed.GetResult(Entry_Low_Window, defaultValue = d.Intraday.EntryLowWindow)
                    ExitHighWindow   = parsed.GetResult(Exit_High_Window, defaultValue = d.Intraday.ExitHighWindow)
                    RequireAboveVwap = d.Intraday.RequireAboveVwap || parsed.Contains Require_Above_Vwap
                    MinLowsIntoLeg   = parsed.GetResult(Min_Lows_Into_Leg, defaultValue = d.Intraday.MinLowsIntoLeg)
                    MinAtrPct        = parsed.GetResult(Min_Intraday_Atr_Pct, defaultValue = d.Intraday.MinAtrPct)
                    MaxConcurrent    = parsed.GetResult(Max_Concurrent,  defaultValue = d.Intraday.MaxConcurrent)
                    EntryStartMin    = parsed.GetResult(Entry_Start_Min, defaultValue = d.Intraday.EntryStartMin)
                    EntryEndMin      = parsed.GetResult(Entry_End_Min,   defaultValue = d.Intraday.EntryEndMin)
                    VolWindow        = parsed.GetResult(Vol_Window,      defaultValue = d.Intraday.VolWindow)
                    AdxPeriod        = parsed.GetResult(Adx_Period,      defaultValue = d.Intraday.AdxPeriod) }
            MinDv0945 = parsed.GetResult(Min_Dv_0945, defaultValue = d.MinDv0945) }

    // ⚠ KNOWABILITY GUARD (docs/lookahead_protocol.md R3). dv_0945 and permin15m are both
    // only determined at 09:45, and are legal ONLY because entries start at/after 09:45.
    // Lower the entry window below that and they SILENTLY become lookaheads — precisely the
    // class of bug that destroyed three systems on 2026-07-16. Fail loudly instead.
    if cfg.Intraday.EntryStartMin < 585 then
        eprintfn "FATAL: --entry-start-min %d is before 09:45 (585)." cfg.Intraday.EntryStartMin
        eprintfn "  dv_0945 (the universe filter) and permin15m (the volume baseline) are only"
        eprintfn "  determined at 09:45. Entering before then makes BOTH of them LOOKAHEADS."
        eprintfn "  See docs/lookahead_protocol.md R3 (the knowability clock)."
        exit 1

    let ic = cfg.Intraday
    let hhmm m = sprintf "%02d:%02d" (m / 60) (m % 60)
    printfn "DipRiderV6 — INTRADAY MEAN REVERSION (buy the %dm low above VWAP, sell into the %dm high)"
        ic.EntryLowWindow ic.ExitHighWindow
    printfn "  db          = %s" dbPath
    printfn "  range       = %O .. %O" startDate endDate
    // NB: rvol_0945_honest is NOT gated here — it is a RECORDED column on diprider_v6_candidate
    // (built with --min-rvol 0) so the sampler can slice it post-hoc. Do not claim a gate we do not apply.
    printfn "  universe    = %s   [LIVE-SAFE; rvol_0945_honest RECORDED, not gated]"
        (if cfg.MinDv0945 > 0.0 then sprintf "dv_0945 >= $%.1fM" (cfg.MinDv0945 / 1e6) else "NONE (dv_0945 recorded only)")
    printfn "  ENTRY       = close <= prior %dm MIN of closes%s%s" ic.EntryLowWindow
        (if ic.RequireAboveVwap then "   AND close > VWAP" else "   (VWAP: RECORDED not gated — sampler)")
        (if ic.MinLowsIntoLeg > 0 then sprintf "   AND >= %d lows into the leg (ONE position/leg)" ic.MinLowsIntoLeg else "")
    printfn "  EXIT        = close >= prior %dm MAX of closes (target)  |  MOC    [NO STOP]" ic.ExitHighWindow
    printfn "  gate        = %s" (if ic.MinAtrPct > 0.0 then sprintf "log-ATR20 >= %.3f" ic.MinAtrPct else "NONE — pure sampler (every filter is post-hoc)")
    printfn "  entry window= %s–%s ET   features fold from %s ET" (hhmm ic.EntryStartMin) (hhmm ic.EntryEndMin) (hhmm ic.FeatureStartMin)
    if ic.MaxConcurrent <= 0 then
        printfn "  mode        = ⭐ SAMPLER (mc=0 unlimited → it AVERAGES DOWN)"
        printfn "                PF/net below are ATTRIBUTION ONLY, not portfolio numbers."
        printfn "                Re-run any selected cell at --max-concurrent 1 for a real book."
    else
        printfn "  mode        = BOOK (max-concurrent %d)" ic.MaxConcurrent

    let sw = Stopwatch.StartNew()
    let trips, nCand = run dbPath minuteDir cfg startDate endDate
    sw.Stop()

    writeCsv outPath trips

    let wins   = trips |> Array.filter (fun t -> t.NetPnL > 0.0)
    let sumW   = wins |> Array.sumBy (fun t -> t.NetPnL)
    let sumL   = trips |> Array.filter (fun t -> t.NetPnL < 0.0) |> Array.sumBy (fun t -> t.NetPnL)
    let pf     = if sumL = 0.0 then nan else sumW / -sumL
    let netPnl = trips |> Array.sumBy (fun t -> t.NetPnL)
    let avgPct = if trips.Length = 0 then nan else 100.0 * (trips |> Array.averageBy (fun t -> t.RetMoc))

    printfn ""
    printfn "  candidates = %d  (ticker-days that cleared the preconditions)" nCand
    printfn "  trips      = %d  (%.1f s)" trips.Length sw.Elapsed.TotalSeconds
    printfn "  win rate   = %.1f%%  (%d / %d)"
        (100.0 * float wins.Length / float (max 1 trips.Length)) wins.Length trips.Length
    printfn "  avg %%/trip = %.3f%%   ⚠ costs ≈0.1%% round-trip (spread) + commissions" avgPct
    printfn "  net P&L    = %s" (netPnl.ToString("N0"))
    printfn "  PF (MOC)   = %.3f%s" pf (if ic.MaxConcurrent <= 0 then "   [ATTRIBUTION ONLY — mc=0]" else "")
    printfn "  wrote      = %s" outPath
    0
