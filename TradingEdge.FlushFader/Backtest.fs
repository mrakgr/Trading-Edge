module TradingEdge.FlushFader.Backtest

open System
open System.Collections.Generic
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control
open DuckDB.NET.Data
open TradingEdge.FlushFader.Intraday

// ===========================================================================
// FlushFader backtest wiring (template: DipRiderV6/Backtest.fs via SurgeRider).
//
// Pipeline 1 is the same pure-SQL read of `diprider_v6_candidate` (built by
// scripts/equity/build_diprider_v5_candidate.fsx — the leak-audited universe:
// dv_0945 floor, no avgvol20 anywhere). Pipeline 2 streams each candidate
// day's PRESENT 1-second bars from data/intraday_1s_slim/ into IntradaySystem.
//
// ⭐ OUTPUT IS PARQUET, NOT CSV (user, 2026-07-23): at 1s granularity the
// sampler emits orders of magnitude more trips than the 1m systems — a CSV
// would be multi-GB and every post-hoc query would pay a full text parse.
// Trips STREAM into an in-memory DuckDB staging table via the appender (the
// TradesDownload.fs writeTradesToParquet pattern) and rotate to zstd part
// files every RowsPerPart rows, so neither the CLR heap nor the staging table
// ever holds the full book. Post-hoc: read_parquet('<outDir>/*.parquet').
// NaN features are written as NULL — SQL aggregates then skip them natively.
// ===========================================================================

/// FlushFader config = the intraday engine knobs + notional + the daily floor.
type Config =
    { Intraday: IntradayConfig
      Notional: float
      /// ⭐ The daily in-play floor: minimum 09:30-09:45 dollar volume. Default $3M —
      /// DipRiderV6 F14's MANDATORY floor: below it the apparent PF rise is a
      /// penny-stock artifact ($1.13 median entry at <$250k). The per-bar
      /// DvFloor60/TcFloor60 gates do the second-by-second version of the same job.
      MinDv0945: float
      /// ⭐ Optional in-play universe pre-filter: rvol_0945_honest >= this in the candidate
      /// SELECT. 0 = off (THE SAMPLER DEFAULT — rvol stays recorded-not-gated for breadth).
      /// Knowability: same 09:45 class as dv_0945, legal for EntryStartSec >= 35100. Use for
      /// focused sweeps (e.g. --min-rvol-0945 10 narrows the universe ~50x and a full run
      /// drops to seconds per day).
      MinRvol0945: float
      /// ⭐ Universe gate on the PRIOR day's close in day-D's raw (post-split) scale —
      /// close_m1 >= this — already in day D's RAW scale, no rescale. Knowable
      /// BEFORE the open (D-1 close),
      /// unlike entry price. 0 = off (record-first). 2.0 = the ">=$2 stocks" universe
      /// (sub-$1 is priced out on every EU-accessible broker route).
      MinPrevClose: float
      /// ⭐ S40e (user, 2026-08-01): episode warmup — candidate barnum (ROW_NUMBER over
      /// the episode, prior-only, live-knowable) >= this. Default 22 = cut the
      /// IPO/early-listing slice (measured BELOW-BOOK for the LONG book: 1,476 trips
      /// @ 1.425 mc=0, −0.040 at mc=1; the slice stays IN mr_candidate_1s for the
      /// future SHORT system). 0 = off. Applied only when the column exists
      /// (legacy tables predate it and are warmed by construction).
      MinBarnum: int
      /// ⭐ S39h: day-worker parallelism. Days are the natural isolation unit
      /// (fresh IntradaySystems, no cross-day state); the trip SET is identical
      /// at any worker count, only parquet row ORDER varies.
      Workers: int }

/// The sampler defaults (mc = 0). Every gate here is a HARD gate; everything
/// else is recorded and sliced post-hoc over the parquet.
let defaultConfig =
    { Intraday =
        { EntryChannelBars = 1200       // ⭐ the ~20m flush channel: entry on its new LOW, leg reset
                                        // on its new HIGH. {60,120,300,600,1200}.
          ExitChannelBars  = 300        // ⭐ the ~5m reversion target (V6 F16's direction).
                                        // {30,60,120,300,600,1200}.
          ExitChannelBarsAfterHours = 0 // OFF — one target all session (see Intraday.fs).
          AfterHoursSec    = 57600      // 16:00, where the tighter target would take over.
          DvFloor60        = 100_000.0  // >= $100k traded over the last 60 present bars at the signal
          TcFloor60        = 60.0       // >= 60 trades over the same window (1/sec — kills the
                                        // block-print-only tape)
          // (MinAbsEff20m deleted — S40i: AbsEff20Lo is the abs floor)
          MinVolat20m      = 0.004      // ⭐ SPEC v1.2 (S18): the 40bp volatility floor
          MaxVolat20m      = Double.PositiveInfinity
          // ⭐ SPEC v1.2 GATES (S18, baked 2026-07-29). Defaults = the production
          // stack; disable individually for sweeps (see IntradayConfig for the
          // off-conventions). Formulas identical to the recorded columns.
          MaxSpeed1m       = -0.02      // flush speed < -2%/1m
          MaxDist1mHi      = -0.02      // ⭐ SPEC v1.9 (S40g): dist from 1m HIGH < -2% — the 1m
                                        // conjunction (fast last minute AND a real 1m leg);
                                        // first challenger to win at BOTH mc levels
          HaltMinRunSec    = 58         // ⭐ S40x halt detector (user design; record-only)
          HaltMinRng300    = 0.04
          HaltMaxPreGap60  = 4        // ⭐ SPEC v2.6 (user, S42y): was 2. The detector must
                                        // agree with the universe it FEEDS — we trade gap_60 < 4,
                                        // so a name at gap_60 = 3 is tradeable but its halts went
                                        // unclassified. Effect is small and all in the right
                                        // direction (S42w): +3.4% halted trips, S-tier cell
                                        // 224 -> 229, halt-band voice 5.86 -> 6.02.
          MinRSinceFlow    = -0.95      // ⭐ SPEC v2.1 (S40y): the falling-knife gate
          CascadeHaltCount = 3          // ⭐ SPEC v2.4 (S42n): the cascade-knife gate — reject
          CascadeWindowSec = 1200       // ht>=3 signals < 20m after the last resume
          ReopenBlockSec   = 120        // ⭐ SPEC v2.5 (user, S42t): nothing in the first
                                        // 2m after ANY resume (first 1-2 halts included)
          MaxZ20m          = -1.5       // ⭐ SPEC v2.3 (user, S41r): 20m vw-sigma z < -1.5 —
                                        // trims the weak dip (429 @ 1.67; edge trim, both
                                        // mc levels improve)
          KBandLo          = 26         // lows_since_first_low ∈ [26, 50] — THE 2022 fix
          KBandHi          = 50
          AbsEff20Lo       = 0.3        // ⭐ S40i redesign: |eff_20m| ∈ [0.3, 0.5) — the exhaustion
          AbsEff20Hi       = 0.5        // band, one |·| convention with |eff10| (sign = non-event)
          MinAbsEff10m     = 0.15       // |eff_10m| >= 0.15 — no flat 10m tape
          MinEff9Ema10m    = -0.10      // ⭐ SPEC v2.7 (S43al): eff_9ema_10m >= -0.10 — the whipsaw
                                        // knife. ⚠ NEGATIVE bound; off = -infinity, NOT 0.
          // ⭐ SPEC v2.2 (user, S41c/d): the LEG-NATIVE PAIR replaces the v2.0
          // dvw < -5% + d20m-high < -10% distance pair (corr 0.946 = one
          // feature twice; ssf x dlv corr 0.075). The v1.8 wall (DistHiLo)
          // and both old fields are DELETED with their gates.
          SsfLoBpm         = -375.0     // slope_since_flow >= -375bp/min (user tightened from
                                        // -400; < -400 = 0.58, [-400,-375) = 0.63)
          SsfHiBpm         = -25.0      // slope_since_flow < -25bp/min ([-25,0) = 1.96, >= 0 = 0.97)
          MaxDistLegVwap   = -0.03      // >= 3% below the leg's own vwap ([-3,0) = 1.15-1.71)
          MinVol10Rate     = 0.75       // last-10s volume rate >= 0.75x the 1m rate (S17/S18)
          MinLows300       = 6          // ⭐ SPEC v1.4: >= 6 lows since the last 5m-high bounce (S38h)
          MaxRngFront      = 0.8        // ⭐ SPEC v1.5: rng_300/rng_20m < 0.8 — no pure cliffs (S38k)
          MinAccel1020Bpm  = -80.0      // ⭐ SPEC v1.7 (S39o/r): accel(10m−20m) >= −80bp/min — reject
                                        // the late-accelerating bleed band [−150,−80); −80 = the
                                        // user's table boundary; all-years sweep at both mc levels
          MaxSlope20Bpm    = -10.0      // ⭐ SPEC v1.7 (user): slope_20m < −10bp/min — L-shape
                                        // insurance (the 64-trip 0.438 flat-slope sliver)
          MinSlope5Bpm     = -400.0     // ⭐ SPEC v1.7 (user, S39s): slope_5m >= −400bp/min — no
                                        // vertical last-5m collapse (66 trips @ 0.706/36.4%)
          MinDv0945Tape    = 3e6        // ⭐ THE universe floor (S35): Σ vwap·vol over OUR 1s
                                        // bars < 09:45, honest dollars — replaces the
                                        // candidate dv_0945 gate (real dollars × adj_ratio,
                                        // future-split-dependent; 20% of universe inflated in)
          // ⭐ price-acceptance stops (user, 2026-07-28): a fresh entry-channel low on
          // >=8x 1m/20m participation, or at <-1%/1m pace, is the market ACCEPTING the
          // lower price — stop out. S2 motivation: >=8x lows = PF 0.59-0.87 at entry.
          VolStopRatio     = infinity   // acceptance stops default OFF — S16 A/B:
          TcStopRatio      = infinity   // speed stop fired on 93.7% of the spec book
          SpeedStopPct     = 0.0        // at median 2s; stopped trades won 70% without it
          MaxConcurrent    = 0          // ⭐ SAMPLER. 1 = a real book.
          SlotBars         = 30
          SessionStartSec  = 34200      // 09:30 — features fold from the RTH open
          EntryStartSec    = 35100      // ⭐ 09:45 — THE knowability floor itself (user 2026-07-29:
                                        // 10:00-13:30 was a VwapReclaim-era throwback; use the full day)
          EntryEndSec      = 54000      // ⭐ 15:00 (S31b/S31c: after it, quality and
                                        // completion-room degrade together; 14:30 rejected)
          EntryEndSecShort = 43200      // ⭐ S43bc: 12:00 on NYSE early-close days — the
                                        // hour-before-close rule mirrored onto the 13:00 close
                                        // (in-sample cost: 3 of 1,231 book trades, all winners)
          MocSec           = 57600      // 16:00
          MocSecShort      = 46800
          // research default: keep the post-exit counterfactual marks
          LiveSlim         = false }    // ⭐ S43bx: 13:00 on NYSE early-close days
      Notional = 10_000.0
      MinDv0945 = 0.0               // 💀 DEPRECATED (S35): the candidate column = real
                                    // dollars × adj_ratio (future-split-dependent).
                                    // The floor now lives in MinDv0945Tape. Column
                                    // still recorded for reference.
      MinRvol0945 = 0.0
      MinPrevClose = 0.0            // record-first (user 2026-07-29): a prev-close gate
                                    // would drop names flushing DOWN through $1 — the $1
                                    // book stays a POST-HOC entry_px cut (S7c fee wall)
      MinBarnum = 22                // ⭐ S40e: cut the early-episode slice (long book only)
      Workers = max 1 (Environment.ProcessorCount - 2) }

/// One candidate (ticker, day) from diprider_v6_candidate — the daily context
/// that rides along on every trip for post-hoc slicing. Forward closes are
/// REPORTED only.
/// ⭐ S43br: every price here is in day D's RAW scale, and each reference day
/// carries BOTH a price and a dividend increment — a return needs both endpoints'
/// scale info when the share count changes:
///     ret(D -> x) = (CloseX + DivX) / CloseD - 1
/// Div* are NEGATIVE for past days (cash was paid between then and D). See
/// docs/price_adjustment.md.
type Candidate =
    { Ticker: string
      Date: DateOnly
      CloseD: float              // RAW close on D — no adjustment, ever
      N: float                   // causal cumulative split multiplier at D
      CloseM1: float             // prior close, in D's raw scale
      DivM1: float
      CloseM3: float             // close 3 trading days back — chg_3d in SQL
      DivM3: float
      CloseP1: float             // forward closes: REPORTED only, lookahead by design
      DivP1: float
      CloseP3: float
      DivP3: float
      CloseP5: float
      DivP5: float
      OpenP1: float              // next session's OPEN, in D's raw scale (S43bq)
      Dv0945: float
      Rvol0945Honest: float }

/// The candidate table: `mr_candidate_1s_v2` (S43br — the CAUSAL rebuild; 1s-tape-native,
/// dv_0945_tape >= $2M x n_bars_1s >= 200, 2016+) unless overridden via FF_CANDIDATE_TABLE.
/// ⚠ The legacy `mr_candidate_1s` will NOT work: its daily-context columns are
/// back-adjusted and differently named (day_close/adj_ratio/prev_adj_close/close_fwd_*).
/// (Research: run a
/// breakdown against a different universe, e.g. the old 1m-gated diprider_v6_candidate or
/// a restricted tkd table). Identifier-only (injection-safe). Fails fast on a bad value.
let candidateTable =
    match Environment.GetEnvironmentVariable "FF_CANDIDATE_TABLE" with
    | null | "" -> "mr_candidate_1s_v2"
    | t when t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') -> t
    | bad -> failwithf "Invalid FF_CANDIDATE_TABLE %A (identifier chars only)" bad

/// ⭐ Public since 2026-08-18: the live scanner reads the SAME candidate set so a
/// parity run cannot differ by universe. (Live, the universe is computed in-stream
/// at 09:45; this stays the historical/parity path.)
let readCandidates (conn: DuckDBConnection) (startDate: DateOnly) (endDate: DateOnly) (minDv0945: float) (minRvol0945: float) (minPrevClose: float) (minVolat20m: float) (minBarnum: int) : Candidate[] =
    let table = candidateTable
    // ⭐ S39j volat-prepass trim (user): mr_candidate_1s carries max_slot_absr_bp =
    // the day's MAX |30s-slot log return| (engine slot definition, bp). volat_20m
    // is an EmaHlMa = CONVEX COMBINATION of that |r| stream, so volat <= day-max at
    // every bar — a day whose max is under the volat floor can NEVER open the gate
    // and is PROVABLY signal-free: skipping it changes nothing but wall-clock.
    // Derived from the LIVE MinVolat20m (never diverges from the actual gate);
    // 0.01bp margin absorbs the ~1e-13 SQL-vs-engine slot reconstruction noise.
    // NULL max (no completed slot-return all session) => volat never warms => trim.
    // Applied only when the column exists (override tables may predate it).
    let hasPrepassCol =
        use c = conn.CreateCommand()
        c.CommandText <- $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = 'max_slot_absr_bp'"
        Convert.ToInt64(c.ExecuteScalar()) > 0L
    let prepassClause =
        if minVolat20m > 0.0 && hasPrepassCol then
            eprintfn "  prepass     = volat trim: max_slot_absr_bp >= %.2f bp (implied by the volat_20m >= %.0f bp gate)"
                (minVolat20m * 1e4 - 0.01) (minVolat20m * 1e4)
            sprintf "AND max_slot_absr_bp >= %.17g" (minVolat20m * 1e4 - 0.01)
        else ""
    // ⭐ S40e episode-warmup gate (see Config.MinBarnum). Column-guarded like the
    // prepass: legacy tables without `barnum` were warmed by construction.
    let hasBarnumCol =
        use c = conn.CreateCommand()
        c.CommandText <- $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = 'barnum'"
        Convert.ToInt64(c.ExecuteScalar()) > 0L
    let barnumClause =
        if minBarnum > 0 && hasBarnumCol then
            eprintfn "  warmup      = barnum >= %d (early-episode slice cut — S40e)" minBarnum
            sprintf "AND barnum >= %d" minBarnum
        else ""
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        // ⭐ close_m1 is ALREADY the prior close in D's raw scale, so the price
        // floor is a plain comparison — no `/ adj_ratio` rescale, which is exactly
        // the ratio-of-two-differently-scaled-numbers that CLAUDE.md rule 4 warns
        // about. This is the visible payoff of the causal scheme.
        $"SELECT ticker, date, close_d, n, close_m1, div_m1, close_m3, div_m3,
                 close_p1, div_p1, close_p3, div_p3, close_p5, div_p5, open_p1,
                 dv_0945, rvol_0945_honest
          FROM {table}
          WHERE date >= $start AND date <= $end AND dv_0945 >= $mindv
            AND rvol_0945_honest >= $minrvol
            AND coalesce(close_m1, 0) >= $minprevclose
            {prepassClause}
            {barnumClause}
          ORDER BY ticker, date"
    let pStart = cmd.CreateParameter() in pStart.ParameterName <- "start"; pStart.Value <- startDate; cmd.Parameters.Add pStart |> ignore
    let pEnd   = cmd.CreateParameter() in pEnd.ParameterName   <- "end";   pEnd.Value   <- endDate;   cmd.Parameters.Add pEnd   |> ignore
    let pDv    = cmd.CreateParameter() in pDv.ParameterName    <- "mindv"; pDv.Value    <- minDv0945; cmd.Parameters.Add pDv    |> ignore
    let pRv    = cmd.CreateParameter() in pRv.ParameterName    <- "minrvol"; pRv.Value  <- minRvol0945; cmd.Parameters.Add pRv  |> ignore
    let pPc    = cmd.CreateParameter() in pPc.ParameterName    <- "minprevclose"; pPc.Value <- minPrevClose; cmd.Parameters.Add pPc |> ignore
    let out = ResizeArray<Candidate>()
    use reader = cmd.ExecuteReader()
    let dbl (i: int) = if reader.IsDBNull i then nan else reader.GetDouble i
    while reader.Read() do
        out.Add(
            { Ticker = reader.GetString 0
              Date   = DateOnly.FromDateTime(reader.GetDateTime 1)
              CloseD = dbl 2
              N = dbl 3
              CloseM1 = dbl 4
              DivM1 = dbl 5
              CloseM3 = dbl 6
              DivM3 = dbl 7
              CloseP1 = dbl 8
              DivP1 = dbl 9
              CloseP3 = dbl 10
              DivP3 = dbl 11
              CloseP5 = dbl 12
              DivP5 = dbl 13
              OpenP1 = dbl 14
              Dv0945 = dbl 15
              Rvol0945Honest = dbl 16 })
    out.ToArray()

// ===========================================================================
// The trip parquet sink — appender into an in-memory staging table, rotated to
// zstd part files (trips_p000.parquet, trips_p001.parquet, ...) so nothing
// holds the full book. Column order in appendTrip MUST match the CREATE TABLE.
// ===========================================================================
[<Literal>]
// 250k (was 2M): the S39 base pass emits millions of ~140-col trips — a 2M-row
// in-memory staging table is ~2GB+ and OOM'd a 15GB VM at 82% with zero parts
// flushed. 250k bounds staging at ~350MB; post-hoc reads glob the parts anyway.
let private RowsPerPart = 250_000

let private tripTableSql = """
CREATE TABLE trips (
    symbol VARCHAR, trade_date VARCHAR, n DOUBLE,
    signal_sec INTEGER, signal_vwap DOUBLE, entry_sec INTEGER, entry_px DOUBLE,
    volat_20m DOUBLE, volat_10m DOUBLE, rng_20m DOUBLE, eff_20m DOUBLE, eff_10m DOUBLE, slot_count INTEGER,
    rng_sess DOUBLE, rng_600 DOUBLE, rng_300 DOUBLE, rng_120 DOUBLE, rng_60 DOUBLE, rng_30 DOUBLE,
    breach_sess INTEGER, breach_1200 INTEGER, breach_600 INTEGER, breach_300 INTEGER,
    breach_120 INTEGER, breach_60 INTEGER, breach_30 INTEGER,
    breach_lo_sess INTEGER, breach_lo_1200 INTEGER, breach_lo_600 INTEGER, breach_lo_300 INTEGER,
    breach_lo_120 INTEGER, breach_lo_60 INTEGER, breach_lo_30 INTEGER,
    bars_since_first_low INTEGER, lows_since_first_low INTEGER,
    bars_since_first_low_300 INTEGER, lows_since_first_low_300 INTEGER,
    bars_since_first_low_600 INTEGER, lows_since_first_low_600 INTEGER,
    trade_idx INTEGER, open_at_signal INTEGER,
    vwap_1200 DOUBLE, chan_hi DOUBLE, chan_lo DOUBLE, exit_chan_hi DOUBLE,
    gap_60 INTEGER, gap_30 INTEGER, gap_15 INTEGER,
    sess_vwap DOUBLE, dist_sess_vwap DOUBLE, pct_chg_open DOUBLE,
    bar_vol DOUBLE, bar_tc INTEGER,
    vol_5 DOUBLE, vol_10 DOUBLE, vol_15 DOUBLE, vol_30 DOUBLE, vol_60 DOUBLE, vol_600 DOUBLE, vol_1200 DOUBLE,
    tc_5 DOUBLE, tc_10 DOUBLE, tc_15 DOUBLE, tc_30 DOUBLE, tc_60 DOUBLE, tc_600 DOUBLE, tc_1200 DOUBLE,
    vol_60_prev DOUBLE, tc_60_prev DOUBLE, vwap_60 DOUBLE, vwap_60_prev DOUBLE,
    vwap_5_prev DOUBLE, vwap_10_prev DOUBLE,
    dollar_vol_60 DOUBLE, cum_vol DOUBLE, cum_tc DOUBLE,
    fwd_vwap_60 DOUBLE, fwd_vwap_300 DOUBLE, fwd_vwap_600 DOUBLE, fwd_vwap_1200 DOUBLE,
    aux_hi_60_px DOUBLE, aux_hi_60_sec INTEGER,
    aux_hi_120_px DOUBLE, aux_hi_120_sec INTEGER,
    aux_hi_300_px DOUBLE, aux_hi_300_sec INTEGER,
    aux_hi_600_px DOUBLE, aux_hi_600_sec INTEGER,
    aux_hi_1200_px DOUBLE, aux_hi_1200_sec INTEGER,
    ma_10m_px DOUBLE, ma_10m_sec INTEGER,
    ma_20m_px DOUBLE, ma_20m_sec INTEGER,
    ma_30m_px DOUBLE, ma_30m_sec INTEGER,
    ma_40m_px DOUBLE, ma_40m_sec INTEGER,
    ma_50m_px DOUBLE, ma_50m_sec INTEGER,
    ma_60m_px DOUBLE, ma_60m_sec INTEGER,
    vwma_10m_px DOUBLE, vwma_10m_sec INTEGER,
    vwma_20m_px DOUBLE, vwma_20m_sec INTEGER,
    vwma_30m_px DOUBLE, vwma_30m_sec INTEGER,
    vwma_40m_px DOUBLE, vwma_40m_sec INTEGER,
    vwma_50m_px DOUBLE, vwma_50m_sec INTEGER,
    vwma_60m_px DOUBLE, vwma_60m_sec INTEGER,
    exit_sec INTEGER, exit_px DOUBLE, exit_reason VARCHAR,
    ret_exit DOUBLE, bars_held INTEGER,
    close_m1 DOUBLE, div_m1 DOUBLE, close_m3 DOUBLE, div_m3 DOUBLE, close_d DOUBLE,
    close_p1 DOUBLE, div_p1 DOUBLE, close_p3 DOUBLE, div_p3 DOUBLE,
    close_p5 DOUBLE, div_p5 DOUBLE, open_p1 DOUBLE,
    dv_0945 DOUBLE, rvol_0945_honest DOUBLE, dv_0945_tape DOUBLE,
    vwap_30_prev DOUBLE, hi_30 DOUBLE, vwap_30 DOUBLE, vwap_120 DOUBLE, vwap_180 DOUBLE,
    ols_slope_60 DOUBLE, ols_r_60 DOUBLE, ols_slope_120 DOUBLE, ols_r_120 DOUBLE,
    ols_slope_180 DOUBLE, ols_r_180 DOUBLE,
    ols_slope_300 DOUBLE, ols_r_300 DOUBLE,
    ols_slope_600 DOUBLE, ols_r_600 DOUBLE, ols_slope_1200 DOUBLE, ols_r_1200 DOUBLE,
    n_eff_shannon_600 DOUBLE, n_eff_hhi_600 DOUBLE, n_eff_shannon_1200 DOUBLE, n_eff_hhi_1200 DOUBLE,
    n_eff_ret_20m DOUBLE, n_eff_ret_10m DOUBLE,
    hi_60 DOUBLE,
    rng_slots_20m DOUBLE, rng_slots_10m DOUBLE, eff_rng_20m DOUBLE, eff_rng_10m DOUBLE,
    eff_rng_lin_20m DOUBLE, eff_rng_lin_10m DOUBLE,
    secs_since_first_low INTEGER,
    eff_9ema_20m DOUBLE, eff_9ema_10m DOUBLE,
    gap_300 INTEGER, gap_600 INTEGER, gap_1200 INTEGER,
    max_gap_run_1200 DOUBLE, max_gap_run_300 DOUBLE, big_gap_runs_1200 DOUBLE,
    gap_adj_60 INTEGER, gap_adj_300 INTEGER, gap_adj_600 INTEGER, gap_adj_1200 INTEGER,
    halts_today INTEGER, secs_since_halt INTEGER,
    ols_slope_since_high DOUBLE, ols_r_since_high DOUBLE, bars_since_high INTEGER,
    ols_slope_since_flow DOUBLE, ols_r_since_flow DOUBLE,
    eff_since_high DOUBLE, eff_since_flow DOUBLE,
    eff9_since_high DOUBLE, eff9_since_flow DOUBLE, slots_since_high INTEGER, slots_since_flow INTEGER,
    z_since_high DOUBLE, z_since_flow DOUBLE, chan_lo_prev DOUBLE,
    chan_lo_prev_120 DOUBLE, hi_120 DOUBLE, chan_lo_prev_180 DOUBLE, hi_180 DOUBLE,
    d_hi_flow DOUBLE, ols_slope_hi_flow DOUBLE, ols_r_hi_flow DOUBLE, eff_hi_flow DOUBLE,
    arm_hi_eff_20m DOUBLE, arm_hi_eff_10m DOUBLE, arm_hi_slots_20m INTEGER, arm_hi_slots_10m INTEGER, first_low_vwap DOUBLE,
    bars_above_svwap INTEGER, bars_present INTEGER, sess_low DOUBLE, sess_high DOUBLE,
    sess_dv2 DOUBLE, sess_dlv DOUBLE, sess_dlv2 DOUBLE,
    dv2_300 DOUBLE, dlv_300 DOUBLE, dlv2_300 DOUBLE,
    dv2_600 DOUBLE, dlv_600 DOUBLE, dlv2_600 DOUBLE,
    dv2_1200 DOUBLE, dlv_1200 DOUBLE, dlv2_1200 DOUBLE,
    vol_300 DOUBLE, tc_300 DOUBLE,
    dollar_vol_300 DOUBLE, dollar_vol_600 DOUBLE, dollar_vol_1200 DOUBLE,
    n_eff_shannon_60 DOUBLE, n_eff_hhi_60 DOUBLE, n_eff_shannon_300 DOUBLE, n_eff_hhi_300 DOUBLE,
    vol_leg DOUBLE, tc_leg DOUBLE, dv_leg DOUBLE, cum_dv DOUBLE,
    targets_today INTEGER, volat_20m_prev DOUBLE, vol_0945_tape DOUBLE,
    volat_slope_20m DOUBLE, volat_r_20m DOUBLE, volat_slope_10m DOUBLE, volat_r_10m DOUBLE,
    halts_1200 INTEGER, halts_600 INTEGER,
    downticks_since_uptick INTEGER, run_downticks INTEGER, secs_since_last_uptick INTEGER,
    dn_15 INTEGER, dn_30 INTEGER, dn_60 INTEGER, dn_120 INTEGER,
    up_15 INTEGER, up_30 INTEGER, up_60 INTEGER, up_120 INTEGER,
    gap_120 INTEGER,
    downticks_since_flow INTEGER, upticks_since_flow INTEGER, lows_since_uptick INTEGER, chg_since_last_uptick DOUBLE, chg_since_run_pre_low DOUBLE, chg_since_run_first_low DOUBLE, chg_since_run_first_dn DOUBLE,
    qty DOUBLE, net_pnl DOUBLE
)"""

// (right-side-of-V cont_trips sink DELETED — S39g: study closed at S26,
// tracking cost removed from the sampler.)

type TripSink(outDir: string) =
    let conn = new DuckDBConnection("Data Source=:memory:")
    do
        conn.Open()
        IO.Directory.CreateDirectory outDir |> ignore
        use cmd = conn.CreateCommand()
        let tmpDir = IO.Path.Combine(IO.Path.GetTempPath(), $"ff_duck_sink_{Guid.NewGuid():N}")
        IO.Directory.CreateDirectory tmpDir |> ignore
        cmd.CommandText <- $"PRAGMA memory_limit='2GB'; PRAGMA temp_directory='{tmpDir}'; " + tripTableSql
        cmd.ExecuteNonQuery() |> ignore
    let mutable appender = conn.CreateAppender "trips"
    let mutable rowsInPart = 0
    let mutable part = 0
    let mutable total = 0L
    // quick console attribution (⚠ mc=0 = attribution, not portfolio)
    let mutable grossWin = 0.0
    let mutable grossLoss = 0.0
    let mutable wins = 0L

    let flushPart () =
        appender.Close()
        if rowsInPart > 0 then
            let path = IO.Path.Combine(outDir, sprintf "trips_p%03d.parquet" part).Replace("\\", "/").Replace("'", "''")
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"COPY trips TO '{path}' (FORMAT PARQUET, COMPRESSION 'zstd'); DELETE FROM trips;"
            cmd.ExecuteNonQuery() |> ignore
            part <- part + 1
            rowsInPart <- 0

    member _.Total = total
    member _.Wins = wins
    member _.GrossWin = grossWin
    member _.GrossLoss = grossLoss

    /// Append one finished trip. NaN floats become NULL.
    member _.Add (c: Candidate) (notional: float) (p: FlushPosition) =
        match p.State with
        | Holding | PendingExit _ -> failwith "TripSink.Add on an unfinished position (Flatten first)"
        | ExitedAt (exitSec, lastBarPx, rawReason) ->
            // ⭐⭐ S43bw (user 2026-08-14): AN UNRESOLVED POSITION NO LONGER SELLS
            // INTO THE LAST BAR. A "moc" exit means the 5m-high target simply was
            // not hit during the session — that is not a reason to dump stock into
            // the close at whatever the tape prints. The position is HELD OVERNIGHT
            // and exits at the NEXT SESSION'S OPEN ("next_open").
            //
            // Measured on the 181 v44 moc trips (all four rules, same trips):
            //     moc @16:00 old corpus   mean -5.98%  med -3.01%  win 13.3%  PF 0.022
            //     moc @16:00 new corpus   mean -4.85%  med -2.47%  win 18.2%  PF 0.157
            //     run to the tape's end   mean -4.29%  med -2.19%  win 23.2%  PF 0.140
            //  ⭐ hold to the next OPEN    mean -3.36%  med -1.69%  win 41.4%  PF 0.422
            // Letting the trade run into the POST-MARKET is not the answer (+0.56pp
            // over the 16:00 bound, and 55 of 181 never resolve even by 20:00 — the
            // channels count PRESENT BARS, so a "5m high" spans hours on thin tape).
            // The next open is, and it more than doubles the win rate.
            // ⚠ It also lengthens the left tail: worst -30.2% -> -39.0%. There is no
            // stopping out overnight — the same asymmetry that sank ShortSnoozer.
            //
            // exit_px carries the OPEN PLUS THE DIVIDEND increment, so it is exit
            // PROCEEDS per share and `ret_exit = exit_px/entry_px - 1` still holds
            // for every row. Both terms already arrive in day D's raw scale, so they
            // are directly comparable to entry_px (S43br); div_p1 is recorded
            // separately, so folding it in loses nothing. It is nonzero on 6 of
            // 36,025 trips and on none of the moc ones.
            //
            // exit_sec stays the LAST INTRADAY BAR's second — when trading stopped,
            // not when the fill happens. `exit_reason = 'next_open'` is what marks the
            // gap. The label names the FILL, like 'target' and 'moc' do — an earlier
            // 'ovn' named the overnight HOLD instead and read as "opening" to everyone
            // who had not seen the S43bq study it came from (user, 2026-08-14).
            //
            // Fallback: no next session (last day of the sample, delisting) leaves
            // open_p1 NULL -> NaN. Those keep the old last-bar 'moc' behaviour, since
            // there is no open to exit into. 67 of 36,025 trips carry a NULL open_p1.
            let toNextOpen = rawReason = "moc" && not (Double.IsNaN c.OpenP1) && c.OpenP1 > 0.0
            let exitPx =
                if toNextOpen then c.OpenP1 + (if Double.IsNaN c.DivP1 then 0.0 else c.DivP1)
                else lastBarPx
            let reason = if toNextOpen then "next_open" else rawReason
            let qty = notional / p.EntryPx
            let pnl = qty * (exitPx - p.EntryPx)
            let row = appender.CreateRow()
            let inline f (x: float) =
                if Double.IsNaN x then row.AppendNullValue() |> ignore
                else row.AppendValue x |> ignore
            let inline i (x: int) = row.AppendValue x |> ignore
            let inline s (x: string) = row.AppendValue x |> ignore
            s c.Ticker
            s (c.Date.ToString "yyyy-MM-dd")
            f c.N
            i p.SignalSec; f p.SignalVwap; i p.EntrySec; f p.EntryPx
            f p.Volat20m; f p.Volat10m; f p.Rng20m; f p.Eff20m; f p.Eff10m; i p.SlotCount
            f p.RngSess; f p.Rng600; f p.Rng300; f p.Rng120; f p.Rng60; f p.Rng30
            i p.BreachSess; i p.Breach1200; i p.Breach600; i p.Breach300
            i p.Breach120; i p.Breach60; i p.Breach30
            i p.BreachLoSess; i p.BreachLo1200; i p.BreachLo600; i p.BreachLo300
            i p.BreachLo120; i p.BreachLo60; i p.BreachLo30
            i p.BarsSinceFirstLow; i p.LowsSinceFirstLow
            i p.BarsSinceFirstLow300; i p.LowsSinceFirstLow300
            i p.BarsSinceFirstLow600; i p.LowsSinceFirstLow600
            i p.TradeIdx; i p.OpenAtSignal
            f p.Vwap1200; f p.ChanHi; f p.ChanLo; f p.ExitChanHi
            i p.Gap60; i p.Gap30; i p.Gap15
            f p.SessVwap; f p.DistSessVwap; f p.PctChgOpen
            f p.BarVol; i p.BarTc
            f p.Vol5; f p.Vol10; f p.Vol15; f p.Vol30; f p.Vol60; f p.Vol600; f p.Vol1200
            f p.Tc5; f p.Tc10; f p.Tc15; f p.Tc30; f p.Tc60; f p.Tc600; f p.Tc1200
            f p.Vol60Prev; f p.Tc60Prev; f p.Vwap60; f p.Vwap60Prev
            f p.Vwap5Prev; f p.Vwap10Prev
            f p.DollarVol60; f p.CumVol; f p.CumTc
            f p.FwdVwap60; f p.FwdVwap300; f p.FwdVwap600; f p.FwdVwap1200
            let inline auxSec (s: int) =
                if s < 0 then row.AppendNullValue() |> ignore else row.AppendValue s |> ignore
            f p.AuxHi60; auxSec p.AuxSec60
            f p.AuxHi120; auxSec p.AuxSec120
            f p.AuxHi300; auxSec p.AuxSec300
            f p.AuxHi600; auxSec p.AuxSec600
            f p.AuxHi1200; auxSec p.AuxSec1200
            f p.Ma10Px; auxSec p.Ma10Sec
            f p.Ma20Px; auxSec p.Ma20Sec
            f p.Ma30Px; auxSec p.Ma30Sec
            f p.Ma40Px; auxSec p.Ma40Sec
            f p.Ma50Px; auxSec p.Ma50Sec
            f p.Ma60Px; auxSec p.Ma60Sec
            f p.Vwma10Px; auxSec p.Vwma10Sec
            f p.Vwma20Px; auxSec p.Vwma20Sec
            f p.Vwma30Px; auxSec p.Vwma30Sec
            f p.Vwma40Px; auxSec p.Vwma40Sec
            f p.Vwma50Px; auxSec p.Vwma50Sec
            f p.Vwma60Px; auxSec p.Vwma60Sec
            i exitSec; f exitPx; s reason
            f (if p.EntryPx > 0.0 then exitPx / p.EntryPx - 1.0 else nan)
            i p.BarsHeld
            f c.CloseM1; f c.DivM1; f c.CloseM3; f c.DivM3; f c.CloseD
            f c.CloseP1; f c.DivP1; f c.CloseP3; f c.DivP3
            f c.CloseP5; f c.DivP5; f c.OpenP1
            f c.Dv0945; f c.Rvol0945Honest; f p.Dv0945Tape
            f p.Vwap30Prev; f p.Hi30; f p.Vwap30; f p.Vwap120; f p.Vwap180
            f p.OlsSlope60; f p.OlsR60; f p.OlsSlope120; f p.OlsR120; f p.OlsSlope180; f p.OlsR180
            f p.OlsSlope300; f p.OlsR300
            f p.OlsSlope600; f p.OlsR600; f p.OlsSlope1200; f p.OlsR1200
            f p.NEffShannon600; f p.NEffHhi600; f p.NEffShannon1200; f p.NEffHhi1200
            f p.NEffRet20m; f p.NEffRet10m
            f p.Hi60
            f p.RngSlots20m; f p.RngSlots10m; f p.EffRng20m; f p.EffRng10m
            f p.EffRngLin20m; f p.EffRngLin10m
            i p.SecsSinceFirstLow
            f p.Eff9Ema20m; f p.Eff9Ema10m
            i p.Gap300; i p.Gap600; i p.Gap1200
            f p.MaxGapRun1200; f p.MaxGapRun300; f p.BigGapRuns1200
            i p.GapAdj60; i p.GapAdj300; i p.GapAdj600; i p.GapAdj1200
            i p.HaltsToday; i p.SecsSinceHalt
            f p.OlsSlopeSinceHigh; f p.OlsRSinceHigh; i p.BarsSinceHigh
            f p.OlsSlopeSinceFlow; f p.OlsRSinceFlow
            f p.EffSinceHigh; f p.EffSinceFlow
            f p.Eff9SinceHigh; f p.Eff9SinceFlow; i p.SlotsSinceHigh; i p.SlotsSinceFlow
            f p.ZSinceHigh; f p.ZSinceFlow; f p.ChanLoPrev
            f p.ChanLoPrev120; f p.Hi120; f p.ChanLoPrev180; f p.Hi180
            f p.DHiFlow; f p.OlsSlopeHiFlow; f p.OlsRHiFlow; f p.EffHiFlow
            f p.ArmHiEff20m; f p.ArmHiEff10m; i p.ArmHiSlots20m; i p.ArmHiSlots10m; f p.FirstLowVwap
            i p.BarsAboveSvwap; i p.BarsPresent
            f p.SessLow; f p.SessHigh
            f p.SessDv2; f p.SessDlv; f p.SessDlv2
            f p.Dv2300; f p.Dlv300; f p.Dlv2300
            f p.Dv2600; f p.Dlv600; f p.Dlv2600
            f p.Dv21200; f p.Dlv1200; f p.Dlv21200
            f p.Vol300; f p.Tc300
            f p.DollarVol300; f p.DollarVol600; f p.DollarVol1200
            f p.NEffShannon60; f p.NEffHhi60; f p.NEffShannon300; f p.NEffHhi300
            f p.VolLeg; f p.TcLeg; f p.DvLeg; f p.CumDv
            i p.TargetsToday; f p.Volat20mPrev; f p.Vol0945Tape
            f p.VolatSlope20m; f p.VolatR20m; f p.VolatSlope10m; f p.VolatR10m
            i p.Halts1200; i p.Halts600
            i p.DownticksSinceUptick; i p.RunDownticks; i p.SecsSinceLastUptick
            i p.Dn15; i p.Dn30; i p.Dn60; i p.Dn120
            i p.Up15; i p.Up30; i p.Up60; i p.Up120
            i p.Gap120
            i p.DownticksSinceFlow; i p.UpticksSinceFlow; i p.LowsSinceUptick; f p.ChgSinceLastUptick; f p.ChgSinceRunPreLow; f p.ChgSinceRunFirstLow; f p.ChgSinceRunFirstDn
            f qty; f pnl
            row.EndRow()
            total <- total + 1L
            if pnl > 0.0 then wins <- wins + 1L; grossWin <- grossWin + pnl
            else grossLoss <- grossLoss - pnl
            rowsInPart <- rowsInPart + 1
            if rowsInPart >= RowsPerPart then
                flushPart ()
                appender <- conn.CreateAppender "trips"

    interface IDisposable with
        member _.Dispose () =
            flushPart ()
            conn.Dispose()

// ===========================================================================
// Pipeline 2 — SecEmitter -> IntradaySystem -> TripSink. Same drain-on-ticker-
// boundary shape as DipRiderV6's MinuteEmitter loop; the parquet is opened once
// per date and streamed in (ticker, bucket) order (the files are stored sorted).
// ===========================================================================
type SecEmitter
        ( conn: DuckDBConnection, path: string,
          tickers: string[], sessionStartSec: int, mocSec: int ) =

    member val Conn = conn

    member val Sql =
        let tickerList = tickers |> Array.map (fun t -> "'" + t.Replace("'", "''") + "'") |> String.concat ","
        // `bucket` IS seconds-since-00:00-ET (the 1s builder already did the
        // timezone work) — no window_start conversion, unlike the 1m emitters.
        sprintf """
        SELECT ticker, bucket, vwap::DOUBLE, volume::DOUBLE, trade_count
        FROM read_parquet('%s')
        WHERE ticker IN (%s) AND bucket >= %d AND bucket <= %d
          AND vwap > 0 AND volume > 0
        ORDER BY ticker, bucket"""
            (path.Replace("'", "''")) tickerList sessionStartSec mocSec

    /// Stream every candidate-ticker present 1s bar for this date, RAW, in
    /// (ticker, bucket) order. `inline` so onNext fuses into the read loop — this
    /// loop runs ~10^6-10^7 times per day.
    ///
    /// ⭐ S43br: THE TAPE IS NO LONGER SCALED. It used to be `vwap = raw * adj_ratio`
    /// and `volume = raw / adj_ratio`, where adj_ratio folded in every FUTURE split —
    /// so every intraday price carried future information, and price and volume had
    /// to be kept on one scale by hand (the 2026-07-29 fix, after the DvFloor60 gate
    /// went future-split-dependent and let 808 trips through on inflation alone, S29;
    /// and S43v, where a second multiply made 71.4% of an "S-tier" book artifact).
    ///
    /// Raw is correct because every intraday feature is either a same-day RATIO (where
    /// any common factor cancels) or a dollar quantity (where raw price x raw volume is
    /// already real dollars). Cross-day context arrives pre-converted into D's raw scale
    /// on the Candidate. Nothing left to multiply — so the double-multiply bug class is
    /// gone, not merely audited for.
    member inline this.Process(onNext: string * SecBar -> unit) =
        use cmd = this.Conn.CreateCommand()
        cmd.CommandText <- this.Sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let ticker = reader.GetString 0
            let bar : SecBar =
                { etSec      = reader.GetInt32 1
                  vwap       = reader.GetDouble 2
                  volume     = reader.GetDouble 3
                  tradeCount = reader.GetInt32 4 }
            onNext (ticker, bar)

/// One day-read request to the single reader: the reply channel receives one
/// message per (candidate, materialized bar array), then completes. On a read
/// failure the reply completes with the exception (the requesting worker's
/// fold loop rethrows it — no hangs).
type private DayRequest =
    { Date: DateOnly
      Cands: Candidate[]
      Reply: ChannelWriter<struct (Candidate * SecBar[])> }

/// Run pipeline 2 for every candidate day, streaming finished trips into the
/// sink. Returns the number of (ticker, day) candidates whose tape was found.
///
/// ⭐ S39h/S39p: DAYS RUN IN PARALLEL behind a SINGLE READER (user design). One
/// reader task owns THE process's only parquet-reading DuckDB connection;
/// cfg.Workers day-workers are pure folders (a day is the natural isolation
/// unit: fresh IntradaySystems, no cross-day state). A worker claims a day,
/// sends (day, cands, reply) to the reader, and folds the per-tkd bar arrays
/// that come back on its private reply channel; in-flight tape data is
/// structurally bounded at ~one day per worker because a worker only requests
/// its next day after folding the previous one. Centralized IO: no per-worker
/// memory caps, no concurrent spill directories (the S39 crash class is
/// structurally gone). Finished tkds flow to the single consumer below, which
/// owns the TripSink (appender single-threaded by construction) and the
/// progress counter. Trip ORDER in the parquet is nondeterministic across
/// runs; the trip SET is not (zero-diff, set-compared).
let collectTrips (cfg: Config) (secDir: string)
                 (candidates: Candidate[]) (sink: TripSink)
                 (progress: (DateOnly -> int -> int -> int64 -> unit) option) : int =
    let work = Channel.CreateUnbounded<DateOnly * Candidate[]>()
    // Producer task with ASYNC writes (user): sync TryWrite-and-ignore against a
    // concurrent structure silently drops failures — an anti-pattern even where
    // "it can't fail". Complete() in finally so workers can never hang on a
    // faulted producer.
    let producer =
        task {
            try
                // sortBy fst (user): groupBy emits day-groups in first-occurrence
                // order of the ticker-sorted candidate list — scrambled dates in
                // the progress log and a jumpier ETA. Chronological costs nothing.
                for item in candidates |> Array.groupBy (fun c -> c.Date) |> Array.sortBy fst do
                    do! work.Writer.WriteAsync item
            finally work.Writer.Complete()
        }

    // One message per (ticker, day): its finished positions after Flatten.
    // tapeFound=false for days whose 1s file is missing (they still count into
    // the progress denominator so the remaining estimate stays honest). A
    // candidate whose file exists always drains: the dv_0945_tape >= $2M table
    // floor guarantees at least one vwap>0/volume>0 bar in the emitter's range.
    // BOUNDED: 4096 tkd messages of backpressure. If the single sink consumer
    // ever falls behind 14 producers on a trip-dense stretch, workers block on
    // the write instead of ballooning the heap (part of the 82%-OOM postmortem).
    let results = Channel.CreateBounded<struct (Candidate * FlushPosition[] * bool)>(BoundedChannelOptions 4096)

    // ----- the single reader -----
    let requests = Channel.CreateUnbounded<DayRequest>()
    let readerTask =
        task {
            do! Task.Yield()
            use conn = new DuckDBConnection("Data Source=:memory:")
            conn.Open()
            do  use pragma = conn.CreateCommand()
                // THE parquet connection: give it real threads and memory, and a
                // private spill dir (the shared-".tmp/" collision class is gone
                // by construction with one reader, the private dir is hygiene).
                let tmpDir = IO.Path.Combine(IO.Path.GetTempPath(), $"ff_duck_reader_{Guid.NewGuid():N}")
                IO.Directory.CreateDirectory tmpDir |> ignore
                pragma.CommandText <- $"PRAGMA threads=6; PRAGMA memory_limit='4GB'; PRAGMA temp_directory='{tmpDir}'"
                pragma.ExecuteNonQuery() |> ignore
            let tkds = ResizeArray<struct (Candidate * SecBar[])>()
            let buf = ResizeArray<SecBar>()
            for req in requests.Reader.ReadAllAsync() do
                try
                    // materialize the day's per-tkd bar arrays in the SYNC emitter
                    // fold, then hand them over with ASYNC writes (user: no
                    // TryWrite against a concurrent structure).
                    tkds.Clear()
                    let path = IO.Path.Combine(secDir, sprintf "%s.parquet" (req.Date.ToString "yyyy-MM-dd"))
                    let byTicker = req.Cands |> Array.map (fun c -> c.Ticker, c) |> dict
                    // ⭐ S43bx: the bar query's upper bound is the DAY'S close, so on
                    // early-close days the near-empty post-13:00 tape is never read
                    // (it is also never acted on — IntradaySystem uses the same bound).
                    let dayMocSec =
                        if TradingEdge.Orb.Timezone.early_closes.Contains req.Date
                        then cfg.Intraday.MocSecShort else cfg.Intraday.MocSec
                    let emitter = SecEmitter(conn, path, Array.map (fun (c: Candidate) -> c.Ticker) req.Cands,
                                             cfg.Intraday.SessionStartSec, dayMocSec)
                    let mutable curTicker : string = null
                    let flush () =
                        if not (isNull curTicker) then
                            tkds.Add(struct (byTicker.[curTicker], buf.ToArray()))
                            buf.Clear()
                    emitter.Process(fun (ticker, bar) ->
                        if ticker <> curTicker then
                            flush ()
                            curTicker <- ticker
                        buf.Add bar)
                    flush ()
                    curTicker <- null
                    for t in tkds do
                        do! req.Reply.WriteAsync t
                    req.Reply.Complete()
                with ex ->
                    // fail THIS request loudly (the worker's fold loop rethrows)
                    // and keep serving the rest — a poisoned day must not hang
                    // the pipeline.
                    req.Reply.TryComplete ex |> ignore
        }

    let worker () : Task =
        task {
            // hop off the caller's thread FIRST: the work channel is already
            // complete, so ReadAllAsync yields items synchronously — without
            // this, worker #1 would hot-start and drain the whole channel
            // before worker #2 is even constructed (task{} runs synchronously
            // until its first genuine await).
            do! Task.Yield()
            let dayOut = ResizeArray<struct (Candidate * FlushPosition[] * bool)>()
            for date, cands in work.Reader.ReadAllAsync() do
                dayOut.Clear()
                let path = IO.Path.Combine(secDir, sprintf "%s.parquet" (date.ToString "yyyy-MM-dd"))
                if not (IO.File.Exists path) then
                    for c in cands do dayOut.Add(struct (c, Array.empty, false))
                else
                    let reply = Channel.CreateUnbounded<struct (Candidate * SecBar[])>()
                    do! requests.Writer.WriteAsync { Date = date; Cands = cands; Reply = reply.Writer }
                    for struct (c, bars) in reply.Reader.ReadAllAsync() do
                        if bars.Length > 0 then
                            let sys = IntradaySystem(cfg.Intraday, c.Ticker, date)
                            for b in bars do sys.Process b
                            sys.Flatten bars.[bars.Length - 1]
                            dayOut.Add(struct (c, Seq.toArray sys.Positions, true))
                for r in dayOut do
                    do! results.Writer.WriteAsync r
        }

    let workerTasks = Array.init (max 1 cfg.Workers) (fun _ -> worker ())
    // Close the request + results channels when every worker is done — in a
    // finally, so a faulted worker can never leave the reader or consumer
    // hanging; the fault itself re-surfaces at the WaitAll below.
    let allWorkers =
        task {
            try do! Task.WhenAll workerTasks
            finally
                requests.Writer.Complete()
                results.Writer.Complete()
        }

    let mutable daysRun = 0
    let mutable processedTkd = 0
    let consumer =
        task {
            for struct (c, positions, tapeFound) in results.Reader.ReadAllAsync() do
                if tapeFound then daysRun <- daysRun + 1
                for pos in positions do
                    match pos.State with
                    | ExitedAt _ -> sink.Add c cfg.Notional pos
                    | _ -> failwith "Flatten closes all; unreachable"
                processedTkd <- processedTkd + 1
                progress |> Option.iter (fun p -> p c.Date processedTkd candidates.Length sink.Total)
        }
    // sole synchronous join at the outermost boundary (main is sync)
    Task.WaitAll [| producer :> Task; readerTask :> Task; allWorkers :> Task; consumer :> Task |]
    daysRun

/// Console-summary snapshot of the sink counters (the sink itself is disposed
/// — and its last part flushed — before `run` returns).
type TripSinkStats =
    { Total: int64
      Wins: int64
      GrossWin: float
      GrossLoss: float }

/// Run the whole FlushFader sampler: candidates from trading.db (pipeline 1),
/// then the 1s flush engine per candidate day (pipeline 2), trips streamed
/// to parquet part files in `outDir`. Returns (candidate count, days run).
let run (dbPath: string) (secDir: string) (outDir: string) (cfg: Config)
        (startDate: DateOnly) (endDate: DateOnly)
        (progress: (DateOnly -> int -> int -> int64 -> unit) option) : int * int * TripSinkStats =
    let connStr = $"Data Source={dbPath};ACCESS_MODE=READ_ONLY"
    use conn = new DuckDBConnection(connStr)
    conn.Open()
    do
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA memory_limit='6GB'"
        pragma.ExecuteNonQuery() |> ignore

    let candidates = readCandidates conn startDate endDate cfg.MinDv0945 cfg.MinRvol0945 cfg.MinPrevClose cfg.Intraday.MinVolat20m cfg.MinBarnum
    use sink = new TripSink(outDir)
    let daysRun = collectTrips cfg secDir candidates sink progress
    // the `use` binding disposes the sink on return, flushing the final part
    // before the caller ever sees the stats
    candidates.Length, daysRun,
    { Total = sink.Total; Wins = sink.Wins; GrossWin = sink.GrossWin; GrossLoss = sink.GrossLoss }
