module TradingEdge.LongHiker.Intraday

open System
open TradingEdge.RollingMa
open TradingEdge.LongHiker.Roll

// ===========================================================================
// LongHiker — 1-SECOND-bar intraday MOMENTUM (long-only). The quantified SMB
// "Hitchhiker": a clean, efficient move off the open that you RIDE.
//
// ⭐⭐ THE SAMPLER SHAPE IS THE POINT (user, 2026-08-23). Every other engine in
// this repo fires on an EVENT — a new 20m low, a breakout, a reclaim — so one
// trip is one opportunity and the fill is one moment of microstructure. A
// momentum system evaluated that way cannot separate edge from the luck of
// WHERE in the move you happened to enter. LongHiker fires on a STATE instead:
//
//   ENTRY  : EVERY present bar whose efficiency-ratio-since-the-open is >=
//            MinEffOpen, inside the entry window and over the liquidity floors.
//            No leg machine, no latch, no one-trip-per-move rule.
//   FILL   : the NEXT present bar's vwap (the house convention — the 1s dataset
//            has no close, and the next-bar vwap is the honest "you traded into
//            the following second").
//   EXIT   : a pure BAR TIMESTOP, HoldBars present bars after the fill bar,
//            filled AT that bar's vwap. ⭐ Filling at that bar rather than the
//            one after is NOT a shortcut: the exit second was fixed HoldBars
//            bars earlier, so no information from it enters the decision. The
//            entry needs the next-bar convention because its decision is made
//            from the signal bar's own close; a pre-scheduled exit does not.
//   BACKSTOP: MocSec, and the day's last bar (Flatten).
//
// Averaging the entries across the whole move is what averages out the
// microstructure noise — that is the method, not a convenience.
//
// ⭐ EVERYTHING ELSE IS A RECORDED COLUMN. The ER gate produces a LOT of trips
// on purpose; the tightening is post-hoc SQL over the trip parquet, never a
// re-run with a new flag (the DipRiderV6/FlushFader discipline).
//
// ⚠⚠ TWO KNOWABILITY NOTES, both live and both quarantined post-hoc:
//
//  1. ENTRY WINDOW OPENS AT 09:40 (user), but the candidate universe
//     `mr_candidate_1s_v2` gates on `dv_0945_tape` and `n_bars_1s` measured over
//     [09:30, 09:45). Those are determined at 09:45. Every signal with
//     signal_sec < 35100 is therefore selected using five minutes of FUTURE
//     tape — a universe-shaped lookahead of exactly the class
//     docs/lookahead_protocol.md rule 5 describes.
//     ⭐ THE CONTROL IS FREE AND NEEDS NO RE-RUN: `signal_sec` is a recorded
//     column, so `WHERE signal_sec >= 35100` IS the clean book. Run every
//     headline both ways. If the 09:40-09:45 slice carries the edge, the edge
//     is the lookahead.
//     The engine also records its OWN tape-native dv_0945 (`dv_0945_tape`) so a
//     live-consistent floor can be applied instead of the table's.
//
//  2. eff_open IS NOT SPAN-FREE. Kaufman efficiency over a GROWING window drifts
//     with the window's length (measured on the anchored twins in FlushFader:
//     median 0.771 at 10-20 slots -> 0.139 at >= 80). eff_open >= 0.3 at 09:41
//     is a different statement from eff_open >= 0.3 at 15:00. `eff_open_slots`
//     is recorded for exactly this reason — CONDITION ON IT in every breakdown.
//
// ⭐ PRESENT-BAR SEMANTICS: the engine steps ONLY on seconds that exist in
// data/intraday_1s_slim/. Every bar-count window spans 60+ wall-clock seconds on
// a gappy name. The gap_* family measures precisely what that convention hides,
// and the forward marks are in WALL-CLOCK seconds so the horizon sweep is
// era-invariant.
//
// ⭐ CHANNELS ARE PARTIAL-TOLERANT HERE, unlike FlushFader's warm-only entry
// channel. At 09:40 the session is ten minutes old, so a "20m high" is the
// session high — which is what a trader looking at the chart actually sees, and
// refusing to answer would blank the headline reseat features across the whole
// early window the system is built to trade. `bars_present` and
// `secs_since_hi_*` are recorded so warmth is filterable post-hoc.
// ===========================================================================

/// One 1-second present bar from data/intraday_1s_slim/, RAW (S43br: the tape is
/// no longer split-scaled — same-day ratios cancel any common factor and raw
/// price x raw volume is already honest dollars).
type SecBar =
    { etSec: int
      vwap: float
      volume: float
      tradeCount: int }

/// Missing-second counter over a trailing WALL-CLOCK window: how many of the
/// last `windowSecs` seconds (the current bar's inclusive) had NO present bar.
/// Session-start clamp: before the window fills with session seconds the
/// denominator is the elapsed session span, so the first RTH bars do not read as
/// one giant gap.
[<Sealed>]
type GapCounter(windowSecs: int, sessionStartSec: int) =
    let q = System.Collections.Generic.Queue<int>()
    let mutable lastSec = -1
    member _.Push (sec: int) =
        q.Enqueue sec
        while q.Peek() <= sec - windowSecs do
            q.Dequeue() |> ignore
        lastSec <- sec
    member _.Gaps =
        if lastSec < 0 then 0
        else
            let span = min windowSecs (lastSec - sessionStartSec + 1)
            max 0 (span - q.Count)

/// ⭐ Growing-window OLS of y against the present-bar index since the SESSION
/// OPEN — the open-anchored twin of the fixed-window ols_*/vol_* pairs (user,
/// 2026-08-23). O(1) push, no window to warm and none to age out, so it is the
/// only trend measure that describes the WHOLE move rather than a trailing slice
/// of it — which is exactly the Hitchhiker claim. nan below 3 points.
///
/// ⚠ Its span grows all session, so like eff_open it is NOT comparable across
/// times of day. `bars_present` is its span; condition on it.
[<Sealed>]
type AnchoredOls() =
    let mutable n = 0.0
    let mutable sx = 0.0
    let mutable sy = 0.0
    let mutable sxy = 0.0
    let mutable sxx = 0.0
    let mutable syy = 0.0
    member _.Count = int n
    member _.Push (y: float) =
        let x = n
        n <- n + 1.0
        sx <- sx + x
        sy <- sy + y
        sxy <- sxy + x * y
        sxx <- sxx + x * x
        syy <- syy + y * y
    /// y per present bar (for log price, x 6e5 ~= bp/min in SQL — the same
    /// convention as the windowed ols_slope_*).
    member _.Slope =
        if n < 3.0 then nan
        else
            let d = n * sxx - sx * sx
            if d <= 0.0 then nan else (n * sxy - sx * sy) / d
    /// Pearson r, SIGNED (< 0 on a decline) — trend quality WITH direction.
    member _.R =
        if n < 3.0 then nan
        else
            let dx = n * sxx - sx * sx
            let dy = n * syy - sy * sy
            if dx <= 0.0 || dy <= 0.0 then nan
            else (n * sxy - sx * sy) / sqrt (dx * dy)

/// Trip life-cycle. There is no PendingExit state: the only exits are a
/// pre-scheduled timestop and the two backstops, all of which fill on the bar
/// that triggers them (see the header).
type LhPosState =
    | Holding
    | ExitedAt of exitSec: int * exitPx: float * reason: string
      // "timestop" | "moc" | "eod"

/// One sampler trip. Features are the state at the SIGNAL bar's close (that bar
/// has closed — inclusive is not lookahead). The fill is the NEXT present bar's
/// vwap. ⭐ NOTHING here gates beyond the ER gate and the two liquidity floors;
/// it is all recorded for post-hoc SQL.
///
/// ⚠ LEVELS, NOT RATIOS. hi_*/lo_*/vwap_* are raw prices; the distances the
/// study wants (`signal_vwap/lo_60 - 1`, `signal_vwap/vwap_60_prev - 1`, the
/// channel ranges `ln(hi_N/lo_N)`) are one arithmetic expression away in SQL and
/// are NOT duplicated as columns. One definition per quantity.
///
/// ⚠⚠ THE LIFECYCLE FIELDS ARE MUTABLE, and an F# record is a REFERENCE type, so
/// `all` below holds one object per trip and the fill / forward marks / exit
/// write into it. The obvious immutable spelling (`positions.[i] <- {p with ...}`
/// once per open trip per bar) allocates a ~100-field record per trip per bar:
/// at this system's density that is ~1,200 live trips x ~20,000 bars x ~1 KB =
/// TENS OF GIGABYTES of garbage per ticker-day. The Faders can afford it because
/// they hold a handful of trips; LongHiker cannot.
type LhPosition =
    { SignalSec: int
      SignalVwap: float
      // set at the FILL bar
      mutable EntrySec: int
      mutable EntryPx: float
      /// Present-bar index of the fill bar. The timestop counts BARS, so it needs
      /// the bar clock rather than the wall clock.
      mutable EntryBarIdx: int
      // ----- the 30-present-bar SLOT block. Volatility, every efficiency ratio
      // and the drawdown feature all read the SAME slot-vwap stream, anchored on
      // the session's opening slot (F5c: sub-30s returns are microstructure). -----
      Volat20m: float            // EmaHlMa hl=40 slots of |slot return|
      Volat10m: float            // hl=20 twin
      VolatOpen: float           // ⭐ plain mean |slot return| since the OPEN (no
                                 // decay) — the third member the user asked for.
      SlotCount: int             // completed slots this session (warmth for all of the above)
      Eff20m: float              // ln(V/V_40ago) / Sum40|r| in [-1,1]; nan until 41 slots
      Eff10m: float              // 20-slot twin; nan until 21 slots
      EffOpen: float             // ⭐ THE GATE: ln(V_last/V_first) / Sum_all|r|,
                                 // anchored on the opening slot. ⚠ NOT span-free.
      EffOpenSlots: int          // its span — condition on this, always (header note 2)
      // ----- ⭐ THE SLOT DRAWDOWN (user's design). Measured on slot vwaps, not
      // 1s bars, so it is not a noise statistic: d_t = ln(max(V_{t-39..t}) / V_t)
      // >= 0 is this slot's log distance below its own 40-slot high, and dd_20m
      // is the MAX of d over the last 40 slots — "the worst the move has been
      // underwater against its 20m high, in the last 20m". -----
      Dd20m: float
      Dd10m: float               // 20-slot twin of both halves
      DdNow20m: float            // the CURRENT slot's distance below its 40-slot high
      DdNow10m: float
      // ----- levels (raw prices; every distance is derived in SQL) -----
      OpenPx: float              // the session's FIRST present-bar vwap
      SessHi: float
      SessLo: float
      SessVwap: float            // cum_dv / cum_vol
      Hi60: float                // present-bar channel MAX, inclusive of the signal bar
      Hi120: float
      Hi300: float
      Hi600: float
      Hi1200: float
      Lo60: float                // ... and MIN. dist-from-1m-low = signal_vwap/lo_60 - 1
      Lo120: float
      Lo300: float
      Lo600: float
      Lo1200: float
      Vwap60: float              // rolling dv_60/vol_60
      Vwap60Prev: float          // ⭐ the same 60-bar vwap 60 bars ago — the SPEED
                                 // denominator (signal_vwap/vwap_60_prev - 1), the
                                 // momentum twin of FlushFader's flush speed
      Vwap300: float
      Vwap1200: float
      // ----- ⭐ TIME MEASURES (user): WALL-CLOCK seconds since the last strict new
      // N-present-bar high / low. -1 = no such event yet this session (the anchor
      // is then the open). Era-invariant, unlike a bar count — and the pair is
      // what makes "is the 1m low or the 1m high more recent" a comparison
      // instead of a chart impression. -----
      SecsSinceHi60: int
      SecsSinceHi120: int
      SecsSinceHi300: int
      SecsSinceHi600: int
      SecsSinceHi1200: int
      SecsSinceLo60: int
      SecsSinceLo120: int
      SecsSinceLo300: int
      SecsSinceLo600: int
      SecsSinceLo1200: int
      // ----- ⭐ THE RESEAT FAMILY (user): how many new 20m HIGHS have printed
      // since the last new N-bar LOW. A stock that has made eight fresh 20m highs
      // without touching a 5m low is in a different state from one that has made
      // one. Counts from the OPEN while the corresponding low has never fired. -----
      Highs20mSinceLo60: int
      Highs20mSinceLo120: int
      Highs20mSinceLo300: int
      Highs20mSinceLo600: int
      Highs20mSinceLo1200: int
      // ----- gaps: missing seconds in each trailing WALL-CLOCK window -----
      GapOpen: int               // session seconds elapsed minus present bars
      Gap10: int
      Gap30: int
      Gap60: int
      Gap120: int
      Gap300: int
      Gap600: int
      Gap1200: int
      // ----- activity at the SAME horizons as the gap family (user). Present-bar
      // windows; gap_N is exactly the correction between the two conventions. -----
      DvSess: float
      Dv10: float
      Dv30: float
      Dv60: float
      Dv120: float
      Dv300: float
      Dv600: float
      Dv1200: float
      TcSess: float
      Tc10: float
      Tc30: float
      Tc60: float
      Tc120: float
      Tc300: float
      Tc600: float
      Tc1200: float
      BarVol: float
      BarTc: int
      BarsPresent: int           // present bars folded this session (the warmth denominator)
      Dv0945Tape: float          // Sum vwap*volume over OUR 1s bars strictly before 09:45
      // ----- ⭐ THE TREND PAIR (user, 2026-08-23): OLS of ln(PRICE) and of
      // ln(VOLUME) against the present-bar index, over the same horizons — since
      // the OPEN, and trailing 1m / 5m / 10m / 20m. slope x 6e5 ~= bp/min for
      // price and ~= the same units of log-growth per minute for volume; r is
      // SIGNED (sign(slope) * sqrt(R2)) so it reads as trend quality WITH
      // direction, the convention the Fader engines use.
      //
      // ⭐ WHY BOTH: price slope alone cannot tell a move the tape is JOINING
      // from one it is abandoning. The Hitchhiker thesis is that the good ones
      // are both — price rising and participation rising — and the pair is what
      // makes that a testable statement instead of a chart impression.
      // ⚠ ols_*_open / vol_*_open grow their window all session; their span is
      // `bars_present`. Condition on it, exactly as with eff_open. -----
      OlsSlopeOpen: float
      OlsROpen: float
      OlsSlope60: float
      OlsR60: float
      OlsSlope300: float
      OlsR300: float
      OlsSlope600: float
      OlsR600: float
      OlsSlope1200: float
      OlsR1200: float
      VolSlopeOpen: float
      VolROpen: float
      VolSlope60: float
      VolR60: float
      VolSlope300: float
      VolR300: float
      VolSlope600: float
      VolR600: float
      VolSlope1200: float
      VolR1200: float
      OpenAtSignal: int          // trips already open in the engine when this one fired
                                 // (the honest add-index at mc = 0)
      // ----- forward marks: vwap at the first present bar >= entry + N WALL-CLOCK
      // seconds; nan if the day ends first. ⭐ THE HORIZON SWEEP LIVES HERE — the
      // HoldBars timestop is one choice, and `fwd_*` answers every other one
      // without a re-run. -----
      mutable Fwd30: float
      mutable Fwd60: float
      mutable Fwd120: float
      mutable Fwd300: float
      mutable Fwd600: float
      mutable Fwd1200: float
      mutable BarsHeld: int      // present bars from the fill bar to the exit-fill bar
      mutable State: LhPosState }

/// LongHiker config. The ER gate, two liquidity floors, the timestop, the clock.
/// Every other lever is a recorded column.
type IntradayConfig =
    { /// ⭐ THE SYSTEM: enter on every bar with eff_open >= this. SIGNED (long
      /// only), so a downtrend of equal quality does not qualify. A COLD
      /// eff_open (fewer than MinEffOpenSlots slots) FAILS — the standard
      /// unwarm-fails-an-armed-gate stance.
      MinEffOpen: float
      /// Slots required before eff_open is considered warm. 4 slots = 3 returns
      /// ~= 2 minutes of dense tape. ⚠ Raising this does NOT make eff_open
      /// span-free; it only moves the left edge of the drift.
      MinEffOpenSlots: int
      /// ⭐ THE EXIT: present bars held after the fill bar. 30 = the user's
      /// timestop. The fill is AT that bar's vwap (see header).
      HoldBars: int
      /// Fire only every Nth qualifying bar per (ticker, day). 1 = every bar
      /// (the design). > 1 is a UNIFORM SUBSAMPLE of the same signal set —
      /// unbiased for means, and the escape hatch if a full-period run's trip
      /// count is unmanageable. ⚠ It is NOT a filter: never report a stride run
      /// as a book.
      SignalStride: int
      /// Hard entry floors over the trailing 60 present bars: dollars traded and
      /// trades. Live-knowable, trailing-window, no lookahead. 0 = off.
      DvFloor60: float
      TcFloor60: float
      /// Record-first regime band on volat_20m (raw mean-|r| per 30s slot).
      /// 0 / infinity = off, which is the default: band it post-hoc.
      MinVolat20m: float
      MaxVolat20m: float
      /// 0 = the SAMPLER (unlimited concurrent trips — the design: every
      /// qualifying bar is an independent row). 1 = a real book.
      MaxConcurrent: int
      SlotBars: int              // 30 present bars — the slot clock, shared with the Faders
      SessionStartSec: int       // 34200 = 09:30; features fold from the RTH open
      EntryStartSec: int         // ⭐ 34800 = 09:40 (user). See header note 1.
      EntryEndSec: int
      EntryEndSecShort: int      // NYSE early-close days
      MocSec: int                // 57600 = 16:00
      MocSecShort: int }         // 46800 = 13:00

/// The channel horizons, in present bars: 1m / 2m / 5m / 10m / 20m. The order is
/// load-bearing — every per-window array below is indexed by it, and CH1200 is
/// the reseat numerator's window.
let CHANS = [| 60; 120; 300; 600; 1200 |]
[<Literal>]
let private CH1200 = 4

/// Trailing WALL-CLOCK gap windows, in seconds: open / 10s / 30s / 1m / 2m / 5m /
/// 10m / 20m (the `open` member is computed directly, not from a counter).
let GAP_SECS = [| 10; 30; 60; 120; 300; 600; 1200 |]

/// Forward-mark horizons, in WALL-CLOCK seconds after the fill. Order is
/// load-bearing: `fwdCur` and the dispatch in step 4 are indexed by it.
let FWD_SECS = [| 30; 60; 120; 300; 600; 1200 |]

[<Sealed>]
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly) =
    let isEarlyClose = TradingEdge.Orb.Timezone.early_closes.Contains day
    let entryEndSec = if isEarlyClose then cfg.EntryEndSecShort else cfg.EntryEndSec
    let mocSec = if isEarlyClose then cfg.MocSecShort else cfg.MocSec

    // ⭐⭐ THE LIFECYCLE IS POINTER-ADVANCE, NOT A PER-BAR SCAN. Trips are appended
    // to `all` in FILL order, so every deadline they carry — the timestop's bar
    // index and each forward mark's ET second — is NON-DECREASING down the list.
    // That makes each schedule a single monotone cursor: on every bar, advance
    // the cursor while the head trip's deadline has passed, and stop. Amortised
    // O(1) per bar instead of O(open trips), which at ~1,200 concurrent trips
    // over ~20,000 bars is the difference between a research run and a hang.
    //
    // ⚠ `all` is NEVER compacted or reordered — the cursors index into it
    // directly, and a compaction would silently invalidate every one of them.
    let all = ResizeArray<LhPosition>()
    let mutable pendingEntry : LhPosition voption = ValueNone
    let mutable exitCur = 0                      // first trip not yet exited
    /// One cursor per forward horizon, same order as FWD_SECS.
    let fwdCur = Array.zeroCreate<int> 6

    // ----- bar-level sums + OLS, all sharing ONE ring -----
    let dv10   = DvSum 10
    let dv30   = DvSum 30
    let dv60   = DvSum 60
    let dv120  = DvSum 120
    let dv300  = DvSum 300
    let dv600  = DvSum 600
    let dv1200 = DvSum 1200
    let tc10   = TcSum 10
    let tc30   = TcSum 30
    let tc60   = TcSum 60
    let tc120  = TcSum 120
    let tc300  = TcSum 300
    let tc600  = TcSum 600
    let tc1200 = TcSum 1200
    let vol60   = VolSum 60
    let vol300  = VolSum 300
    let vol1200 = VolSum 1200
    let pxOls60   = LogPxOls 60
    let pxOls300  = LogPxOls 300
    let pxOls600  = LogPxOls 600
    let pxOls1200 = LogPxOls 1200
    let vlOls60   = LogVolOls 60
    let vlOls300  = LogVolOls 300
    let vlOls600  = LogVolOls 600
    let vlOls1200 = LogVolOls 1200
    // ⚠⚠ THIS LIST *IS* THE PUSH BLOCK. A window that is not registered here is
    // never pushed, reads ValueNone forever, and its column silently goes NULL —
    // so adding a window means adding it in BOTH places.
    let barRoller =
        roller [|
            10, dv10 :> IRoll<RollBar>;   30, dv30;    60, dv60;    120, dv120
            300, dv300;  600, dv600;  1200, dv1200
            10, tc10;    30, tc30;    60, tc60;    120, tc120
            300, tc300;  600, tc600;  1200, tc1200
            60, vol60;   300, vol300; 1200, vol1200
            60, pxOls60; 300, pxOls300; 600, pxOls600; 1200, pxOls1200
            60, vlOls60; 300, vlOls300; 600, vlOls600; 1200, vlOls1200
        |]
    // the open-anchored twins of the same two regressions
    let pxOlsOpen = AnchoredOls()
    let vlOlsOpen = AnchoredOls()

    // ----- the slot block: ONE stream, anchored on the opening slot -----
    let slots = SlotVwapMa cfg.SlotBars
    let ew40 = EmaHlMa 40.0                      // volat_20m
    let ew20 = EmaHlMa 20.0                      // volat_10m
    let slotLag40 = LagMa<float> 40              // eff_20m numerator
    let slotLag20 = LagMa<float> 20              // eff_10m numerator
    let slotAbs40 = SumMa 40                     // eff_20m denominator
    let slotAbs20 = SumMa 20
    let slotMax40 = MaxMa 40                     // the drawdown reference highs
    let slotMax20 = MaxMa 20
    let ddRun40 = MaxMa 40                       // MaxMa OF the slot drawdowns -> dd_20m
    let ddRun20 = MaxMa 20
    let mutable prevSlotVwap : float voption = ValueNone
    let mutable firstSlotVwap = nan
    let mutable lastSlotVwap = nan
    let mutable slotCount = 0
    let mutable slotAbsCum = 0.0                 // Sum|r| since the open — eff_open / volat_open
    let mutable slotRetN = 0
    let mutable ddNow40 = nan
    let mutable ddNow20 = nan

    // ----- channels, one entry per CHANS index -----
    let maxCh = CHANS |> Array.map MaxMa
    let minCh = CHANS |> Array.map MinMa
    let priorMax : float voption[] = Array.create CHANS.Length ValueNone
    let priorMin : float voption[] = Array.create CHANS.Length ValueNone
    let lastHiSec = Array.create CHANS.Length -1
    let lastLoSec = Array.create CHANS.Length -1
    /// New 20m highs since the last N-bar low (0 = at/since that low; while the
    /// low has never fired the anchor is the session open).
    let hiSinceLo = Array.create CHANS.Length 0

    // ----- gaps, session, speed -----
    let gaps = GAP_SECS |> Array.map (fun w -> GapCounter(w, cfg.SessionStartSec))
    let sessHi = RunMaxMa<float>()
    let sessLo = RunMinMa<float>()
    let vwap60Lag = LagMa<float> 60              // the 60-bar vwap one minute ago
    let mutable cumVol = 0.0
    let mutable cumDv = 0.0
    let mutable cumTc = 0.0
    let mutable dv0945Tape = 0.0
    let mutable barsPresent = 0                  // ALSO the present-bar index clock (1-based)
    let mutable openPx = nan
    let mutable sinceLastSignal = 0              // the SignalStride counter

    let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
    let sumv (s: SumRoll<RollBar>) = vv s.State
    /// SIGNED Pearson r: sign(slope) * sqrt(R2). nan on a degenerate window.
    let olsR (o: OlsRoll<RollBar>) =
        match o.Slope, o.R2 with
        | ValueSome sl, ValueSome r2 -> (if sl < 0.0 then -1.0 else 1.0) * sqrt r2
        | _ -> nan
    let olsSlope (o: OlsRoll<RollBar>) = vv o.Slope
    let secsSince (last: int) (now: int) = if last < 0 then -1 else now - last

    member _.Ticker = ticker
    member _.Day = day
    /// Every trip this ticker-day produced, in fill order.
    member _.Positions = all :> seq<LhPosition>
    /// Filled trips that have not yet exited.
    member _.OpenCount = all.Count - exitCur
    member this.HasSlot =
        cfg.MaxConcurrent <= 0
        || this.OpenCount + (if pendingEntry.IsSome then 1 else 0) < cfg.MaxConcurrent

    /// Advance the system by one PRESENT 1s bar. Bars arrive in etSec order, RTH
    /// only (the emitter filters to [SessionStartSec, MocSec]).
    member this.Process (bar: SecBar) =
        if bar.etSec < cfg.SessionStartSec then () else

        // ===== 1. snapshot the STRICTLY-PRIOR channel extremes =====
        for i in 0 .. CHANS.Length - 1 do
            priorMax.[i] <- maxCh.[i].State
            priorMin.[i] <- minCh.[i].State

        // ===== 2. fold this bar into every structure =====
        barsPresent <- barsPresent + 1
        if Double.IsNaN openPx then openPx <- bar.vwap
        cumVol <- cumVol + bar.volume
        cumTc <- cumTc + float bar.tradeCount
        cumDv <- cumDv + bar.vwap * bar.volume
        // 35100 = 09:45, THE knowability floor (docs/lookahead_protocol.md R4/R5)
        if bar.etSec < 35100 then dv0945Tape <- dv0945Tape + bar.vwap * bar.volume
        for g in gaps do g.Push bar.etSec
        barRoller.Push (rollBar bar.vwap bar.volume bar.tradeCount)
        pxOlsOpen.Push (log bar.vwap)
        vlOlsOpen.Push (log bar.volume)
        // the 60-bar vwap, pushed once per WARM bar so .Lagged is the previous
        // NON-OVERLAPPING minute's value — the speed denominator
        if vol60.Count = vol60.WindowSize then
            match dv60.State, vol60.State with
            | ValueSome d, ValueSome v when v > 0.0 -> vwap60Lag.Push (d / v)
            | _ -> ()
        for i in 0 .. CHANS.Length - 1 do
            maxCh.[i].Push bar.vwap
            minCh.[i].Push bar.vwap
        sessHi.Push bar.vwap
        sessLo.Push bar.vwap

        // ----- the slot chain: one |r| per completed 30-bar slot -----
        match slots.Push(bar.vwap, bar.volume) with
        | ValueSome v ->
            (match prevSlotVwap with
             | ValueSome pv when pv > 0.0 && v > 0.0 ->
                 let ar = abs (log (v / pv))
                 ew40.Push ar
                 ew20.Push ar
                 slotAbs40.Push ar
                 slotAbs20.Push ar
                 slotAbsCum <- slotAbsCum + ar
                 slotRetN <- slotRetN + 1
             | _ -> ())
            slotLag40.Push v
            slotLag20.Push v
            // ⚠ ORDER: the slot's own vwap goes into the reference high FIRST, so
            // the distance is measured against a high that INCLUDES it and is
            // therefore >= 0 by construction; then that distance goes into the
            // running max. Reversing either step silently changes the feature.
            slotMax40.Push v
            slotMax20.Push v
            ddNow40 <- (match slotMax40.State with ValueSome h when h > 0.0 && v > 0.0 -> log (h / v) | _ -> nan)
            ddNow20 <- (match slotMax20.State with ValueSome h when h > 0.0 && v > 0.0 -> log (h / v) | _ -> nan)
            if not (Double.IsNaN ddNow40) then ddRun40.Push ddNow40
            if not (Double.IsNaN ddNow20) then ddRun20.Push ddNow20
            if slotCount = 0 then firstSlotVwap <- v
            lastSlotVwap <- v
            slotCount <- slotCount + 1
            prevSlotVwap <- ValueSome v
        | ValueNone -> ()

        // ===== 3. channel events: time stamps + the reseat counters =====
        // Strict inequalities on BOTH sides (the F21 phantom-tie rule: two
        // consecutive identical prints are not a new extreme). Lows are applied
        // before highs; the two are mutually exclusive on one bar anyway, since
        // prior_max_1200 >= prior_min_N for every N.
        for i in 0 .. CHANS.Length - 1 do
            match priorMin.[i] with
            | ValueSome lo when bar.vwap < lo ->
                lastLoSec.[i] <- bar.etSec
                hiSinceLo.[i] <- 0
            | _ -> ()
        let isNewHi1200 =
            match priorMax.[CH1200] with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        for i in 0 .. CHANS.Length - 1 do
            match priorMax.[i] with
            | ValueSome hi when bar.vwap > hi -> lastHiSec.[i] <- bar.etSec
            | _ -> ()
        if isNewHi1200 then
            for i in 0 .. CHANS.Length - 1 do
                hiSinceLo.[i] <- hiSinceLo.[i] + 1

        // ===== 4. forward marks — one monotone cursor per horizon =====
        // Marks are filled for EXITED trips too: the whole point of recording
        // them is that the HoldBars timestop is one choice among many, and
        // `fwd_vwap_*` answers every other horizon without a re-run.
        for h in 0 .. FWD_SECS.Length - 1 do
            let dl = FWD_SECS.[h]
            let mutable c = fwdCur.[h]
            while c < all.Count && bar.etSec >= all.[c].EntrySec + dl do
                let p = all.[c]
                (match h with
                 | 0 -> p.Fwd30 <- bar.vwap
                 | 1 -> p.Fwd60 <- bar.vwap
                 | 2 -> p.Fwd120 <- bar.vwap
                 | 3 -> p.Fwd300 <- bar.vwap
                 | 4 -> p.Fwd600 <- bar.vwap
                 | _ -> p.Fwd1200 <- bar.vwap)
                c <- c + 1
            fwdCur.[h] <- c

        // ===== 5. exits: the timestop cursor, then the MOC drain =====
        while exitCur < all.Count && all.[exitCur].EntryBarIdx + cfg.HoldBars <= barsPresent do
            let p = all.[exitCur]
            p.BarsHeld <- barsPresent - p.EntryBarIdx
            p.State <- ExitedAt (bar.etSec, bar.vwap, "timestop")
            exitCur <- exitCur + 1
        // ⭐ The 16:00 bar IS the auction-proximate print, so MOC fills here rather
        // than next bar. Entries stop at EntryEndSec < MocSec, so this drains once.
        if bar.etSec >= mocSec then
            while exitCur < all.Count do
                let p = all.[exitCur]
                p.BarsHeld <- barsPresent - p.EntryBarIdx
                p.State <- ExitedAt (bar.etSec, bar.vwap, "moc")
                exitCur <- exitCur + 1

        // ===== 6. fill the pending entry at THIS bar's vwap =====
        // ⚠⚠ AFTER steps 4 and 5, DELIBERATELY. A trip filled on this bar must not
        // be marked or aged by this same bar: appending here makes EntryBarIdx the
        // fill bar's index, so the timestop in step 5 fires exactly HoldBars
        // present bars later. It is also why the just-filled trip is visible to
        // `OpenAtSignal` below, as it should be.
        match pendingEntry with
        | ValueSome p ->
            p.EntrySec <- bar.etSec
            p.EntryPx <- bar.vwap
            p.EntryBarIdx <- barsPresent
            all.Add p
            pendingEntry <- ValueNone
        | ValueNone -> ()

        // ===== 7. the entry signal (fills next bar) =====
        let effOpen =
            if slotCount >= cfg.MinEffOpenSlots && slotAbsCum > 0.0
               && firstSlotVwap > 0.0 && lastSlotVwap > 0.0
            then log (lastSlotVwap / firstSlotVwap) / slotAbsCum
            else nan
        let inWindow = bar.etSec >= cfg.EntryStartSec && bar.etSec <= entryEndSec
        // ⚠ a NaN eff_open fails this comparison, which is the intended
        // unwarm-fails-an-armed-gate behaviour — not an accident of IEEE.
        let effOk = effOpen >= cfg.MinEffOpen
        let floorsOk =
            (cfg.DvFloor60 <= 0.0 || (match dv60.State with ValueSome d -> d >= cfg.DvFloor60 | ValueNone -> false))
            && (cfg.TcFloor60 <= 0.0 || (match tc60.State with ValueSome t -> t >= cfg.TcFloor60 | ValueNone -> false))
        let volatOk =
            (cfg.MinVolat20m <= 0.0
             || (match ew40.State with ValueSome v -> v >= cfg.MinVolat20m | ValueNone -> false))
            && (Double.IsPositiveInfinity cfg.MaxVolat20m
                || (match ew40.State with ValueSome v -> v < cfg.MaxVolat20m | ValueNone -> true))
        if inWindow && effOk && floorsOk && volatOk && bar.etSec < mocSec && this.HasSlot then
            sinceLastSignal <- sinceLastSignal + 1
            if cfg.SignalStride <= 1 || sinceLastSignal >= cfg.SignalStride then
                sinceLastSignal <- 0
                let vwapOf (d: DvSum) (v: VolSum) =
                    match d.State, v.State with
                    | ValueSome dv, ValueSome vo when vo > 0.0 -> dv / vo
                    | _ -> nan
                pendingEntry <-
                    ValueSome
                        { SignalSec = bar.etSec
                          SignalVwap = bar.vwap
                          EntrySec = 0
                          EntryPx = nan
                          EntryBarIdx = -1
                          Volat20m = vv ew40.State
                          Volat10m = vv ew20.State
                          VolatOpen = (if slotRetN > 0 then slotAbsCum / float slotRetN else nan)
                          SlotCount = slotCount
                          Eff20m =
                            (match slotLag40.Lagged, slotAbs40.State with
                             | ValueSome v0, ValueSome sa when slotAbs40.Count = slotAbs40.WindowSize && sa > 0.0 && v0 > 0.0
                                 -> log (lastSlotVwap / v0) / sa
                             | _ -> nan)
                          Eff10m =
                            (match slotLag20.Lagged, slotAbs20.State with
                             | ValueSome v0, ValueSome sa when slotAbs20.Count = slotAbs20.WindowSize && sa > 0.0 && v0 > 0.0
                                 -> log (lastSlotVwap / v0) / sa
                             | _ -> nan)
                          EffOpen = effOpen
                          EffOpenSlots = slotCount
                          Dd20m = vv ddRun40.State
                          Dd10m = vv ddRun20.State
                          DdNow20m = ddNow40
                          DdNow10m = ddNow20
                          OpenPx = openPx
                          SessHi = vv sessHi.State
                          SessLo = vv sessLo.State
                          SessVwap = (if cumVol > 0.0 then cumDv / cumVol else nan)
                          Hi60 = vv maxCh.[0].State
                          Hi120 = vv maxCh.[1].State
                          Hi300 = vv maxCh.[2].State
                          Hi600 = vv maxCh.[3].State
                          Hi1200 = vv maxCh.[4].State
                          Lo60 = vv minCh.[0].State
                          Lo120 = vv minCh.[1].State
                          Lo300 = vv minCh.[2].State
                          Lo600 = vv minCh.[3].State
                          Lo1200 = vv minCh.[4].State
                          Vwap60 = vwapOf dv60 vol60
                          Vwap60Prev = vv vwap60Lag.Lagged
                          Vwap300 = vwapOf dv300 vol300
                          Vwap1200 = vwapOf dv1200 vol1200
                          SecsSinceHi60 = secsSince lastHiSec.[0] bar.etSec
                          SecsSinceHi120 = secsSince lastHiSec.[1] bar.etSec
                          SecsSinceHi300 = secsSince lastHiSec.[2] bar.etSec
                          SecsSinceHi600 = secsSince lastHiSec.[3] bar.etSec
                          SecsSinceHi1200 = secsSince lastHiSec.[4] bar.etSec
                          SecsSinceLo60 = secsSince lastLoSec.[0] bar.etSec
                          SecsSinceLo120 = secsSince lastLoSec.[1] bar.etSec
                          SecsSinceLo300 = secsSince lastLoSec.[2] bar.etSec
                          SecsSinceLo600 = secsSince lastLoSec.[3] bar.etSec
                          SecsSinceLo1200 = secsSince lastLoSec.[4] bar.etSec
                          Highs20mSinceLo60 = hiSinceLo.[0]
                          Highs20mSinceLo120 = hiSinceLo.[1]
                          Highs20mSinceLo300 = hiSinceLo.[2]
                          Highs20mSinceLo600 = hiSinceLo.[3]
                          Highs20mSinceLo1200 = hiSinceLo.[4]
                          GapOpen = max 0 (bar.etSec - cfg.SessionStartSec + 1 - barsPresent)
                          Gap10 = gaps.[0].Gaps
                          Gap30 = gaps.[1].Gaps
                          Gap60 = gaps.[2].Gaps
                          Gap120 = gaps.[3].Gaps
                          Gap300 = gaps.[4].Gaps
                          Gap600 = gaps.[5].Gaps
                          Gap1200 = gaps.[6].Gaps
                          DvSess = cumDv
                          Dv10 = sumv dv10
                          Dv30 = sumv dv30
                          Dv60 = sumv dv60
                          Dv120 = sumv dv120
                          Dv300 = sumv dv300
                          Dv600 = sumv dv600
                          Dv1200 = sumv dv1200
                          TcSess = cumTc
                          Tc10 = sumv tc10
                          Tc30 = sumv tc30
                          Tc60 = sumv tc60
                          Tc120 = sumv tc120
                          Tc300 = sumv tc300
                          Tc600 = sumv tc600
                          Tc1200 = sumv tc1200
                          BarVol = bar.volume
                          BarTc = bar.tradeCount
                          BarsPresent = barsPresent
                          Dv0945Tape = dv0945Tape
                          OlsSlopeOpen = pxOlsOpen.Slope
                          OlsROpen = pxOlsOpen.R
                          OlsSlope60 = olsSlope pxOls60
                          OlsR60 = olsR pxOls60
                          OlsSlope300 = olsSlope pxOls300
                          OlsR300 = olsR pxOls300
                          OlsSlope600 = olsSlope pxOls600
                          OlsR600 = olsR pxOls600
                          OlsSlope1200 = olsSlope pxOls1200
                          OlsR1200 = olsR pxOls1200
                          VolSlopeOpen = vlOlsOpen.Slope
                          VolROpen = vlOlsOpen.R
                          VolSlope60 = olsSlope vlOls60
                          VolR60 = olsR vlOls60
                          VolSlope300 = olsSlope vlOls300
                          VolR300 = olsR vlOls300
                          VolSlope600 = olsSlope vlOls600
                          VolR600 = olsR vlOls600
                          VolSlope1200 = olsSlope vlOls1200
                          VolR1200 = olsR vlOls1200
                          OpenAtSignal = this.OpenCount
                          Fwd30 = nan
                          Fwd60 = nan
                          Fwd120 = nan
                          Fwd300 = nan
                          Fwd600 = nan
                          Fwd1200 = nan
                          BarsHeld = 0
                          State = Holding }

    /// Close the day: every trip still holding exits at the final bar's vwap.
    /// Unfilled forward marks stay nan — the day simply ended first, and NULL is
    /// the honest value.
    member _.Flatten (lastBar: SecBar) =
        // ⚠ A signal on the day's LAST bar is DISCARDED, not filled here: it has
        // no next present bar, and filling it at the signal bar's own vwap would
        // be a free zero-slippage entry the live system could never take.
        pendingEntry <- ValueNone
        while exitCur < all.Count do
            let p = all.[exitCur]
            p.BarsHeld <- barsPresent - p.EntryBarIdx
            p.State <- ExitedAt (lastBar.etSec, lastBar.vwap, "eod")
            exitCur <- exitCur + 1
