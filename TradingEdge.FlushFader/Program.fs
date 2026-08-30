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
    // ----- SPEC v1.2 gates (defaults = the S18 production stack) -----
    | Max_Speed_1m of float
    | K_Band_Lo of int
    | K_Band_Hi of int
    | Abs_Eff20_Lo of float
    | Abs_Eff20_Hi of float
    | Min_Abs_Eff_10m of float
    | Min_Eff_9ema_10m of float
    | Ssf_Lo of float
    | Ssf_Hi of float
    | Max_Dist_Leg_Vwap of float
    | Min_Vol10_Rate of float
    | Min_Lows_300 of int
    | Min_Lows_180 of int
    | Max_Rng_Front of float
    | Min_Accel_1020 of float
    | Max_Slope_20m of float
    | Min_Slope_5m of float
    | Min_Dv_0945_Tape of float
    // ----- price-acceptance stops -----
    | Vol_Stop_Ratio of float
    | Tc_Stop_Ratio of float
    | Speed_Stop_Pct of float
    // ----- universe -----
    | Min_Dv_0945 of float
    | Min_Rvol_0945 of float
    | Min_Prev_Close of float
    | Min_Barnum of int
    | Max_Dist_1m of float
    | Halt_Min_Run of int
    | Halt_Min_Rng_300 of float
    | Halt_Max_Pre_Gap_60 of int
    | Min_R_Since_Flow of float
    | Max_Z_20m of float
    | Cascade_Halt_Count of int
    | Cascade_Window_Sec of int
    | Reopen_Block_Sec of int
    | Base_Run
    // ----- sampler vs book -----
    | Max_Concurrent of int
    | Workers of int
    // ----- timing -----
    | Entry_Start_Sec of int
    | Entry_End_Sec of int
    | Entry_End_Sec_Short of int
    | Moc_Sec of int
    | Moc_Sec_Short of int
    | Exit_Channel_Bars_After_Hours of int
    | After_Hours_Sec of int

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
            | Max_Speed_1m _ -> "⭐ SPEC v1.2: flush speed gate — vwap/vwap_60_prev - 1 < this at the signal. Default -0.02. 0 = off."
            | K_Band_Lo _ -> "⭐ SPEC v1.2: lows_since_first_low >= this (K band floor — THE 2022 fix). Default 26. 0 = off."
            | K_Band_Hi _ -> "⭐ SPEC v1.2: lows_since_first_low <= this (K band ceiling). Default 50. 0 = off."
            | Abs_Eff20_Lo _ -> "⭐ S40i redesign: |eff_20m| >= this (abs band, mirrors |eff10|). Default 0.3. <= 0 = off."
            | Abs_Eff20_Hi _ -> "⭐ S40i redesign: |eff_20m| < this. Default 0.5. Infinity = off."
            | Min_Abs_Eff_10m _ -> "⭐ SPEC v1.2: |eff_10m| >= this (no flat 10m tape). Default 0.15. 0 = off."
            | Min_Eff_9ema_10m _ -> "⭐ SPEC v2.7 (S43al): eff_9ema_10m >= this — the whipsaw knife. Default -0.10. ⚠ OFF = -infinity ONLY; 0 is a LIVE bound."
            | Ssf_Lo _ -> "⭐ SPEC v2.2 (S41c/d): ols_slope_since_flow x 6e5 >= this bp/min (no vertical crash). Default -375. -Infinity = off."
            | Ssf_Hi _ -> "⭐ SPEC v2.2 (S41c/d): ols_slope_since_flow x 6e5 < this bp/min (no shallow drift). Default -25. >= 0 = off."
            | Max_Dist_Leg_Vwap _ -> "⭐ SPEC v2.2 (S41d): vwap/(dv_leg/vol_leg) - 1 < this — stretched below the leg's OWN vwap. Default -0.03. >= 0 = off."
            | Min_Vol10_Rate _ -> "⭐ SPEC v1.2: (vol_10/10)/(vol_60/60) >= this (S17 last-10s volume-rate floor — no quiet-tail drift-downs). Default 0.75. 0 = off."
            | Min_Lows_300 _ -> "⭐ SPEC v1.4: lows_since_first_low_300 >= this — kills the FAST-CHASE re-entry (5m bounce without a 20m leg reset re-signals in seconds; PF 0.11 on the A++ cell). Default 6. 0 = off."
            | Min_Lows_180 _ -> "⭐ SPEC v3.0 (S43cl): lows_since_first_low_180 >= this — the 3m freshness floor (net-free at 3: mc=1 slots recycle). Default 3. 0 = off."
            | Max_Rng_Front _ -> "⭐ SPEC v1.5: rng_300/rng_20m < this — reject the PURE CLIFF (whole 20m range in the last 5m; monotone-worst at mc=1). Default 0.8. Infinity = off."
            | Min_Accel_1020 _ -> "⭐ SPEC v1.7: (slope_10m - slope_20m)*6e5 >= this bp/min — reject the late-accelerating bleed band. Default -80. -Infinity = off."
            | Max_Slope_20m _ -> "⭐ SPEC v1.7: slope_20m*6e5 < this bp/min — the L-shape insurance (flat-slope late cliff). Default -10. >= 0 = off."
            | Min_Slope_5m _ -> "⭐ SPEC v1.7: slope_5m*6e5 >= this bp/min — no vertical last-5m collapse (the <-400 slice = 0.706/36.4%% under the other gates). Default -400. -Infinity = off."
            | Min_Dv_0945_Tape _ -> "Tape-native dv_0945 floor: Σ vwap·volume over OUR 1s bars strictly before 09:45 >= this (live-scanner-consistent; honest dollars). Default 0 = record-first. Pair with --min-dv-0945 0 when replacing the candidate-table gate."
            | Vol_Stop_Ratio _ -> "PRICE-ACCEPTANCE STOP: exit holders when a NEW entry-channel low prints on (vol_60/60)/(vol_1200/1200) >= this. Default Infinity = OFF (S16 A/B: stops gut the book). e.g. 8 to arm."
            | Tc_Stop_Ratio _ -> "PRICE-ACCEPTANCE STOP: same on the trade-count ratio. Default Infinity = OFF (S16)."
            | Speed_Stop_Pct _ -> "PRICE-ACCEPTANCE STOP: exit holders when a NEW entry-channel low prints at vwap/vwap_60_prev-1 < this (the flush continuing at pace). Default 0 = OFF (S16: fired on 93.7% of spec book at median 2s). e.g. -0.01 to arm."
            | Min_Dv_0945 _ -> "💀 DEPRECATED (S35): the candidate column = real dollars × adj_ratio (future-split-dependent — 20% of the universe was inflated in). Default 0 = off. THE floor is --min-dv-0945-tape."
            | Min_Rvol_0945 _ -> "Optional in-play universe pre-filter: rvol_0945_honest >= this (premkt-incl vol thru 09:45 / prior-20d avg; LIVE-SAFE at 09:45). Default 0 = off (sampler breadth)."
            | Min_Prev_Close _ -> "Universe gate: PRIOR day's close in day-D RAW scale >= this (the `close_m1` column — already converted, no rescale; knowable BEFORE the open). Default 0 = off. 2 = the >=$2 universe (sub-$1 priced out on every EU-accessible broker)."
            | Min_Barnum _ -> "⭐ S40e episode warmup: candidate barnum (prior-only ROW_NUMBER, live-knowable) >= this. Default 22 = cut the IPO/early-listing slice (below-book for the LONG book; reserved for a future short system). 0 = off. Column-guarded (legacy tables skip it)."
            | Max_Dist_1m _ -> "⭐ SPEC v1.9 (S40g): vwap/hi_60 - 1 < this — dist from the 1m HIGH; conjunction with the speed gate (the shallow slice above -2%% = slot thieves). Default -0.02. >= 0 = off."
            | Halt_Min_Run _ -> "⭐ S40x halt detector (record-only): a tradeless run >= this many seconds can classify as a HALT. Default 58."
            | Halt_Min_Rng_300 _ -> "⭐ S40x: pre-hole 5m range (ln hi/lo) >= this for the run to classify as a halt (the LULD trigger state). Default 0.04."
            | Halt_Max_Pre_Gap_60 _ -> "⭐ S40x: pre-hole ADJUSTED 1m gap < this (tape continuous up to the stop). Default 2."
            | Min_R_Since_Flow _ -> "⭐ SPEC v2.1 (S40y): ols_r_since_flow >= this — reject the PERFECT-LINE flush (the falling-knife quantifier; < -0.95 = 1.22). Default -0.95. <= -1 = off."
            | Max_Z_20m _ -> "⭐ SPEC v2.3 (S41r): 20m vw-sigma z (ln space) < this — trims the weak dip [-1.5,-1). Default -1.5. >= 0 = off."
            | Cascade_Halt_Count _ -> "⭐ SPEC v2.4 (S42n): cascade-knife gate — reject a signal iff halts_today >= this AND secs since resume < --cascade-window-sec. Default 3. 0 = off."
            | Reopen_Block_Sec _ -> "⭐ SPEC v2.5 (S42t): reject any signal within this many seconds of ANY resume — the first 1-2 halts INCLUDED. Default 120. 0 = off."
            | Cascade_Window_Sec _ -> "the cascade gate's post-resume window in seconds (default 1200 = 20m)."
            | Base_Run -> "⭐ THE BASE PASS: turn EVERY spec gate OFF in one flag (speed/d1m/ssf/dlv/rflow/z20/K/eff20/eff10/vol10rate/lows300/rngfront/accel/slope20/slope5/dv0945tape). Keeps the SIGNAL definition (volat >= 40bp, 20m low, channel warm, barnum >= 22, entry window). Explicit gate flags still override. Replaces the 17-flag canonical base CLI (S42h; a wrong sentinel here once cost 540k trips silently)."
            | Max_Concurrent _ -> "0 (DEFAULT) = the SAMPLER: unlimited concurrent positions — every new low opens another trip, so it AVERAGES DOWN. Removes path dependency (every trip = an independent row) but PF is then ATTRIBUTION, not a portfolio number. 1 = a real book."
            | Workers _ -> "S39h: parallel day-workers (default: cores - 2). Trip SET is identical at any worker count; parquet row order is not."
            | Entry_Start_Sec _ -> "Earliest ET second (since midnight) an entry may fire. Default 35100 = 09:45 — the knowability floor itself (the old 10:00 was a VwapReclaim-era throwback). ⚠ Must be >= 35100."
            | Entry_End_Sec _ -> "Latest ET second an entry may fire. Default 54000 = 15:00 (one hour before the regular close)."
            | Entry_End_Sec_Short _ -> "S43bc: latest ET second an entry may fire on NYSE early-close days (13:00 ET close). Default 43200 = 12:00 — the hour-before-close rule mirrored."
            | Moc_Sec_Short _ -> "S43bx: the same bound on NYSE early-close days. Default 46800 = 13:00. The post-13:00 window there carries a median of 40 traded seconds of 10,800 (0.4%, vs 30.8% on a regular afternoon) and cannot form a 5m high on 88% of ticker-days, so it is treated as closed."
            | Exit_Channel_Bars_After_Hours _ -> "S43bw: a TIGHTER exit channel that engages only after --after-hours-sec, because the channels count PRESENT BARS and post-market tape is sparse (a 300-bar '5m high' spans hours after 16:00). One of {30,60,120,300,600,1200}; 0 = off (default). ⚠ Inert unless --moc-sec is raised past --after-hours-sec."
            | After_Hours_Sec _ -> "S43bw: ET second at which --exit-channel-bars-after-hours takes over. Default 57600 = 16:00."
            | Moc_Sec _ -> "Latest ET second a position may be held; holders force-exit at the first bar >= this. Default 57600 = 16:00. ⭐ ALSO CAPS THE BAR QUERY (Backtest.fs SecReader), so raising it is what lets the post-market tape in at all — e.g. 86399 runs to the tape's end. Entries are unaffected (--entry-end-sec)."

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
    // ⭐ --base-run: the per-gate OFF sentinels (see each config field's doc).
    let baseRun = parsed.Contains Base_Run
    let dI =
        if not baseRun then d.Intraday
        else
            { d.Intraday with
                MaxSpeed1m = 0.0; MaxDist1mHi = 0.0
                SsfLoBpm = Double.NegativeInfinity; SsfHiBpm = 0.0
                MaxDistLegVwap = 0.0; MinRSinceFlow = -1.0; MaxZ20m = 0.0
                KBandLo = 0; KBandHi = 0
                AbsEff20Lo = 0.0; AbsEff20Hi = Double.PositiveInfinity
                MinAbsEff10m = 0.0; MinVol10Rate = 0.0; MinLows300 = 0; MinLows180 = 0
                MinEff9Ema10m = Double.NegativeInfinity   // ⚠ off is -inf, NOT 0 (a live bound)
                MaxRngFront = Double.PositiveInfinity
                MinAccel1020Bpm = Double.NegativeInfinity
                MaxSlope20Bpm = 0.0; MinSlope5Bpm = Double.NegativeInfinity
                MinDv0945Tape = 0.0
                // ⚠ S42t BUGFIX: these two were MISSING here — the S42n edit that was
                // meant to add CascadeHaltCount silently no-op'd (wrong indentation, no
                // assert), so --base-run carried a LIVE cascade gate. No data was
                // affected (base_v15 predates v2.4; every bake since passed flags
                // explicitly), but a base run is only a base run if EVERY gate is off.
                CascadeHaltCount = 0; ReopenBlockSec = 0 }
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
                    MaxSpeed1m       = parsed.GetResult(Max_Speed_1m,       defaultValue = dI.MaxSpeed1m)
                    MaxDist1mHi      = parsed.GetResult(Max_Dist_1m,        defaultValue = dI.MaxDist1mHi)
                    SsfLoBpm         = parsed.GetResult(Ssf_Lo,             defaultValue = dI.SsfLoBpm)
                    SsfHiBpm         = parsed.GetResult(Ssf_Hi,             defaultValue = dI.SsfHiBpm)
                    MaxDistLegVwap   = parsed.GetResult(Max_Dist_Leg_Vwap,  defaultValue = dI.MaxDistLegVwap)
                    HaltMinRunSec    = parsed.GetResult(Halt_Min_Run,       defaultValue = d.Intraday.HaltMinRunSec)
                    HaltMinRng300    = parsed.GetResult(Halt_Min_Rng_300,   defaultValue = d.Intraday.HaltMinRng300)
                    HaltMaxPreGap60  = parsed.GetResult(Halt_Max_Pre_Gap_60, defaultValue = d.Intraday.HaltMaxPreGap60)
                    MinRSinceFlow    = parsed.GetResult(Min_R_Since_Flow,    defaultValue = dI.MinRSinceFlow)
                    MaxZ20m          = parsed.GetResult(Max_Z_20m,          defaultValue = dI.MaxZ20m)
                    CascadeHaltCount = parsed.GetResult(Cascade_Halt_Count, defaultValue = dI.CascadeHaltCount)
                    CascadeWindowSec = parsed.GetResult(Cascade_Window_Sec, defaultValue = dI.CascadeWindowSec)
                    ReopenBlockSec   = parsed.GetResult(Reopen_Block_Sec,   defaultValue = dI.ReopenBlockSec)
                    KBandLo          = parsed.GetResult(K_Band_Lo,          defaultValue = dI.KBandLo)
                    KBandHi          = parsed.GetResult(K_Band_Hi,          defaultValue = dI.KBandHi)
                    AbsEff20Lo       = parsed.GetResult(Abs_Eff20_Lo,       defaultValue = dI.AbsEff20Lo)
                    AbsEff20Hi       = parsed.GetResult(Abs_Eff20_Hi,       defaultValue = dI.AbsEff20Hi)
                    MinAbsEff10m     = parsed.GetResult(Min_Abs_Eff_10m,    defaultValue = dI.MinAbsEff10m)
                    MinEff9Ema10m    = parsed.GetResult(Min_Eff_9ema_10m,   defaultValue = dI.MinEff9Ema10m)
                    MinVol10Rate     = parsed.GetResult(Min_Vol10_Rate,     defaultValue = dI.MinVol10Rate)
                    MinLows300       = parsed.GetResult(Min_Lows_300,       defaultValue = dI.MinLows300)
                    MinLows180       = parsed.GetResult(Min_Lows_180,       defaultValue = dI.MinLows180)
                    MaxRngFront      = parsed.GetResult(Max_Rng_Front,      defaultValue = dI.MaxRngFront)
                    MinAccel1020Bpm  = parsed.GetResult(Min_Accel_1020,     defaultValue = dI.MinAccel1020Bpm)
                    MaxSlope20Bpm    = parsed.GetResult(Max_Slope_20m,      defaultValue = dI.MaxSlope20Bpm)
                    MinSlope5Bpm     = parsed.GetResult(Min_Slope_5m,       defaultValue = dI.MinSlope5Bpm)
                    MinDv0945Tape    = parsed.GetResult(Min_Dv_0945_Tape,   defaultValue = dI.MinDv0945Tape)
                    VolStopRatio     = parsed.GetResult(Vol_Stop_Ratio,     defaultValue = d.Intraday.VolStopRatio)
                    TcStopRatio      = parsed.GetResult(Tc_Stop_Ratio,      defaultValue = d.Intraday.TcStopRatio)
                    SpeedStopPct     = parsed.GetResult(Speed_Stop_Pct,     defaultValue = d.Intraday.SpeedStopPct)
                    MaxConcurrent    = parsed.GetResult(Max_Concurrent,     defaultValue = d.Intraday.MaxConcurrent)
                    EntryStartSec    = parsed.GetResult(Entry_Start_Sec,    defaultValue = d.Intraday.EntryStartSec)
                    EntryEndSec      = parsed.GetResult(Entry_End_Sec,      defaultValue = d.Intraday.EntryEndSec)
                    EntryEndSecShort = parsed.GetResult(Entry_End_Sec_Short, defaultValue = d.Intraday.EntryEndSecShort)
                    MocSec           = parsed.GetResult(Moc_Sec,             defaultValue = d.Intraday.MocSec)
                    MocSecShort      = parsed.GetResult(Moc_Sec_Short,       defaultValue = d.Intraday.MocSecShort)
                    ExitChannelBarsAfterHours =
                        parsed.GetResult(Exit_Channel_Bars_After_Hours, defaultValue = d.Intraday.ExitChannelBarsAfterHours)
                    AfterHoursSec    = parsed.GetResult(After_Hours_Sec,     defaultValue = d.Intraday.AfterHoursSec) }
            MinDv0945 = parsed.GetResult(Min_Dv_0945, defaultValue = d.MinDv0945)
            MinRvol0945 = parsed.GetResult(Min_Rvol_0945, defaultValue = d.MinRvol0945)
            MinPrevClose = parsed.GetResult(Min_Prev_Close, defaultValue = d.MinPrevClose)
            MinBarnum = parsed.GetResult(Min_Barnum, defaultValue = d.MinBarnum)
            Workers = parsed.GetResult(Workers, defaultValue = d.Workers) }

    // ⚠ KNOWABILITY GUARD (docs/lookahead_protocol.md R4). The universe is GATED on
    // dv_0945 and every trip RECORDS dv_0945 / rvol_0945_honest — all three are only
    // determined at 09:45 ET. Legal ONLY because entries start at/after 09:45; lower
    // the window and the universe selection itself becomes conditioned on the future —
    // the exact bug class that killed three systems on 2026-07-16. Fail loudly.
    // S43bw: the after-hours channel is a SELECTION over the six maintained windows —
    // an unlisted value would silently fall back to the RTH channel and the run would
    // look like it tested something it did not.
    if not (List.contains cfg.Intraday.ExitChannelBarsAfterHours [0; 30; 60; 120; 300; 600; 1200]) then
        eprintfn "FATAL: --exit-channel-bars-after-hours %d is not one of {0,30,60,120,300,600,1200}."
                 cfg.Intraday.ExitChannelBarsAfterHours
        exit 1
    if cfg.Intraday.ExitChannelBarsAfterHours > 0 && cfg.Intraday.MocSec <= cfg.Intraday.AfterHoursSec then
        eprintfn "FATAL: --exit-channel-bars-after-hours is set but --moc-sec %d <= --after-hours-sec %d,"
                 cfg.Intraday.MocSec cfg.Intraday.AfterHoursSec
        eprintfn "  so no bar can ever reach the tighter channel. Raise --moc-sec (e.g. 72000 = 20:00)."
        exit 1
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
    printfn "  EXIT        = vwap > prior %d-bar MAX (strict; ~5m high)  |  else NEXT OPEN   (fill: NEXT bar vwap)" ic.ExitChannelBars
    printfn "  accept stops= new %d-bar low on vr>=%s | tcr>=%s | 1m pace < %s   ⭐ price-acceptance (NO level stop — V6: destructive)"
        ic.EntryChannelBars
        (if Double.IsPositiveInfinity ic.VolStopRatio then "off" else sprintf "%.0fx" ic.VolStopRatio)
        (if Double.IsPositiveInfinity ic.TcStopRatio then "off" else sprintf "%.0fx" ic.TcStopRatio)
        (if ic.SpeedStopPct >= 0.0 then "off" else sprintf "%.1f%%" (ic.SpeedStopPct * 100.0))
    printfn "  leg         = arm on first new low, reset on new %d-bar high (+ 5m/10m-reset twins RECORDED — S38e; books built post-hoc by mc-replay)"
        ic.EntryChannelBars
    printfn "  halt detect = run >= %ds AND pre-hole 5m rng >= %.1f%% AND pre-hole adj 1m gap < %d   (feeds the cascade gate)"
        ic.HaltMinRunSec (ic.HaltMinRng300 * 100.0) ic.HaltMaxPreGap60
    printfn "  volat band  = volat_20m ∈ [%s, %s) bp/30s"
        (if ic.MinVolat20m <= 0.0 then "0=off" else sprintf "%.0f" (ic.MinVolat20m * 1e4))
        (if Double.IsPositiveInfinity ic.MaxVolat20m then "inf" else sprintf "%.0f" (ic.MaxVolat20m * 1e4))
    (if parsed.Contains Base_Run then printfn "  mode        = ⭐ BASE RUN — every spec gate OFF (signal definition only)")
    printfn "  SPEC v3.0   = speed %s | d1m %s | ssf ∈ [%s, %s) bp/m | dlv %s | rflow >= %s | z20 < %s | cascade %s | K ∈ [%s, %s] | |eff20| ∈ [%s, %s) | |eff10| >= %s | eff9ema10 >= %s | vol10rate >= %s | lows300 >= %s | lows180 >= %s | rngfront < %s | accel1020 >= %s | slope20 < %s | slope5 >= %s"
        (if ic.MaxSpeed1m >= 0.0 then "off" else sprintf "< %.0f%%/1m" (ic.MaxSpeed1m * 100.0))
        (if ic.MaxDist1mHi >= 0.0 then "off" else sprintf "< %.0f%%" (ic.MaxDist1mHi * 100.0))
        (if Double.IsNegativeInfinity ic.SsfLoBpm then "off" else sprintf "%.0f" ic.SsfLoBpm)
        (if ic.SsfHiBpm >= 0.0 then "off" else sprintf "%.0f" ic.SsfHiBpm)
        (if ic.MaxDistLegVwap >= 0.0 then "off" else sprintf "< %.0f%%" (ic.MaxDistLegVwap * 100.0))
        (if ic.MinRSinceFlow <= -1.0 then "off" else sprintf "%.2f" ic.MinRSinceFlow)
        (if ic.MaxZ20m >= 0.0 then "off" else sprintf "%.1fσ" ic.MaxZ20m)
        // the gate reads as a case analysis on the halt count — print it that way,
        // and print each rule's OFF state as "off" (a raw 0 renders as "ht>=0" /
        // "wait 0s", which reads like a live rule; banners are how gates get verified)
        (if ic.CascadeHaltCount <= 0 && ic.ReopenBlockSec <= 0 then "off"
         else
             sprintf "%s, %s"
                 (if ic.ReopenBlockSec <= 0 then "ht>=1 off"
                  else sprintf "ht>=1 wait %ds" ic.ReopenBlockSec)
                 (if ic.CascadeHaltCount <= 0 then "serial-breaker off"
                  else sprintf "ht>=%d wait %ds" ic.CascadeHaltCount ic.CascadeWindowSec))
        (if ic.KBandLo <= 0 then "off" else string ic.KBandLo)
        (if ic.KBandHi <= 0 then "off" else string ic.KBandHi)
        (if ic.AbsEff20Lo <= 0.0 then "off" else sprintf "%.2f" ic.AbsEff20Lo)
        (if Double.IsPositiveInfinity ic.AbsEff20Hi then "off" else sprintf "%.2f" ic.AbsEff20Hi)
        (if ic.MinAbsEff10m <= 0.0 then "off" else sprintf "%.2f" ic.MinAbsEff10m)
        (if Double.IsNegativeInfinity ic.MinEff9Ema10m then "off" else sprintf "%+.2f" ic.MinEff9Ema10m)
        (if ic.MinVol10Rate <= 0.0 then "off" else sprintf "%.2fx" ic.MinVol10Rate)
        (if ic.MinLows300 <= 0 then "off" else string ic.MinLows300)
        (if ic.MinLows180 <= 0 then "off" else string ic.MinLows180)
        (if Double.IsPositiveInfinity ic.MaxRngFront then "off" else sprintf "%.2f" ic.MaxRngFront)
        (if Double.IsNegativeInfinity ic.MinAccel1020Bpm then "off" else sprintf "%.0fbp/m" ic.MinAccel1020Bpm)
        (if ic.MaxSlope20Bpm >= 0.0 then "off" else sprintf "%.0fbp/m" ic.MaxSlope20Bpm)
        (if Double.IsNegativeInfinity ic.MinSlope5Bpm then "off" else sprintf "%.0fbp/m" ic.MinSlope5Bpm)
    printfn "  entry window= %s-%s ET   features fold from %s ET" (hhmmss ic.EntryStartSec) (hhmmss ic.EntryEndSec) (hhmmss ic.SessionStartSec)
    printfn "  close       = %s ET (%s on early closes) -> anything still open exits at the NEXT SESSION'S OPEN (S43bw/S43bx)"
            (hhmmss ic.MocSec) (hhmmss ic.MocSecShort)
    if ic.ExitChannelBarsAfterHours > 0 then
        printfn "  ⚠ after-hours target = %d bars from %s ET" ic.ExitChannelBarsAfterHours (hhmmss ic.AfterHoursSec)
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
