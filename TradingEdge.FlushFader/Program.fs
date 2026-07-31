module TradingEdge.FlushFader.Program

open System
open System.Diagnostics
open Argu
open TradingEdge.FlushFader.Backtest

let private defaultDb = "/home/mrakgr/Trading-Edge/data/trading.db"
let private defaultSecDir = "/home/mrakgr/Trading-Edge/data/intraday_1s_slim"
let private defaultOutDir = "/tmp/flushfader_trips"

/// FlushFader CLI. Deliberately TINY (the DipRiderV6 discipline): this is a
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
    // ----- per-bar liquidity floors -----
    | Dv_Floor_60 of float
    | Tc_Floor_60 of float
    // ----- record-first regime gates (default off) -----
    | Min_Volat_20m of float
    | Max_Volat_20m of float
    | Min_Abs_Eff_20m of float
    // ----- SPEC v1.2 gates (defaults = the S18 production stack) -----
    | Max_Speed_1m of float
    | K_Band_Lo of int
    | K_Band_Hi of int
    | Eff20_Lo of float
    | Eff20_Hi of float
    | Min_Abs_Eff_10m of float
    | Dist_Hi_Lo of float
    | Dist_Hi_Hi of float
    | Min_Vol10_Rate of float
    | Min_Lows_300 of int
    | Max_Rng_Front of float
    | Min_Dv_0945_Tape of float
    // ----- price-acceptance stops -----
    | Vol_Stop_Ratio of float
    | Tc_Stop_Ratio of float
    | Speed_Stop_Pct of float
    // ----- universe -----
    | Min_Dv_0945 of float
    | Min_Rvol_0945 of float
    | Min_Prev_Close of float
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
            | Entry_Channel_Bars _ -> "⭐ ENTRY: buy when the bar vwap prints STRICTLY UNDER the prior N-present-bar MIN of vwaps (the flush channel; also the leg-reset channel — a new N-bar HIGH ends the down-leg). One of {60,120,300,600,1200}. Default 1200 (~20m on an active name)."
            | Exit_Channel_Bars _ -> "⭐ EXIT: sell when the vwap STRICTLY EXCEEDS the prior N-present-bar MAX (the reversion target). One of {30,60,120,300,600,1200}. Default 300 (~5m — V6 F16's direction). NO stop; MOC backstop."
            | Dv_Floor_60 _ -> "Hard entry gate: >= this many DOLLARS traded over the trailing 60 present bars at the signal. Default 100000."
            | Tc_Floor_60 _ -> "Hard entry gate: >= this many TRADES over the same window. Default 60 — volume without trades is one block print."
            | Min_Volat_20m _ -> "volat_20m floor at the signal (raw mean-|r|/30s units; cold volat FAILS a positive floor). Default 0 = off. ⚠ RECORD-FIRST: the breakout F10 band does NOT transfer to MR (THE INVERSION) — band post-hoc over the volat_20m column."
            | Max_Volat_20m _ -> "volat_20m ceiling. Default inf = off. Same record-first stance."
            | Min_Abs_Eff_20m _ -> "|eff_20m| floor at the signal (cold eff FAILS a positive floor). Default 0 = off — superseded by the SIGNED --eff20-lo/--eff20-hi band; kept for sweeps."
            | Max_Speed_1m _ -> "⭐ SPEC v1.2: flush speed gate — vwap/vwap_60_prev - 1 < this at the signal. Default -0.02. 0 = off."
            | K_Band_Lo _ -> "⭐ SPEC v1.2: lows_since_first_low >= this (K band floor — THE 2022 fix). Default 26. 0 = off."
            | K_Band_Hi _ -> "⭐ SPEC v1.2: lows_since_first_low <= this (K band ceiling). Default 50. 0 = off."
            | Eff20_Lo _ -> "⭐ SPEC v1.2: SIGNED eff_20m >= this. Default -0.5. -Infinity = off."
            | Eff20_Hi _ -> "⭐ SPEC v1.2: SIGNED eff_20m < this. Default -0.3. Infinity = off."
            | Min_Abs_Eff_10m _ -> "⭐ SPEC v1.2: |eff_10m| >= this (no flat 10m tape). Default 0.15. 0 = off."
            | Dist_Hi_Lo _ -> "⭐ SPEC v1.2: vwap/chan_hi - 1 >= this (the -35% un-fadeable wall). Default -0.35. -Infinity = off."
            | Dist_Hi_Hi _ -> "⭐ SPEC v1.2: vwap/chan_hi - 1 < this (deep enough into the leg). Default -0.10. 0 = off."
            | Min_Vol10_Rate _ -> "⭐ SPEC v1.2: (vol_10/10)/(vol_60/60) >= this (S17 last-10s volume-rate floor — no quiet-tail drift-downs). Default 0.75. 0 = off."
            | Min_Lows_300 _ -> "⭐ SPEC v1.4: lows_since_first_low_300 >= this — kills the FAST-CHASE re-entry (5m bounce without a 20m leg reset re-signals in seconds; PF 0.11 on the A++ cell). Default 6. 0 = off."
            | Max_Rng_Front _ -> "⭐ SPEC v1.5: rng_300/rng_20m < this — reject the PURE CLIFF (whole 20m range in the last 5m; monotone-worst at mc=1). Default 0.8. Infinity = off."
            | Min_Dv_0945_Tape _ -> "Tape-native dv_0945 floor: Σ vwap·volume over OUR 1s bars strictly before 09:45 >= this (live-scanner-consistent; honest dollars). Default 0 = record-first. Pair with --min-dv-0945 0 when replacing the candidate-table gate."
            | Vol_Stop_Ratio _ -> "PRICE-ACCEPTANCE STOP: exit holders when a NEW entry-channel low prints on (vol_60/60)/(vol_1200/1200) >= this. Default Infinity = OFF (S16 A/B: stops gut the book). e.g. 8 to arm."
            | Tc_Stop_Ratio _ -> "PRICE-ACCEPTANCE STOP: same on the trade-count ratio. Default Infinity = OFF (S16)."
            | Speed_Stop_Pct _ -> "PRICE-ACCEPTANCE STOP: exit holders when a NEW entry-channel low prints at vwap/vwap_60_prev-1 < this (the flush continuing at pace). Default 0 = OFF (S16: fired on 93.7% of spec book at median 2s). e.g. -0.01 to arm."
            | Min_Dv_0945 _ -> "💀 DEPRECATED (S35): the candidate column = real dollars × adj_ratio (future-split-dependent — 20% of the universe was inflated in). Default 0 = off. THE floor is --min-dv-0945-tape."
            | Min_Rvol_0945 _ -> "Optional in-play universe pre-filter: rvol_0945_honest >= this (premkt-incl vol thru 09:45 / prior-20d avg; LIVE-SAFE at 09:45). Default 0 = off (sampler breadth)."
            | Min_Prev_Close _ -> "Universe gate: PRIOR day's close in day-D raw (post-split) scale >= this (prev_adj_close/adj_ratio; knowable BEFORE the open). Default 0 = off. 2 = the >=$2 universe (sub-$1 priced out on every EU-accessible broker)."
            | Max_Concurrent _ -> "0 (DEFAULT) = the SAMPLER: unlimited concurrent positions — every new low opens another trip, so it AVERAGES DOWN. Removes path dependency (every trip = an independent row) but PF is then ATTRIBUTION, not a portfolio number. 1 = a real book."
            | Entry_Start_Sec _ -> "Earliest ET second (since midnight) an entry may fire. Default 35100 = 09:45 — the knowability floor itself (the old 10:00 was a VwapReclaim-era throwback). ⚠ Must be >= 35100."
            | Entry_End_Sec _ -> "Latest ET second an entry may fire. Default 57000 = 15:50 (last 10m reserved for exits)."

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Args>(programName = "TradingEdge.FlushFader")
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
                    DvFloor60        = parsed.GetResult(Dv_Floor_60,        defaultValue = d.Intraday.DvFloor60)
                    TcFloor60        = parsed.GetResult(Tc_Floor_60,        defaultValue = d.Intraday.TcFloor60)
                    MinVolat20m      = parsed.GetResult(Min_Volat_20m,      defaultValue = d.Intraday.MinVolat20m)
                    MaxVolat20m      = parsed.GetResult(Max_Volat_20m,      defaultValue = d.Intraday.MaxVolat20m)
                    MinAbsEff20m     = parsed.GetResult(Min_Abs_Eff_20m,    defaultValue = d.Intraday.MinAbsEff20m)
                    MaxSpeed1m       = parsed.GetResult(Max_Speed_1m,       defaultValue = d.Intraday.MaxSpeed1m)
                    KBandLo          = parsed.GetResult(K_Band_Lo,          defaultValue = d.Intraday.KBandLo)
                    KBandHi          = parsed.GetResult(K_Band_Hi,          defaultValue = d.Intraday.KBandHi)
                    Eff20Lo          = parsed.GetResult(Eff20_Lo,           defaultValue = d.Intraday.Eff20Lo)
                    Eff20Hi          = parsed.GetResult(Eff20_Hi,           defaultValue = d.Intraday.Eff20Hi)
                    MinAbsEff10m     = parsed.GetResult(Min_Abs_Eff_10m,    defaultValue = d.Intraday.MinAbsEff10m)
                    DistHiLo         = parsed.GetResult(Dist_Hi_Lo,         defaultValue = d.Intraday.DistHiLo)
                    DistHiHi         = parsed.GetResult(Dist_Hi_Hi,         defaultValue = d.Intraday.DistHiHi)
                    MinVol10Rate     = parsed.GetResult(Min_Vol10_Rate,     defaultValue = d.Intraday.MinVol10Rate)
                    MinLows300       = parsed.GetResult(Min_Lows_300,       defaultValue = d.Intraday.MinLows300)
                    MaxRngFront      = parsed.GetResult(Max_Rng_Front,      defaultValue = d.Intraday.MaxRngFront)
                    MinDv0945Tape    = parsed.GetResult(Min_Dv_0945_Tape,   defaultValue = d.Intraday.MinDv0945Tape)
                    VolStopRatio     = parsed.GetResult(Vol_Stop_Ratio,     defaultValue = d.Intraday.VolStopRatio)
                    TcStopRatio      = parsed.GetResult(Tc_Stop_Ratio,      defaultValue = d.Intraday.TcStopRatio)
                    SpeedStopPct     = parsed.GetResult(Speed_Stop_Pct,     defaultValue = d.Intraday.SpeedStopPct)
                    MaxConcurrent    = parsed.GetResult(Max_Concurrent,     defaultValue = d.Intraday.MaxConcurrent)
                    EntryStartSec    = parsed.GetResult(Entry_Start_Sec,    defaultValue = d.Intraday.EntryStartSec)
                    EntryEndSec      = parsed.GetResult(Entry_End_Sec,      defaultValue = d.Intraday.EntryEndSec) }
            MinDv0945 = parsed.GetResult(Min_Dv_0945, defaultValue = d.MinDv0945)
            MinRvol0945 = parsed.GetResult(Min_Rvol_0945, defaultValue = d.MinRvol0945)
            MinPrevClose = parsed.GetResult(Min_Prev_Close, defaultValue = d.MinPrevClose) }

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
    // Channel membership: the engine pre-builds exactly these windows and aliases
    // the entry/exit channel onto them — an off-menu value would otherwise throw
    // invalidArg mid-run, after the candidate query already ran. The entry channel
    // additionally drives the leg machine and the chan_hi/chan_lo snapshots; 30 is
    // excluded (a 30s "flush channel" is below the feature horizon).
    let entryChanSet = [ 60; 120; 300; 600; 1200 ]
    if not (List.contains cfg.Intraday.EntryChannelBars entryChanSet) then
        eprintfn "FATAL: --entry-channel-bars %d — must be one of %A." cfg.Intraday.EntryChannelBars entryChanSet
        exit 1
    let exitChanSet = [ 30; 60; 120; 300; 600; 1200 ]
    if not (List.contains cfg.Intraday.ExitChannelBars exitChanSet) then
        eprintfn "FATAL: --exit-channel-bars %d — must be one of %A." cfg.Intraday.ExitChannelBars exitChanSet
        exit 1

    let ic = cfg.Intraday
    let hhmmss s = sprintf "%02d:%02d:%02d" (s / 3600) (s % 3600 / 60) (s % 60)
    printfn "FlushFader — 1s LONG mean reversion (DipRiderV6 semantics on the SurgeRider engine)"
    printfn "  db          = %s" dbPath
    printfn "  candidates  = %s%s" Backtest.candidateTable
        (match Environment.GetEnvironmentVariable "FF_CANDIDATE_TABLE" with
         | null | "" -> "  (default)" | _ -> "  [FF_CANDIDATE_TABLE override]")
    printfn "  1s bars     = %s" secDir
    printfn "  range       = %O .. %O" startDate endDate
    printfn "  universe    = dv_0945_tape >= $%.1fM (⭐ 1s-bar-native, honest dollars — S35)%s%s%s" (ic.MinDv0945Tape / 1e6)
        (if cfg.MinDv0945 > 0.0 then sprintf "   AND 💀deprecated dv_0945 >= $%.1fM" (cfg.MinDv0945 / 1e6) else "")
        (if cfg.MinRvol0945 > 0.0 then sprintf "   AND rvol_0945_honest >= %.1f  [IN-PLAY PRE-FILTER]" cfg.MinRvol0945
         else "   [rvol_0945_honest RECORDED, not gated]")
        (if cfg.MinPrevClose > 0.0 then sprintf "   AND prev raw close >= $%.2f" cfg.MinPrevClose else "")
    printfn "  ENTRY       = vwap < prior %d-bar MIN (strict; new ~20m low)   AND dv60 >= $%.0fk AND tc60 >= %.0f   (fill: NEXT bar vwap)"
        ic.EntryChannelBars (ic.DvFloor60 / 1e3) ic.TcFloor60
    printfn "  EXIT        = vwap > prior %d-bar MAX (strict; ~5m high)  |  MOC   (fill: NEXT bar vwap)" ic.ExitChannelBars
    printfn "  accept stops= new %d-bar low on vr>=%s | tcr>=%s | 1m pace < %s   ⭐ price-acceptance (NO level stop — V6: destructive)"
        ic.EntryChannelBars
        (if Double.IsPositiveInfinity ic.VolStopRatio then "off" else sprintf "%.0fx" ic.VolStopRatio)
        (if Double.IsPositiveInfinity ic.TcStopRatio then "off" else sprintf "%.0fx" ic.TcStopRatio)
        (if ic.SpeedStopPct >= 0.0 then "off" else sprintf "%.1f%%" (ic.SpeedStopPct * 100.0))
    printfn "  leg         = arm on first new low, reset on new %d-bar high (+ 5m/10m-reset twins RECORDED — S38e; books built post-hoc by mc-replay)"
        ic.EntryChannelBars
    printfn "  volat band  = volat_20m ∈ [%s, %s) bp/30s"
        (if ic.MinVolat20m <= 0.0 then "0=off" else sprintf "%.0f" (ic.MinVolat20m * 1e4))
        (if Double.IsPositiveInfinity ic.MaxVolat20m then "inf" else sprintf "%.0f" (ic.MaxVolat20m * 1e4))
    printfn "  SPEC v1.6   = speed %s | K ∈ [%s, %s] | eff20 ∈ [%s, %s) COLD FAILS | |eff10| >= %s | dist-20m-hi ∈ [%s, %s) | vol10rate >= %s | lows300 >= %s | rngfront < %s"
        (if ic.MaxSpeed1m >= 0.0 then "off" else sprintf "< %.0f%%/1m" (ic.MaxSpeed1m * 100.0))
        (if ic.KBandLo <= 0 then "off" else string ic.KBandLo)
        (if ic.KBandHi <= 0 then "off" else string ic.KBandHi)
        (if Double.IsNegativeInfinity ic.Eff20Lo then "off" else sprintf "%.2f" ic.Eff20Lo)
        (if Double.IsPositiveInfinity ic.Eff20Hi then "off" else sprintf "%.2f" ic.Eff20Hi)
        (if ic.MinAbsEff10m <= 0.0 then "off" else sprintf "%.2f" ic.MinAbsEff10m)
        (if Double.IsNegativeInfinity ic.DistHiLo then "off" else sprintf "%.0f%%" (ic.DistHiLo * 100.0))
        (if ic.DistHiHi >= 0.0 then "off" else sprintf "%.0f%%" (ic.DistHiHi * 100.0))
        (if ic.MinVol10Rate <= 0.0 then "off" else sprintf "%.2fx" ic.MinVol10Rate)
        (if ic.MinLows300 <= 0 then "off" else string ic.MinLows300)
        (if Double.IsPositiveInfinity ic.MaxRngFront then "off" else sprintf "%.2f" ic.MaxRngFront)
    printfn "  entry window= %s-%s ET   features fold from %s ET" (hhmmss ic.EntryStartSec) (hhmmss ic.EntryEndSec) (hhmmss ic.SessionStartSec)
    if ic.MaxConcurrent <= 0 then
        printfn "  mode        = ⭐ SAMPLER (mc=0 unlimited → every new low opens another trip; averages down)"
        printfn "                PF/net below are ATTRIBUTION ONLY, not portfolio numbers."
    else
        printfn "  mode        = BOOK (max-concurrent %d)" ic.MaxConcurrent

    let sw = Stopwatch.StartNew()
    // per-tkd callback, printed at most once a second (the drain fires it
    // thousands of times a minute on light names)
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
    printfn "  win rate   = %.1f%%  (%d / %d)"
        (100.0 * float stats.Wins / float (max 1L stats.Total)) stats.Wins stats.Total
    printfn "  net P&L    = %s   ⚠ costs not modeled" ((stats.GrossWin - stats.GrossLoss).ToString "N0")
    printfn "  PF         = %.3f%s" pf (if ic.MaxConcurrent <= 0 then "   [ATTRIBUTION ONLY — mc=0]" else "")
    printfn "  wrote      = %s/trips_p*.parquet" outDir
    0
