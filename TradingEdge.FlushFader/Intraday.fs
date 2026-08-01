module TradingEdge.FlushFader.Intraday

open System
open TradingEdge.RollingMa

// ===========================================================================
// FlushFader — 1-SECOND-bar intraday MEAN REVERSION (long-only): the
// production-tier successor to DipRiderV6 (1m), running on the SurgeRider
// engine. First of the Fader family (FlushFader long, SpikeFader short,
// MinFader/MaxFader hold-to-close). Design: docs/flushfader_results.md.
//
// THE SYSTEM (sampler form — record, don't gate):
//   ENTRY SIGNAL: the vwap prints a NEW ~20m LOW — strictly under the
//         STRICTLY-PRIOR EntryChannelBars-bar MIN (strict `<`: V6's F21
//         phantom-tie fix — `<=` re-fired on round-number pinning ties).
//         Hard floors: 60-bar dollar-volume AND trade count, inside the
//         entry window. mc = 0 averages down; every further new low is
//         another trip.
//   FILL: the NEXT present bar's vwap (the 1s dataset has no close column, and
//         the next-bar vwap is the honest "you traded into the following
//         second" fill). Exits fill the same way.
//   EXIT SIGNAL: the vwap strictly EXCEEDS the strictly-prior
//         ExitChannelBars-bar MAX (the ~5m-high reversion target; strict `>`
//         everywhere is the house rule after F21 — deliberate deviation from
//         V6's inclusive `>=`), or a PRICE-ACCEPTANCE STOP, or MOC.
//   ⭐ PRICE-ACCEPTANCE STOPS (user, 2026-07-28; Lance's framing): while
//         holding, a NEW entry-channel LOW made (a) on >= VolStopRatio 1m/20m
//         volume, (b) on >= TcStopRatio 1m/20m trade count, or (c) at a 1m
//         pace under SpeedStopPct — the market ACCEPTING the lower price;
//         the flush is continuing, not snapping. These are NOT price-level
//         stops: V6 F6 measured a level stop as DESTRUCTIVE for this shape
//         (PF 1.429 -> 1.164) — it fires exactly where MR buys. Acceptance
//         stops fire only on qualified FRESH lows. The S2 tables motivate
//         them: >=8x participation new lows run PF 0.59-0.87 at entry, and
//         sustained <-1% pace is the continuing-flush signature.
//   THE LEG MACHINE (NewLowCounters, ported verbatim from DipRiderV6):
//         armed by the leg's FIRST new low; BarsSinceFirstLow counts every
//         bar, LowsSinceFirstLow counts further new lows (averaging-down
//         depth); ⭐ RESET on a new ENTRY-channel HIGH (the user's "reset at
//         the 20m high" — V6 reset on its exit window, which at 20/20
//         coincided; with a 5m exit that would end legs mid-flush, so the
//         reset is welded to the ENTRY channel here). Books are built
//         POST-HOC by greedy mc-replay (S38) — the old V6 F3 one-trip-per-leg
//         K-gate latch (MinLowsIntoLeg + legConsumed) is deleted.
//
// ⭐ SAMPLER, NOT A BOOK (mc = 0 default): every qualifying bar becomes an
// independent trip with its full feature vector + forward marks; PF on the raw
// output is ATTRIBUTION, not portfolio. Re-run chosen cells at mc=1.
//
// ⭐ PRESENT-BAR SEMANTICS (D1): the engine steps ONLY on bars that exist in
// data/intraday_1s_slim/ (seconds with >= 1 kept trade). Every window is a
// PRESENT-BAR-COUNT window — "60 bars" spans 60+ wall-clock seconds on any
// name with gaps. The GapCounter features (gap60/30/15 = missing seconds in
// the trailing wall-clock window) measure exactly what this convention hides;
// gap60 = 0 certifies present-bar ~= wall-clock locally.
//
// ⭐ THE REGIME BLOCK replaces V6's log_atr_20/adx_14 with the F1-F8
// bake-off lock (docs/surgerider_results.md):
//   30-present-bar slot vwaps (SlotVwapMa) -> r = ln(V/V_prev) -> EmaHlMa of
//   |r| at hl = 40 slots (volat_20m) + hl = 20 slots (volat_10m); rng_20m =
//   ln(high/low) of the 1200-bar vwap channel; eff_20m = ln(V/V_40ago) /
//   Sum40|r| ∈ [-1,1] (SIGNED drift t-stat; |eff| = trendiness — the ADX
//   replacement) + the 20-slot eff_10m twin.
//   ⚠ NAMING (user, 2026-07-28): volat_* = VOLATILITY; vol_* = VOLUME. The
//   old engines called both "vol".
//
// Live-safe: every feature folds from D's own realized bars; the entry window
// floor is 09:45 (etSec 35100), the knowability floor for the 09:45 universe
// context fields (docs/lookahead_protocol.md). Default start 10:00 (V6's
// research window).
// ===========================================================================

/// One 1-second present bar from data/intraday_1s_slim/, split-adjusted to the
/// candidate's daily scale. `etSec` = the `bucket` column: seconds since 00:00
/// ET (RTH open = 34200, 09:45 = 35100, 16:00 = 57600). volume is FLOAT — the
/// tape carries genuine fractional shares — and arrives SPLIT-ADJUSTED (raw
/// shares / adj_ratio, mirroring vwap = raw × adj_ratio) so vwap·volume is
/// honest dollars (S29 fix, 2026-07-29).
type SecBar =
    { etSec: int
      vwap: float
      volume: float
      tradeCount: int }

/// Missing-second counter over a trailing WALL-CLOCK window (user, 2026-07-23):
/// how many of the last `windowSecs` seconds (inclusive of the current bar's)
/// had NO present bar. The engine's windows are present-bar-count (D1); this is
/// the feature that records what that convention skips. `Push` the bar's etSec,
/// then read `.Gaps`. Session-start clamp: before the window fills with session
/// seconds, the denominator is the elapsed session span, so the first RTH bars
/// don't read as one giant gap.
[<Sealed>]
type GapCounter(windowSecs: int, sessionStartSec: int) =
    let q = System.Collections.Generic.Queue<int>()
    let mutable lastSec = -1
    member _.Push (sec: int) =
        q.Enqueue sec
        while q.Peek() <= sec - windowSecs do
            q.Dequeue() |> ignore
        lastSec <- sec
    /// Missing seconds in the trailing window as of the last Push.
    member _.Gaps =
        if lastSec < 0 then 0
        else
            let span = min windowSecs (lastSec - sessionStartSec + 1)
            max 0 (span - q.Count)
    member _.Reset () =
        q.Clear()
        lastSec <- -1

/// Bars-since-last-channel-breach, one per channel per side. -1 = this
/// channel's extreme has not been breached this session; 0 = the CURRENT bar
/// breached it; N = N present bars ago. Step FIRST each bar, then OnBreach if
/// the bar broke the channel extreme, so the breach bar itself reads 0.
[<Sealed>]
type BreachCounter() =
    let mutable bars = -1
    member _.BarsSinceBreach = bars
    member _.Step () = if bars >= 0 then bars <- bars + 1
    member _.OnBreach () = bars <- 0
    member _.Reset () = bars <- -1

/// ⭐ The down-leg reset machine, ported verbatim from DipRiderV6.
/// Armed by the leg's FIRST new low; disarmed (Reset) by a new entry-channel
/// high. The two counters separate HOW DEEP INTO THE AVERAGING-DOWN SEQUENCE a
/// trade is from HOW STALE the leg is:
///   `LowsSinceFirstLow = 0` -> the FIRST low of the leg (the initial dip).
///   `LowsSinceFirstLow = 3` -> the 4th consecutive new low (averaged down 3x).
///   `BarsSinceFirstLow`     -> the leg's age in present bars.
[<Sealed>]
type NewLowCounters() =
    let mutable bars = -1
    let mutable lows = -1
    /// Bars since the FIRST new low of this down-leg. -1 = disarmed (no leg open).
    member _.BarsSinceFirstLow = bars
    /// Further new lows since the first of this leg. -1 = disarmed; 0 = this IS the
    /// first low; N = the (N+1)th low, i.e. averaged down N times.
    member _.LowsSinceFirstLow = lows
    /// Is a down-leg currently open?
    member _.Armed = bars >= 0
    /// A new low fired: arm the leg (idempotent), or count another low into it.
    member _.OnNewLow () =
        if bars < 0 then
            bars <- 0
            lows <- 0
        else
            lows <- lows + 1
    /// Advance one bar. No-op while disarmed.
    member _.Step () = if bars >= 0 then bars <- bars + 1
    /// A new entry-channel high fired: the down-leg is over — disarm.
    member _.Reset () =
        bars <- -1
        lows <- -1

/// Trip life-cycle. Exit SIGNALS are detected at a bar's close but FILL at the
/// next present bar's vwap — PendingExit carries the reason across that bar.
type IntraPosState =
    | Holding
    | PendingExit of reason: string
    | ExitedAt of exitSec: int * exitPx: float * reason: string
      // "target" | "vol_stop" | "tc_stop" | "speed_stop" | "moc"

/// One sampler trip. Features are the state at the SIGNAL bar's close
/// (inclusive of the signal bar — it has closed; not lookahead). The fill is
/// the NEXT present bar's vwap. ⭐ NOTHING here gates (beyond the hard entry
/// gates); it is all recorded for post-hoc SQL.
type FlushPosition =
    { SignalSec: int             // the gate bar (features captured here)
      SignalVwap: float          // its vwap — entry slippage = EntryPx/SignalVwap
      EntrySec: int              // the fill bar
      EntryPx: float             // the fill: next present bar's vwap
      // ----- the regime block (replaces V6's log_atr_20 / adx_14) -----
      Volat20m: float            // EmaHlMa hl=40 slots of |slot return| — THE volatility driver
      Volat10m: float            // hl=20 twin (trajectory: Volat10m << Volat20m = vol collapsing)
      Rng20m: float              // ln(high/low) of the 1200-bar vwap channel (F8 complement)
      Eff20m: float              // SIGNED ln(V/V_40slots_ago) / Sum40|r| ∈ [-1,1]. |eff| =
                                 // trendiness (drift t-stat — the ADX replacement); sign = the
                                 // 20m net direction. At a flush entry expect eff < 0.
                                 // ⭐ S38n: warms at 41 slots ≈ 1,230 bars — one slot AFTER the
                                 // entry channel; cold-at-signal now FAILS the spec (v1.6).
      Eff10m: float              // 20-slot twin (the 10m drift t-stat)
      SlotCount: int             // slot returns folded so far (volat/eff warmth)
      // ----- S40 (user 2026-08-01): slot-vwap RANGE twins of the eff pair.
      // Numerator = ln(hi/lo) over the SAME 41/21-slot-vwap span the eff returns
      // cover; eff_rng = that range over the SAME Σ|r| denominator. Direction-
      // blind; range >= |net| so eff_rng_* >= |eff_*|. nan until the span is
      // full (same warmth as eff_20m/eff_10m). Record-only. -----
      RngSlots20m: float         // ln(hi/lo) of the last 41 slot vwaps
      RngSlots10m: float         // 21-slot-vwap twin
      EffRng20m: float           // rng_slots_20m / Sum40|r| ∈ (0,1]
      EffRng10m: float           // rng_slots_10m / Sum20|r|
      // ----- channel widths, ln(high/low) per present-bar window -----
      RngSess: float
      Rng600: float
      Rng300: float
      Rng120: float
      Rng60: float
      Rng30: float
      Hi60: float                // S40: the raw 60-bar vwap MAX (rng_60 can't recover it) —
                                 // dist-from-1m-high = signal_vwap/hi_60 - 1 (flush-speed study)
      // ----- bars since each channel's HIGH was last breached (-1 = never) -----
      BreachSess: int
      Breach1200: int
      Breach600: int
      Breach300: int
      Breach120: int
      Breach60: int
      Breach30: int
      // ----- bars since each channel's LOW was last breached (-1 = never).
      // breach_lo_sess = 0 reproduces V6's is_new_sess_low free-fall flag. -----
      BreachLoSess: int
      BreachLo1200: int
      BreachLo600: int
      BreachLo300: int
      BreachLo120: int
      BreachLo60: int
      BreachLo30: int
      // ----- ⭐ the down-leg counters (the point of V6), read at the signal
      // bar AFTER its OnNewLow — the entry bar IS a low, so both >= 0. -----
      BarsSinceFirstLow: int     // the leg's age in present bars
      LowsSinceFirstLow: int     // averaging-down depth (0 = the leg's first low)
      // ⭐ S38e: same events, TIGHTER resets — leg disarmed by a new 5m/10m
      // high instead of the 20m one (record-only)
      BarsSinceFirstLow300: int
      LowsSinceFirstLow300: int
      BarsSinceFirstLow600: int
      LowsSinceFirstLow600: int
      TradeIdx: int              // ⭐ index of this SIGNAL within the down-leg (0 = the leg's
                                 // first trade); reset by the new-high leg reset. Diverges from
                                 // LowsSinceFirstLow wherever a low fired no trade (outside the
                                 // window, floors failed, slots full, K-gate).
      OpenAtSignal: int          // trades already OPEN in the engine when this signal fired
                                 // (shared exits => concurrent trips exit together; the
                                 // mechanically honest add-index at mc=0)
      // ----- location levels at the signal -----
      Vwap1200: float            // the 20m ROLLING VWAP (dv_1200/vol_1200) — "the 20m MA"
      ChanHi: float              // the STRICTLY-PRIOR ENTRY-channel max at the signal:
                                 // depth-into-leg = ln(signal_vwap/chan_hi) <= 0
      ChanLo: float              // the STRICTLY-PRIOR ENTRY-channel min — the level this entry
                                 // broke: depth-below-break = ln(signal_vwap/chan_lo) < 0
      ExitChanHi: float          // the STRICTLY-PRIOR EXIT-channel max — the reversion target:
                                 // distance-to-target = ln(exit_chan_hi/signal_vwap) > 0
      // ----- the gap counts (what present-bar windows hide) -----
      Gap60: int
      Gap30: int
      Gap15: int
      // ----- location -----
      SessVwap: float
      DistSessVwap: float        // ln(vwap / session vwap)
      PctChgOpen: float          // vwap / first-RTH-bar vwap - 1
      // ----- raw activity levels (log twins = ln() in SQL) -----
      BarVol: float
      BarTc: int
      Vol5: float                // 5s/10s tails (exhaustion-fade contrast vs the 1m rate)
      Vol10: float
      Vol15: float
      Vol30: float
      Vol60: float
      Vol600: float              // 10m volume sum (mid-horizon participation ratios)
      Vol1200: float             // 20m sums — the absolute 1m-vs-10m-vs-20m comparisons
      Tc5: float                 // 5s/10s tails, tc twins
      Tc10: float
      Tc15: float
      Tc30: float
      Tc60: float
      Tc600: float
      Tc1200: float
      Vol60Prev: float           // the PREVIOUS non-overlapping minute's sums (60-bar lag of the
      Tc60Prev: float            // 60-sums) — minute-over-minute activity SLOPE
      Vwap60: float              // ⭐ the CURRENT rolling 60-bar vwap (dv_60/vol_60)
      Vwap60Prev: float          // ⭐ the rolling 60-bar vwap 60 bars ago (previous non-
                                 // overlapping minute) — flush speed = signal_vwap/vwap_60_prev-1
                                 // (user, 2026-07-28; replaces the noisy two-point vwap_60_ago)
      DollarVol60: float         // Sum60 of vwap*volume — the liquidity-floor value
      CumVol: float
      CumTc: float
      Dv0945Tape: float          // ⭐ Σ vwap·volume over OUR 1s bars strictly before
                                 // 09:45 (honest dollars, v7 scale) — the live-scanner-
                                 // consistent dv_0945; compare vs the candidate column
      // ----- ⭐ S38q rolling OLS of ln(vwap) vs time, 600/1200 present bars.
      // slope = ln per present bar toward the present (×60 ≈ ln/min; ×6e5 ≈
      // bp/min); r = Pearson (r < 0 at a flush). nan on degenerate windows. -----
      OlsSlope300: float           // ⭐ S39p: the 5m twin (300 present bars)
      OlsR300: float
      OlsSlope600: float
      OlsR600: float
      OlsSlope1200: float
      OlsR1200: float
      // ----- ⭐ S39c rolling N_eff (record-only): Shannon perplexity + HHI of the
      // volume distribution over the last 600/1200 PRESENT bars — "how many
      // effective seconds carry the recent volume" (RollingMa NEff monoids in a
      // SlidingAgg; inverse-free). nan until the window is full. -----
      NEffShannon600: float
      NEffHhi600: float
      NEffShannon1200: float
      NEffHhi1200: float
      // ----- ⭐ S39i (user): N_eff (Shannon) of |30s-slot log returns| — the SAME
      // stream the eff ratios consume — 40 slot-returns (20m) / 20 (10m): trend
      // SMOOTHNESS, the candidate replacement for the Kaufman eff ratios. High
      // (near window size) = movement spread evenly; low = gap-and-chop.
      // nan until the window is full (same warmth as eff_20m/eff_10m). -----
      NEffRet20m: float
      NEffRet10m: float
      // ----- forward marks (vwap at the first present bar >= entry + horizon; nan if the day ends first) -----
      FwdVwap60: float
      FwdVwap300: float
      FwdVwap600: float
      FwdVwap1200: float
      // ----- ⭐ AUX-HIGH marks, retargeted for MR: the post-hoc EXIT-WINDOW SWEEP.
      // The first NEW {120,300,600,1200}-present-bar HIGH made STRICTLY AFTER the entry
      // fill bar, MARKED AT THE FOLLOWING BAR's vwap (the fill discipline). Detection is
      // the counter formulation: at the current bar, look up the PREVIOUS bar's
      // breach-counter snapshot — if the mark is still nan and that snapshot's
      // bars-since-high is 0 (the previous bar printed the high), the mark fills at THIS
      // bar's vwap. px = nan / sec = -1 until hit. Prototype "exit at the N-bar high"
      // books in SQL without re-running:
      //   ret = if aux_sec <= exit_sec then aux_px/entry-1 else ret_exit.
      // aux_hi_300 ~= the real exit at the defaults (both strict `>`; aux additionally
      // requires the high strictly after the fill bar) — near-agreement is a smoke test.
      // Tracked until retirement (>= +20m after entry), like the forward marks. -----
      AuxHi120: float
      AuxSec120: int
      AuxHi300: float
      AuxSec300: int
      AuxHi600: float
      AuxSec600: int
      AuxHi1200: float
      AuxSec1200: int
      // ⭐ MA-EXIT MARKS (user, 2026-07-29): first STRICT cross of vwap above the
      // strictly-prior {10,20,30,40,50,60}m mean after the fill bar, filled at the
      // NEXT present bar (aux discipline) — the counterfactual "exit at reversion
      // to the mean" sweep. ma_* = simple mean of bar vwaps; vwma_* = Σ(vwap·vol)/
      // Σvol. Both PARTIAL-TOLERANT (early-session window = session-so-far mean).
      // Unresolved marks fill at the MOC bar / day-end (sec >= MocSec = the moc
      // fallback, distinguishable post-hoc).
      Ma10Px: float
      Ma10Sec: int
      Ma20Px: float
      Ma20Sec: int
      Ma30Px: float
      Ma30Sec: int
      Ma40Px: float
      Ma40Sec: int
      Ma50Px: float
      Ma50Sec: int
      Ma60Px: float
      Ma60Sec: int
      Vwma10Px: float
      Vwma10Sec: int
      Vwma20Px: float
      Vwma20Sec: int
      Vwma30Px: float
      Vwma30Sec: int
      Vwma40Px: float
      Vwma40Sec: int
      Vwma50Px: float
      Vwma50Sec: int
      Vwma60Px: float
      Vwma60Sec: int
      // ----- exit -----
      BarsHeld: int              // present bars from the fill bar to the exit-fill bar
      State: IntraPosState }

/// ⭐ RIGHT-SIDE-OF-V CONTINUATION (user, 2026-07-29 — Lance Breitstein's concept).
/// Parent = a SPEC v1.2 reversal trip. After the parent's ENTRY FILL, the first
/// STRICT break above the prior {60,120,300}-bar MAX — the breach printing strictly
/// after the fill bar (aux-mark discipline) — is the continuation signal for that
/// window; it fills at the NEXT present bar's vwap. One continuation per
/// (parent, window). Each row carries THREE independent counterfactual trailing
/// stops: exit when vwap breaks STRICTLY below the prior {60,120,300}-bar rolling
/// MIN (ratchets up as the low rises; fill next bar), MOC backstop, NO profit
/// target — the right side runs. 3 entries x 3 stops = 9 counterfactuals/parent.
// (right-side-of-V ContPosition machinery DELETED — S39g: the continuation study
// closed at S26 (taker-fee-dead, left side wins) and the 9-counterfactual
// tracking was pure per-bar drag on the sampler.)

/// FlushFader config. Hard gates only — every other lever is a recorded column.
type IntradayConfig =
    { EntryChannelBars: int      // ⭐ ENTRY: vwap < the prior N-present-bar MIN of vwaps (strict).
                                 // Default 1200 (~20m on a fully-active name). The channel must be
                                 // WARM (N bars folded) — a partial-window "low" is not a flush.
                                 // Also the leg-reset channel: a new N-bar HIGH ends the down-leg.
      ExitChannelBars: int       // ⭐ EXIT: vwap > the prior N-bar MAX (strict) -> target. Default
                                 // 300 (~5m) — V6 F16's direction. NO other exit before MOC.
      DvFloor60: float           // hard gate: Sum60(vwap*volume) >= this at the signal bar. $ terms.
      TcFloor60: float           // hard gate: Sum60(tradeCount) >= this.
      // ⭐ VOLATILITY BAND — RECORD-FIRST for MR: the breakout F10 calibration does NOT
      // transfer (THE INVERSION: every momentum gate flipped in MR territory). Both
      // default OFF; volat_20m is a recorded column for post-hoc banding. A signal with
      // the volat feature still cold FAILS a positive floor, like V6's atrOk.
      MinVolat20m: float
      MaxVolat20m: float
      // ⭐ |eff_20m| floor — same record-first stance (V6's adx analog; keep 0). A signal
      // with eff still cold FAILS a positive floor.
      MinAbsEff20m: float
      // ⭐ SPEC v1.2 GATES (baked 2026-07-29) — the S18 production stack as entry
      // conditions, formulas IDENTICAL to the recorded columns. Each individually
      // disable-able; a cold feature FAILS an armed gate (volatOk stance).
      MaxSpeed1m: float          // vwap/vwap_60_prev - 1 < this (flush speed). Default -0.02. 0 = off.
      KBandLo: int               // lows_since_first_low >= this. Default 26. <= 0 = off.
      KBandHi: int               // lows_since_first_low <= this. Default 50. <= 0 = off.
      Eff20Lo: float             // eff_20m (SIGNED) >= this. Default -0.5. -Infinity = off.
      Eff20Hi: float             // eff_20m (SIGNED) <  this. Default -0.3. +Infinity = off.
                                 // ⚠ COLD eff_20m PASSES this band (user 2026-07-29).
      MinAbsEff10m: float        // |eff_10m| >= this. Default 0.15. 0 = off.
      DistHiLo: float            // vwap/chan_hi - 1 >= this (the un-fadeable wall). Default -0.35. -Infinity = off.
      DistHiHi: float            // vwap/chan_hi - 1 <  this (deep enough into the leg). Default -0.10. >= 0 = off.
      MinVol10Rate: float        // (vol_10/10)/(vol_60/60) >= this (S17/S18 last-10s floor). Default 0.75. 0 = off.
      MinLows300: int            // ⭐ SPEC v1.4 (S38h): lows_since_first_low_300 >= this — kills the
                                 // FAST-CHASE re-entry (5m bounce without a 20m leg reset leaves the
                                 // K-band satisfied, re-signaling in seconds; that slice = PF 0.11 on
                                 // the A++ cell). Default 6. 0 = off.
      MaxRngFront: float         // ⭐ SPEC v1.5 (S38k): rng_300/rng_20m < this — reject the PURE
                                 // CLIFF (the whole 20m range printed in the last 5m; monotone-worst
                                 // at mc=1, 1.51/64.7). Degenerate/cold ranges FAIL (mirrors SQL
                                 // nullif). Default 0.8. Infinity = off.
      MinAccel1020Bpm: float     // ⭐ SPEC v1.7 (S39o/r): (slope_10m − slope_20m)×6e5 >= this bp/min —
                                 // reject the LATE-ACCELERATING decline (the [−150,−80) bleed band:
                                 // 1.09/1.32/1.28 across three buckets; the RAW axis carries the
                                 // signal, vol-normalizing washes it out, S39q). Default −80.
                                 // −Infinity = off. Cold OLS FAILS an armed gate.
      MaxSlope20Bpm: float       // ⭐ SPEC v1.7 (user, S39m): slope_20m×6e5 < this bp/min — the
                                 // L-SHAPE INSURANCE (flat 20m slope + dist ≤ −10% ⇒ tail-compressed
                                 // late cliff; the 64-trip 0.438/43.8% sliver). Default −10.
                                 // >= 0 = off. Cold OLS FAILS an armed gate.
      MinSlope5Bpm: float        // ⭐ SPEC v1.7 (user, S39s): slope_5m×6e5 >= this bp/min — reject the
                                 // VERTICAL last-5m collapse (under the other v1.7 gates the < −400
                                 // slice = 66 trips @ 0.706/36.4%, four years with ZERO winners).
                                 // Default −400. −Infinity = off. Cold OLS FAILS an armed gate.
      MinDv0945Tape: float       // ⭐ tape-native dv_0945 floor (Σ vwap·vol, 1s bars < 09:45).
                                 // Default 0 = RECORD-FIRST; the live-scanner-consistent
                                 // replacement for the candidate-table dv_0945 gate.
      MaxDist1mHi: float         // ⭐ SPEC v1.9 (user, S40g): vwap/hi_60 - 1 < this — the 1m-leg
                                 // CONJUNCTION with the speed gate (fast last minute AND a real
                                 // leg below the 1m high; the shallow slice above −2% = 2,261
                                 // trips @ ~1.6, slot thieves — first both-mc-levels winner).
                                 // Default −0.02. >= 0 = off. Same post-push max60 as hi_60.
      // ⭐ PRICE-ACCEPTANCE STOPS (user, 2026-07-28): while holding, exit if a NEW
      // entry-channel low prints on (vol_60/60)/(vol_1200/1200) >= VolStopRatio, or
      // (tc_60/60)/(tc_1200/1200) >= TcStopRatio, or at vwap/vwap_60_prev - 1 <
      // SpeedStopPct. Ratios: +infinity = off. Speed: 0 = off (a 0% speed stop would
      // fire on every low). Both windows must be warm. Fill: next present bar.
      VolStopRatio: float
      TcStopRatio: float
      SpeedStopPct: float
      MaxConcurrent: int         // 0 = unlimited (THE SAMPLER DEFAULT — averages down). 1 = a real book.
      SlotBars: int              // the slot clock: 30 present bars (F5c: 30-40s flat, 30 stands).
      SessionStartSec: int       // 34200 = 09:30 — features fold from RTH open.
      EntryStartSec: int         // 36000 = 10:00 (V6's research window). ⚠ KNOWABILITY FLOOR 35100
                                 // (09:45) — the universe context (dv_0945 / rvol_0945_honest)
                                 // rides along in the output; entries before 09:45 would silently
                                 // make those columns lookahead (R4).
      EntryEndSec: int           // 48600 = 13:30.
      MocSec: int }              // 57600 = 16:00. Positions force-exit at the first bar >= this (its
                                 // own vwap — the auction-proximate print), and Flatten catches days
                                 // whose tape ends earlier (early closes).

/// The FlushFader engine. One instance per (ticker, day).
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly) =
    // ----- the entry/exit channels + the recorded channel set -----
    // MaxMa/MinMa pairs over vwap at six present-bar windows; session extremes
    // via RunMaxMa/RunMinMa. Entry/exit windows are validated in Program to
    // alias these.
    let max30 = MaxMa 30
    let max60 = MaxMa 60
    let max120 = MaxMa 120
    let max300 = MaxMa 300
    let max600 = MaxMa 600
    let max1200 = MaxMa 1200
    let min30 = MinMa 30
    let min60 = MinMa 60
    let min120 = MinMa 120
    let min300 = MinMa 300
    let min600 = MinMa 600
    let min1200 = MinMa 1200
    let sessHigh = RunMaxMa<float>()
    let sessLow = RunMinMa<float>()
    let chanMax n : MaxMa =
        match n with
        | 30 -> max30 | 60 -> max60 | 120 -> max120 | 300 -> max300 | 600 -> max600 | 1200 -> max1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    let chanMin n : MinMa =
        match n with
        | 30 -> min30 | 60 -> min60 | 120 -> min120 | 300 -> min300 | 600 -> min600 | 1200 -> min1200
        | _ -> invalidArg "n" $"no {n}-bar channel"
    let entryMin = chanMin cfg.EntryChannelBars     // ⭐ the flush trigger side
    let entryMax = chanMax cfg.EntryChannelBars     // ⭐ the leg-reset side (+ chan_hi)
    let exitMax = chanMax cfg.ExitChannelBars       // ⭐ the reversion target
    // ----- breach counters, both sides per window -----
    let brSess = BreachCounter()
    let br30 = BreachCounter()
    let br60 = BreachCounter()
    let br120 = BreachCounter()
    let br300 = BreachCounter()
    let br600 = BreachCounter()
    let br1200 = BreachCounter()
    let brLoSess = BreachCounter()
    let brLo30 = BreachCounter()
    let brLo60 = BreachCounter()
    let brLo120 = BreachCounter()
    let brLo300 = BreachCounter()
    let brLo600 = BreachCounter()
    let brLo1200 = BreachCounter()
    // ⭐ the leg machine + the per-leg trade counter (index of each SIGNAL
    // within the down-leg; reset together with the counters on the new
    // entry-channel high — NOT on new lows, unlike the momentum engines).
    let counters = NewLowCounters()
    // ⭐ S38e (user): the SAME new-low event counted under TIGHTER leg resets —
    // disarmed by a new 5m/10m high instead of the 20m one. A 20m-high breach
    // implies a 5m/10m breach (nested windows), so these never outlive `counters`.
    // RECORD-ONLY: no gate reads them.
    let counters300 = NewLowCounters()
    let counters600 = NewLowCounters()
    // ⭐ S38q OLS trend features (record-only): RollingMa's OlsSlopeMa on
    // ln(vwap) per present bar; signed r = sign(slope)·√R²
    let ols300 = OlsSlopeMa 300                  // ⭐ S39p (user): the 5m slope — the missing
                                                 // scale for DIRECT recent-acceleration
                                                 // (slope_5m − slope_10m), replacing the
                                                 // rngfront proxy for "late cliff"
    let ols600 = OlsSlopeMa 600
    let ols1200 = OlsSlopeMa 1200
    // ⭐ S39c rolling N_eff over 1s-bar volume, 600/1200 present bars (record-only):
    // one SlidingAgg per window carrying the (HHI, Shannon) accumulators as a
    // product monoid. SlidingAgg is size-agnostic — the counts live here and the
    // caller pops once the window is full (push-then-pop keeps it exactly at size).
    let neffCombine (struct (h1: NEffHhi, s1: NEffShannon)) (struct (h2: NEffHhi, s2: NEffShannon)) =
        struct (h1.Merge h2, s1.Merge s2)
    let neffZero = struct (NEffHhi.Zero, NEffShannon.Zero)
    let neffLift (v: float) = struct (NEffHhi.Zero.Add v, NEffShannon.Zero.Add v)
    let neff600 = SlidingAgg<struct (NEffHhi * NEffShannon)>(neffZero, neffCombine)
    let neff1200 = SlidingAgg<struct (NEffHhi * NEffShannon)>(neffZero, neffCombine)
    let mutable neff600Count = 0
    let mutable neff1200Count = 0
    // ⭐ S39i (user): N_eff (Shannon) of |30s-slot log returns| — trend SMOOTHNESS
    // on the SAME stream as the eff ratios (the |r| pushed into slotAbsSum/
    // slotAbsSum20): 40 slot-returns = 20m, 20 = 10m. A smooth slide spreads its
    // movement over many effective slots (high N_eff, near the window size); a
    // gap-and-chop tape concentrates it (low). Scale-invariant (entropy of
    // shares), direction-blind — the candidate replacement for the Kaufman eff
    // ratios. Shannon only (HHI = 0.965-correlated duplicate, S39f). Zero-|r|
    // slots occupy a window slot but add no mass (Add skips v <= 0), exactly
    // mirroring slotAbsSum's SumMa semantics.
    let neffRet40 = SlidingAgg<NEffShannon>(NEffShannon.Zero, fun a b -> a.Merge b)
    let neffRet20 = SlidingAgg<NEffShannon>(NEffShannon.Zero, fun a b -> a.Merge b)
    let mutable neffRet40Count = 0
    let mutable neffRet20Count = 0
    let mutable tradeIdx = 0
    // ----- activity sums + lags -----
    let volSum5 = SumMa 5                        // 5s/10s tails (user, 2026-07-29)
    let volSum10 = SumMa 10
    let volSum15 = SumMa 15
    let volSum30 = SumMa 30
    let volSum60 = SumMa 60
    let volSum600 = SumMa 600                    // 10m volume (user, 2026-07-28)
    let volSum1200 = SumMa 1200
    let tcSum5 = SumMa 5
    let tcSum10 = SumMa 10
    let tcSum15 = SumMa 15
    let tcSum30 = SumMa 30
    let tcSum60 = SumMa 60
    let tcSum600 = SumMa 600
    let tcSum1200 = SumMa 1200
    let vol60Lag = LagMa<float> 60               // the previous minute's 60-sum (slope features)
    let tc60Lag = LagMa<float> 60
    let vwap60Lag = LagMa<float> 60              // ⭐ the rolling 60-bar vwap, lagged one minute
    let dvSum60 = SumMa 60                       // Σ vwap·volume — the liquidity floor + vwap_60
    // ⭐ MA-exit machinery (user, 2026-07-29): rolling sums for the {10..60}m means.
    // Simple price MA = pxSumN/count; VWMA = dvSumN/volSumN. All partial-tolerant.
    let pxSum600 = SumMa 600
    let pxSum1200 = SumMa 1200
    let pxSum1800 = SumMa 1800
    let pxSum2400 = SumMa 2400
    let pxSum3000 = SumMa 3000
    let pxSum3600 = SumMa 3600
    let dvSum600 = SumMa 600
    // (no dvSum1200 here — the engine already maintains one below for vwap_1200;
    // a duplicate binding SHADOWED it and its double-push corrupted both. ⚠)
    let dvSum1800 = SumMa 1800
    let dvSum2400 = SumMa 2400
    let dvSum3000 = SumMa 3000
    let dvSum3600 = SumMa 3600
    let volSum1800 = SumMa 1800
    let volSum2400 = SumMa 2400
    let volSum3000 = SumMa 3000
    let volSum3600 = SumMa 3600
    let dvSum1200 = SumMa 1200                   // Σ vwap·volume over 20m — the 20m rolling VWAP
    // ----- the locked volatility block -----
    let slots = SlotVwapMa cfg.SlotBars
    let ew40 = EmaHlMa 40.0                      // volat_20m — THE driver (F7 lock)
    let ew20 = EmaHlMa 20.0                      // volat_10m — the trajectory twin
    // ⭐ S38n: the 40-INTERVAL horizon STAYS (a 39-interval alignment variant was
    // built and rejected — it churned ~11% of the book through the eff band and
    // lost 5 of 7 years). eff_20m therefore warms at 41 slots ≈ 1,230 present
    // bars, ONE SLOT after the 1200-bar entry channel; cold eff now FAILS (the
    // v1.2 cold-passes special case is retired — it only ever admitted the
    // 30-bar warm-up-gap fringe, 471 trips @ 1.78).
    let slotLag = LagMa<float> 40                // slot vwap 40 emissions ago (eff numerator)
    let slotAbsSum = SumMa 40                    // Σ|r| over the same 40 returns (eff denominator)
    let slotLag20 = LagMa<float> 20              // eff10m pair — same stream, half the horizon
    let slotAbsSum20 = SumMa 20
    // S40: slot-vwap extremes over the SAME 41/21-vwap spans the eff returns
    // cover — the range-eff numerators. Warmth aligns with the eff pair: the
    // 41st slot emission fills slotMax41 AND completes slotAbsSum's 40 returns.
    let slotMax41 = MaxMa 41
    let slotMin41 = MinMa 41
    let slotMax21 = MaxMa 21
    let slotMin21 = MinMa 21
    let mutable prevSlotVwap : float voption = ValueNone
    let mutable prevEtSec = -1                   // the PREVIOUS present bar's etSec (aux-mark lookback)
    let mutable prevVwap = nan                   // ... and its vwap (continuation signal price)
    // did the PREVIOUS bar close strictly above its prior {10..60}m mean? (the
    // MA-exit marks fill on the bar AFTER the cross — aux discipline)
    let mutable prevXMa10 = false
    let mutable prevXMa20 = false
    let mutable prevXMa30 = false
    let mutable prevXMa40 = false
    let mutable prevXMa50 = false
    let mutable prevXMa60 = false
    let mutable prevXVw10 = false
    let mutable prevXVw20 = false
    let mutable prevXVw30 = false
    let mutable prevXVw40 = false
    let mutable prevXVw50 = false
    let mutable prevXVw60 = false
    let mutable slotReturns = 0
    // ----- gaps / location / session -----
    let gap60 = GapCounter(60, cfg.SessionStartSec)
    let gap30 = GapCounter(30, cfg.SessionStartSec)
    let gap15 = GapCounter(15, cfg.SessionStartSec)
    let sessVwap = RatioMa()
    let mutable openVwap : float voption = ValueNone
    let mutable cumVol = 0.0
    let mutable dv0945Tape = 0.0                 // frozen once etSec >= 35100 (09:45)
    let mutable cumTc = 0.0

    // ⭐ ACTIVE/RETIRED SPLIT (user, 2026-07-23). At mc=0 a busy day opens hundreds
    // of trips per ticker; looping ALL of them every bar made runtime scale
    // super-linearly with trip count. A trip is INERT once it has exited AND its
    // last forward mark (+1200s) has filled — nothing in the per-bar loop can
    // touch it again — so it retires to `retired` and the hot loop only walks
    // `active`. `.Positions` = retired @ active (⚠ NOT chronological order —
    // sort by signal_sec in SQL if order matters).
    let active = ResizeArray<FlushPosition>()
    let retired = ResizeArray<FlushPosition>()
    let mutable pendingEntry : FlushPosition voption = ValueNone
    // ⭐ right-side-of-V continuations: watchers arm at the parent fill (one per
    // window, removed when fired), positions retire once all three stops resolve.
    // STRICTLY-PRIOR snapshots, captured BEFORE this bar's vwap folds in. ⚠ If
    // the current vwap were inside its own window, "vwap < channel min" would be
    // trivially false on every bar (a value can't undercut a min that contains
    // it) — and the exit target would trivially fire on every leading bar.
    let mutable sMax30 : float voption = ValueNone
    let mutable sMax60 : float voption = ValueNone
    let mutable sMax120 : float voption = ValueNone
    let mutable sMax300 : float voption = ValueNone
    let mutable sMax600 : float voption = ValueNone
    let mutable sMax1200 : float voption = ValueNone
    let mutable sMin30 : float voption = ValueNone
    let mutable sMin60 : float voption = ValueNone
    let mutable sMin120 : float voption = ValueNone
    let mutable sMin300 : float voption = ValueNone
    let mutable sMin600 : float voption = ValueNone
    let mutable sMin1200 : float voption = ValueNone
    let mutable sExitMax : float voption = ValueNone
    let mutable sSessHigh : float voption = ValueNone
    let mutable sSessLow : float voption = ValueNone

    let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
    /// ln(high/low) of a channel pair, nan until both sides carry a value.
    let chanRng (hi: MaxMa) (lo: MinMa) =
        match hi.State, lo.State with
        | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
        | _ -> nan

    member _.Ticker = ticker
    member _.Day = day
    member _.Positions = Seq.append retired active
    member _.OpenCount =
        let mutable k = 0
        for p in active do
            (match p.State with Holding | PendingExit _ -> k <- k + 1 | ExitedAt _ -> ())
        k
    member this.HasSlot =
        cfg.MaxConcurrent <= 0
        || this.OpenCount + (if pendingEntry.IsSome then 1 else 0) < cfg.MaxConcurrent

    /// Advance the whole system by one PRESENT 1s bar. Bars arrive in etSec
    /// order, RTH only (the emitter filters to [SessionStartSec, MocSec]).
    member this.Process (bar: SecBar) =
        if bar.etSec < cfg.SessionStartSec then () else

        // ===== 1. capture the STRICTLY-PRIOR channel states =====
        sMax30 <- max30.State
        sMax60 <- max60.State
        sMax120 <- max120.State
        sMax300 <- max300.State
        sMax600 <- max600.State
        sMax1200 <- max1200.State
        sMin30 <- min30.State
        sMin60 <- min60.State
        sMin120 <- min120.State
        sMin300 <- min300.State
        sMin600 <- min600.State
        sMin1200 <- min1200.State
        sExitMax <- exitMax.State
        sSessHigh <- sessHigh.State
        sSessLow <- sessLow.State
        // strictly-prior {10..60}m means for the MA-exit marks (partial-tolerant:
        // an early-session window degrades to the session-so-far mean)
        let inline sumMean (s: SumMa) = match s.State with ValueSome v when s.Count > 0 -> v / float s.Count | _ -> nan
        let inline sumRatio (a: SumMa) (b: SumMa) = match a.State, b.State with ValueSome x, ValueSome y when y > 0.0 -> x / y | _ -> nan
        let ma10 = sumMean pxSum600
        let ma20 = sumMean pxSum1200
        let ma30 = sumMean pxSum1800
        let ma40 = sumMean pxSum2400
        let ma50 = sumMean pxSum3000
        let ma60 = sumMean pxSum3600
        let vwma10 = sumRatio dvSum600 volSum600
        let vwma20 = sumRatio dvSum1200 volSum1200
        let vwma30 = sumRatio dvSum1800 volSum1800
        let vwma40 = sumRatio dvSum2400 volSum2400
        let vwma50 = sumRatio dvSum3000 volSum3000
        let vwma60 = sumRatio dvSum3600 volSum3600
        let priorEntryMin =
            match cfg.EntryChannelBars with
            | 30 -> sMin30 | 60 -> sMin60 | 120 -> sMin120 | 300 -> sMin300 | 600 -> sMin600 | 1200 -> sMin1200
            | _ -> ValueNone
        let priorEntryMax =
            match cfg.EntryChannelBars with
            | 30 -> sMax30 | 60 -> sMax60 | 120 -> sMax120 | 300 -> sMax300 | 600 -> sMax600 | 1200 -> sMax1200
            | _ -> ValueNone

        // ===== 2. fold this bar into every structure =====
        if openVwap.IsNone then openVwap <- ValueSome bar.vwap
        cumVol <- cumVol + bar.volume
        // tape-native 09:30-09:45 dollar volume (35100 = THE knowability floor, R4)
        if bar.etSec < 35100 then dv0945Tape <- dv0945Tape + bar.vwap * bar.volume
        cumTc <- cumTc + float bar.tradeCount
        gap60.Push bar.etSec
        gap30.Push bar.etSec
        gap15.Push bar.etSec
        volSum5.Push bar.volume
        volSum10.Push bar.volume
        volSum15.Push bar.volume
        volSum30.Push bar.volume
        volSum60.Push bar.volume
        volSum600.Push bar.volume
        volSum1200.Push bar.volume
        tcSum5.Push (float bar.tradeCount)
        tcSum10.Push (float bar.tradeCount)
        tcSum15.Push (float bar.tradeCount)
        tcSum30.Push (float bar.tradeCount)
        tcSum60.Push (float bar.tradeCount)
        tcSum600.Push (float bar.tradeCount)
        tcSum1200.Push (float bar.tradeCount)
        dvSum60.Push (bar.vwap * bar.volume)
        pxSum600.Push bar.vwap
        pxSum1200.Push bar.vwap
        pxSum1800.Push bar.vwap
        pxSum2400.Push bar.vwap
        pxSum3000.Push bar.vwap
        pxSum3600.Push bar.vwap
        dvSum600.Push (bar.vwap * bar.volume)
        dvSum1800.Push (bar.vwap * bar.volume)
        dvSum2400.Push (bar.vwap * bar.volume)
        dvSum3000.Push (bar.vwap * bar.volume)
        dvSum3600.Push (bar.vwap * bar.volume)
        volSum1800.Push bar.volume
        volSum2400.Push bar.volume
        volSum3000.Push bar.volume
        volSum3600.Push bar.volume
        dvSum1200.Push (bar.vwap * bar.volume)
        // the minute-lag chains feed only WARM 60-sums (1 push per bar after
        // warmup keeps .Lagged = the value ending exactly 60 bars ago)
        if volSum60.Count = 60 then
            (match volSum60.State with ValueSome s -> vol60Lag.Push s | ValueNone -> ())
        if tcSum60.Count = 60 then
            (match tcSum60.State with ValueSome s -> tc60Lag.Push s | ValueNone -> ())
        // ⭐ the rolling 60-bar vwap + its one-minute lag (user, 2026-07-28):
        // dv_60/vol_60, pushed into the lag once per warm bar so .Lagged = the
        // previous NON-OVERLAPPING minute's vwap — the flush-speed denominator.
        let vwap60Now =
            if volSum60.Count = volSum60.WindowSize then
                match dvSum60.State, volSum60.State with
                | ValueSome dv, ValueSome v when v > 0.0 -> ValueSome (dv / v)
                | _ -> ValueNone
            else ValueNone
        (match vwap60Now with ValueSome v -> vwap60Lag.Push v | ValueNone -> ())
        sessVwap.Push(bar.vwap * bar.volume, bar.volume)
        max30.Push bar.vwap
        max60.Push bar.vwap
        max120.Push bar.vwap
        max300.Push bar.vwap
        max600.Push bar.vwap
        max1200.Push bar.vwap
        min30.Push bar.vwap
        min60.Push bar.vwap
        min120.Push bar.vwap
        min300.Push bar.vwap
        min600.Push bar.vwap
        min1200.Push bar.vwap
        sessHigh.Push bar.vwap
        sessLow.Push bar.vwap
        ols300.Push (log bar.vwap)
        ols600.Push (log bar.vwap)
        ols1200.Push (log bar.vwap)
        neff600.Push (neffLift bar.volume)
        if neff600Count = 600 then neff600.Pop() else neff600Count <- neff600Count + 1
        neff1200.Push (neffLift bar.volume)
        if neff1200Count = 1200 then neff1200.Pop() else neff1200Count <- neff1200Count + 1
        // the slot chain: one |r| into the volat EWMAs per completed slot
        match slots.Push(bar.vwap, bar.volume) with
        | ValueSome v ->
            (match prevSlotVwap with
             | ValueSome pv when pv > 0.0 && v > 0.0 ->
                 let ar = abs (log (v / pv))
                 ew40.Push ar
                 ew20.Push ar
                 slotAbsSum.Push ar
                 slotAbsSum20.Push ar
                 // S39i: the same |r| into the smoothness windows
                 neffRet40.Push (NEffShannon.Zero.Add ar)
                 if neffRet40Count = 40 then neffRet40.Pop() else neffRet40Count <- neffRet40Count + 1
                 neffRet20.Push (NEffShannon.Zero.Add ar)
                 if neffRet20Count = 20 then neffRet20.Pop() else neffRet20Count <- neffRet20Count + 1
                 slotReturns <- slotReturns + 1
             | _ -> ())
            slotLag.Push v
            slotLag20.Push v
            slotMax41.Push v
            slotMin41.Push v
            slotMax21.Push v
            slotMin21.Push v
            prevSlotVwap <- ValueSome v
        | ValueNone -> ()

        // ===== 3. fill pendings at THIS bar's vwap (signals from the prior bar) =====
        match pendingEntry with
        | ValueSome p ->
            let filled = { p with EntrySec = bar.etSec; EntryPx = bar.vwap }
            active.Add filled
            pendingEntry <- ValueNone
        | ValueNone -> ()
        for i in 0 .. active.Count - 1 do
            match active.[i].State with
            | PendingExit reason ->
                active.[i] <- { active.[i] with State = ExitedAt (bar.etSec, bar.vwap, reason) }
            | _ -> ()

        // ===== 4. breach counters: step, then mark this bar's breaches =====
        // The aux-mark logic (step 5) reads the counters AS OF THE PREVIOUS BAR
        // ("previous snapshot's bars-since-high = 0 -> mark on the current
        // bar") — snapshot them before this bar's update.
        let prevBr120 = br120.BarsSinceBreach
        let prevBr300 = br300.BarsSinceBreach
        let prevBr600 = br600.BarsSinceBreach
        let prevBr1200 = br1200.BarsSinceBreach
        let breached (prior: float voption) = match prior with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        brSess.Step(); br30.Step(); br60.Step(); br120.Step(); br300.Step(); br600.Step(); br1200.Step()
        if breached sSessHigh then brSess.OnBreach()
        if breached sMax30 then br30.OnBreach()
        if breached sMax60 then br60.OnBreach()
        if breached sMax120 then br120.OnBreach()
        if breached sMax300 then br300.OnBreach()
        if breached sMax600 then br600.OnBreach()
        if breached sMax1200 then br1200.OnBreach()
        let breachedLo (prior: float voption) = match prior with ValueSome lo -> bar.vwap < lo | ValueNone -> false
        brLoSess.Step(); brLo30.Step(); brLo60.Step(); brLo120.Step(); brLo300.Step(); brLo600.Step(); brLo1200.Step()
        if breachedLo sSessLow then brLoSess.OnBreach()
        if breachedLo sMin30 then brLo30.OnBreach()
        if breachedLo sMin60 then brLo60.OnBreach()
        if breachedLo sMin120 then brLo120.OnBreach()
        if breachedLo sMin300 then brLo300.OnBreach()
        if breachedLo sMin600 then brLo600.OnBreach()
        if breachedLo sMin1200 then brLo1200.OnBreach()
        // ⭐ the leg machine. Step FIRST so BarsSinceFirstLow counts bars
        // ELAPSED since the leg's first low. STRICT inequalities on both
        // events (V6 F21: `<=` re-fired on round-number pinning ties — two
        // consecutive identical prints are NOT a new low; same rule for the
        // reset high). The RESET itself fires LAST (step 7) — an entry on
        // this bar must read the pre-reset counters.
        counters.Step()
        counters300.Step()
        counters600.Step()
        let isNewLow = match priorEntryMin with ValueSome lo -> bar.vwap < lo | ValueNone -> false
        let isNewHigh = match priorEntryMax with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        if isNewLow then
            counters.OnNewLow()
            counters300.OnNewLow()
            counters600.OnNewLow()

        // ===== 5. advance open positions: forward marks, hold clock, exit signals =====
        // Exit precedence: moc > acceptance stops > target. Stops and target are
        // mutually exclusive on one bar (a new entry-channel LOW cannot also be a
        // new exit-channel HIGH: prior max >= prior min).
        let targetHit = match sExitMax with ValueSome hi -> bar.vwap > hi | ValueNone -> false
        // ⭐ the price-acceptance stops: qualified FRESH lows only (see config)
        let rate60vs1200 (s60: SumMa) (s1200: SumMa) =
            if s60.Count = s60.WindowSize && s1200.Count = s1200.WindowSize then
                match s60.State, s1200.State with
                | ValueSome a, ValueSome b when b > 0.0 -> ValueSome ((a / 60.0) / (b / 1200.0))
                | _ -> ValueNone
            else ValueNone
        let volStopHit =
            isNewLow
            && not (Double.IsPositiveInfinity cfg.VolStopRatio)
            && (match rate60vs1200 volSum60 volSum1200 with
                | ValueSome r -> r >= cfg.VolStopRatio
                | ValueNone -> false)
        let tcStopHit =
            isNewLow
            && not (Double.IsPositiveInfinity cfg.TcStopRatio)
            && (match rate60vs1200 tcSum60 tcSum1200 with
                | ValueSome r -> r >= cfg.TcStopRatio
                | ValueNone -> false)
        let speedStopHit =
            isNewLow
            && cfg.SpeedStopPct < 0.0
            && (match vwap60Lag.Lagged with
                | ValueSome pv when pv > 0.0 -> bar.vwap / pv - 1.0 < cfg.SpeedStopPct
                | _ -> false)
        // compacting walk: survivors overwrite in place, inert trips retire
        let mutable w = 0
        for i in 0 .. active.Count - 1 do
            let p = active.[i]
            // forward marks fill for EVERY trip (exited included — the sampler
            // wants the counterfactual path), first present bar past each horizon
            let p =
                { p with
                    FwdVwap60 = if Double.IsNaN p.FwdVwap60 && bar.etSec >= p.EntrySec + 60 then bar.vwap else p.FwdVwap60
                    FwdVwap300 = if Double.IsNaN p.FwdVwap300 && bar.etSec >= p.EntrySec + 300 then bar.vwap else p.FwdVwap300
                    FwdVwap600 = if Double.IsNaN p.FwdVwap600 && bar.etSec >= p.EntrySec + 600 then bar.vwap else p.FwdVwap600
                    FwdVwap1200 = if Double.IsNaN p.FwdVwap1200 && bar.etSec >= p.EntrySec + 1200 then bar.vwap else p.FwdVwap1200 }
            // aux-high marks: the PREVIOUS bar's breach-counter snapshot reads
            // 0 -> the previous bar printed the new N-bar high -> the mark
            // fills at THIS bar's vwap. Only highs printed STRICTLY AFTER the
            // entry fill bar count (prevEtSec > EntrySec), and only the FIRST
            // one per window per trip (mark still nan).
            let inline auxStep px sec prevBr =
                if Double.IsNaN px && prevBr = 0 && prevEtSec > p.EntrySec
                then struct (bar.vwap, bar.etSec)
                else struct (px, sec)
            let struct (hi120, sc120) = auxStep p.AuxHi120 p.AuxSec120 prevBr120
            let struct (hi300, sc300) = auxStep p.AuxHi300 p.AuxSec300 prevBr300
            let struct (hi600, sc600) = auxStep p.AuxHi600 p.AuxSec600 prevBr600
            let struct (hi1200, sc1200) = auxStep p.AuxHi1200 p.AuxSec1200 prevBr1200
            let p =
                { p with
                    AuxHi120 = hi120; AuxSec120 = sc120
                    AuxHi300 = hi300; AuxSec300 = sc300
                    AuxHi600 = hi600; AuxSec600 = sc600
                    AuxHi1200 = hi1200; AuxSec1200 = sc1200 }
            // MA-exit marks: the PREVIOUS bar crossed strictly above its prior
            // mean (strictly after the fill bar) -> fill at THIS bar's vwap; any
            // mark still unresolved at/past MocSec resolves at this bar (the moc
            // fallback — sec >= MocSec distinguishes it post-hoc).
            let inline maStep px sec prevX =
                if Double.IsNaN px && (bar.etSec >= cfg.MocSec || (prevX && prevEtSec > p.EntrySec))
                then struct (bar.vwap, bar.etSec)
                else struct (px, sec)
            let struct (m10p, m10s) = maStep p.Ma10Px p.Ma10Sec prevXMa10
            let struct (m20p, m20s) = maStep p.Ma20Px p.Ma20Sec prevXMa20
            let struct (m30p, m30s) = maStep p.Ma30Px p.Ma30Sec prevXMa30
            let struct (m40p, m40s) = maStep p.Ma40Px p.Ma40Sec prevXMa40
            let struct (m50p, m50s) = maStep p.Ma50Px p.Ma50Sec prevXMa50
            let struct (m60p, m60s) = maStep p.Ma60Px p.Ma60Sec prevXMa60
            let struct (v10p, v10s) = maStep p.Vwma10Px p.Vwma10Sec prevXVw10
            let struct (v20p, v20s) = maStep p.Vwma20Px p.Vwma20Sec prevXVw20
            let struct (v30p, v30s) = maStep p.Vwma30Px p.Vwma30Sec prevXVw30
            let struct (v40p, v40s) = maStep p.Vwma40Px p.Vwma40Sec prevXVw40
            let struct (v50p, v50s) = maStep p.Vwma50Px p.Vwma50Sec prevXVw50
            let struct (v60p, v60s) = maStep p.Vwma60Px p.Vwma60Sec prevXVw60
            let p =
                { p with
                    Ma10Px = m10p; Ma10Sec = m10s
                    Ma20Px = m20p; Ma20Sec = m20s
                    Ma30Px = m30p; Ma30Sec = m30s
                    Ma40Px = m40p; Ma40Sec = m40s
                    Ma50Px = m50p; Ma50Sec = m50s
                    Ma60Px = m60p; Ma60Sec = m60s
                    Vwma10Px = v10p; Vwma10Sec = v10s
                    Vwma20Px = v20p; Vwma20Sec = v20s
                    Vwma30Px = v30p; Vwma30Sec = v30s
                    Vwma40Px = v40p; Vwma40Sec = v40s
                    Vwma50Px = v50p; Vwma50Sec = v50s
                    Vwma60Px = v60p; Vwma60Sec = v60s }
            let p =
                match p.State with
                | Holding | PendingExit _ -> { p with BarsHeld = p.BarsHeld + 1 }
                | ExitedAt _ -> p
            let p =
                match p.State with
                | Holding ->
                    if bar.etSec >= cfg.MocSec then
                        // the 16:00 bar IS the auction-proximate print — fill here, not next bar
                        { p with State = ExitedAt (bar.etSec, bar.vwap, "moc") }
                    elif volStopHit then { p with State = PendingExit "vol_stop" }
                    elif tcStopHit then { p with State = PendingExit "tc_stop" }
                    elif speedStopHit then { p with State = PendingExit "speed_stop" }
                    elif targetHit then { p with State = PendingExit "target" }
                    else p
                | _ -> p
            // retire when exited AND the last (+1200s) mark has filled — a bar
            // that fills the 1200s mark also fills the 60/300/600 ones — AND no
            // aux mark is about to fill off THIS bar's high (an unset mark whose
            // counter just hit 0 fills next bar; retiring now would lose it)
            match p.State with
            | ExitedAt _ when not (Double.IsNaN p.FwdVwap1200)
                              && not (Double.IsNaN p.AuxHi120 && br120.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxHi300 && br300.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxHi600 && br600.BarsSinceBreach = 0)
                              && not (Double.IsNaN p.AuxHi1200 && br1200.BarsSinceBreach = 0)
                              // MA-exit marks: retire only fully resolved (they
                              // resolve by the MOC bar at the latest)
                              && not (Double.IsNaN p.Ma10Px) && not (Double.IsNaN p.Ma20Px)
                              && not (Double.IsNaN p.Ma30Px) && not (Double.IsNaN p.Ma40Px)
                              && not (Double.IsNaN p.Ma50Px) && not (Double.IsNaN p.Ma60Px)
                              && not (Double.IsNaN p.Vwma10Px) && not (Double.IsNaN p.Vwma20Px)
                              && not (Double.IsNaN p.Vwma30Px) && not (Double.IsNaN p.Vwma40Px)
                              && not (Double.IsNaN p.Vwma50Px) && not (Double.IsNaN p.Vwma60Px) ->
                retired.Add p
            | _ ->
                active.[w] <- p
                w <- w + 1
        if w < active.Count then active.RemoveRange(w, active.Count - w)

        // ===== 6. entry signal (fills next bar) =====
        let inWindow = bar.etSec >= cfg.EntryStartSec && bar.etSec <= cfg.EntryEndSec
        let channelWarm = entryMin.Count = entryMin.WindowSize
        let floorsOk =
            (match dvSum60.State with ValueSome dv -> dv >= cfg.DvFloor60 | ValueNone -> false)
            && (match tcSum60.State with ValueSome tc -> tc >= cfg.TcFloor60 | ValueNone -> false)
        // the volatility band: floor AND ceiling (record-first — see the config comment)
        let volatOk =
            (cfg.MinVolat20m <= 0.0
             || (match ew40.State with ValueSome v -> v >= cfg.MinVolat20m | ValueNone -> false))
            && (Double.IsPositiveInfinity cfg.MaxVolat20m
                || (match ew40.State with ValueSome v -> v < cfg.MaxVolat20m | ValueNone -> true))
        let effOk =
            cfg.MinAbsEff20m <= 0.0
            || (match slotLag.Last, slotLag.Lagged, slotAbsSum.State with
                | ValueSome cur, ValueSome old, ValueSome s
                    when slotAbsSum.Count = slotAbsSum.WindowSize && old > 0.0 && s > 0.0 ->
                    abs (log (cur / old) / s) >= cfg.MinAbsEff20m
                | _ -> false)
        // ⭐ SPEC v1.2 GATES — expressions mirror the recorded columns exactly, so an
        // engine-gated run must bit-match the post-hoc SQL on the same trips (S19).
        let speedOk =
            cfg.MaxSpeed1m >= 0.0
            || (match vwap60Lag.Lagged with
                | ValueSome pv when pv > 0.0 -> bar.vwap / pv - 1.0 < cfg.MaxSpeed1m
                | _ -> false)
        // ⭐ SPEC v1.9 (S40g): post-push max60, identical to the recorded hi_60
        // (no fullness requirement — the 1200-bar signal channel guarantees warmth).
        let d1mOk =
            cfg.MaxDist1mHi >= 0.0
            || (match max60.State with
                | ValueSome h when h > 0.0 -> bar.vwap / h - 1.0 < cfg.MaxDist1mHi
                | _ -> false)
        let kBandOk =
            (cfg.KBandLo <= 0 || counters.LowsSinceFirstLow >= cfg.KBandLo)
            && (cfg.KBandHi <= 0 || counters.LowsSinceFirstLow <= cfg.KBandHi)
        let eff20Signed =
            match slotLag.Last, slotLag.Lagged, slotAbsSum.State with
            | ValueSome cur, ValueSome old, ValueSome s
                when slotAbsSum.Count = slotAbsSum.WindowSize && old > 0.0 && s > 0.0 ->
                ValueSome (log (cur / old) / s)
            | _ -> ValueNone
        let eff20BandOk =
            // ⭐ S38n (SPEC v1.6): COLD eff_20m FAILS — standard stance restored.
            // Cold-at-signal = the one-slot warm-up gap (channel warm at 1,200 bars,
            // eff at ~1,230) — a weak early-morning thin-tape fringe (471 trips @
            // 1.78, hurting 2023) — or a degenerate dead tape (Σ|r| = 0). Post-hoc
            // v1.6 parity vs older parquets: add `eff_20m IS NOT NULL`.
            match eff20Signed with
            | ValueNone -> false
            | ValueSome e ->
                (Double.IsNegativeInfinity cfg.Eff20Lo || e >= cfg.Eff20Lo)
                && (Double.IsPositiveInfinity cfg.Eff20Hi || e < cfg.Eff20Hi)
        let eff10Ok =
            cfg.MinAbsEff10m <= 0.0
            || (match slotLag20.Last, slotLag20.Lagged, slotAbsSum20.State with
                | ValueSome cur, ValueSome old, ValueSome s
                    when slotAbsSum20.Count = slotAbsSum20.WindowSize && old > 0.0 && s > 0.0 ->
                    abs (log (cur / old) / s) >= cfg.MinAbsEff10m
                | _ -> false)
        let distHi =
            match priorEntryMax with
            | ValueSome hi when hi > 0.0 -> ValueSome (bar.vwap / hi - 1.0)
            | _ -> ValueNone
        let distOk =
            (Double.IsNegativeInfinity cfg.DistHiLo
             || (match distHi with ValueSome d -> d >= cfg.DistHiLo | ValueNone -> false))
            && (cfg.DistHiHi >= 0.0
                || (match distHi with ValueSome d -> d < cfg.DistHiHi | ValueNone -> false))
        let vol10Ok =
            cfg.MinVol10Rate <= 0.0
            || (match volSum10.State, volSum60.State with
                | ValueSome v10, ValueSome v60 when v60 > 0.0 ->
                    (v10 / 10.0) / (v60 / 60.0) >= cfg.MinVol10Rate
                | _ -> false)
        let dv0945TapeOk = cfg.MinDv0945Tape <= 0.0 || dv0945Tape >= cfg.MinDv0945Tape
        let lows300Ok = cfg.MinLows300 <= 0 || counters300.LowsSinceFirstLow >= cfg.MinLows300
        let frontOk =
            Double.IsPositiveInfinity cfg.MaxRngFront
            || chanRng max300 min300 / chanRng max1200 min1200 < cfg.MaxRngFront
        // ⭐ SPEC v1.7 (S39o/r): OLS gates. ols1200 warms exactly with the entry
        // channel (both need 1200 present bars) so at any signal both slopes are
        // warm; the ValueNone arms are pure defense.
        let accelOk =
            Double.IsNegativeInfinity cfg.MinAccel1020Bpm
            || (match ols600.State, ols1200.State with
                | ValueSome m6, ValueSome m12
                    when ols600.Count = ols600.WindowSize && ols1200.Count = ols1200.WindowSize ->
                    (m6 - m12) * 6e5 >= cfg.MinAccel1020Bpm
                | _ -> false)
        let slope20Ok =
            cfg.MaxSlope20Bpm >= 0.0
            || (match ols1200.State with
                | ValueSome m when ols1200.Count = ols1200.WindowSize -> m * 6e5 < cfg.MaxSlope20Bpm
                | _ -> false)
        let slope5Ok =
            Double.IsNegativeInfinity cfg.MinSlope5Bpm
            || (match ols300.State with
                | ValueSome m when ols300.Count = ols300.WindowSize -> m * 6e5 >= cfg.MinSlope5Bpm
                | _ -> false)
        let specOk = speedOk && d1mOk && kBandOk && eff20BandOk && eff10Ok && distOk && vol10Ok && dv0945TapeOk && lows300Ok && frontOk && accelOk && slope20Ok && slope5Ok
        if inWindow && channelWarm && isNewLow && floorsOk && volatOk && effOk && specOk && this.HasSlot then
            pendingEntry <-
                ValueSome
                    { SignalSec = bar.etSec
                      SignalVwap = bar.vwap
                      EntrySec = -1                  // filled next bar (step 3)
                      EntryPx = nan
                      Volat20m = vv ew40.State
                      Volat10m = vv ew20.State
                      Rng20m = chanRng max1200 min1200
                      Eff20m =
                        (match slotLag.Last, slotLag.Lagged, slotAbsSum.State with
                         | ValueSome cur, ValueSome old, ValueSome s
                             when slotAbsSum.Count = slotAbsSum.WindowSize && old > 0.0 && s > 0.0 ->
                             log (cur / old) / s
                         | _ -> nan)
                      Eff10m =
                        (match slotLag20.Last, slotLag20.Lagged, slotAbsSum20.State with
                         | ValueSome cur, ValueSome old, ValueSome s
                             when slotAbsSum20.Count = slotAbsSum20.WindowSize && old > 0.0 && s > 0.0 ->
                             log (cur / old) / s
                         | _ -> nan)
                      SlotCount = slotReturns
                      RngSlots20m =
                        (match slotMax41.State, slotMin41.State with
                         | ValueSome h, ValueSome l
                             when slotMax41.Count = slotMax41.WindowSize && l > 0.0 -> log (h / l)
                         | _ -> nan)
                      RngSlots10m =
                        (match slotMax21.State, slotMin21.State with
                         | ValueSome h, ValueSome l
                             when slotMax21.Count = slotMax21.WindowSize && l > 0.0 -> log (h / l)
                         | _ -> nan)
                      EffRng20m =
                        (match slotMax41.State, slotMin41.State, slotAbsSum.State with
                         | ValueSome h, ValueSome l, ValueSome s
                             when slotMax41.Count = slotMax41.WindowSize
                                  && slotAbsSum.Count = slotAbsSum.WindowSize
                                  && l > 0.0 && s > 0.0 ->
                             log (h / l) / s
                         | _ -> nan)
                      EffRng10m =
                        (match slotMax21.State, slotMin21.State, slotAbsSum20.State with
                         | ValueSome h, ValueSome l, ValueSome s
                             when slotMax21.Count = slotMax21.WindowSize
                                  && slotAbsSum20.Count = slotAbsSum20.WindowSize
                                  && l > 0.0 && s > 0.0 ->
                             log (h / l) / s
                         | _ -> nan)
                      RngSess =
                        (match sessHigh.State, sessLow.State with
                         | ValueSome h, ValueSome l when l > 0.0 -> log (h / l)
                         | _ -> nan)
                      Rng600 = chanRng max600 min600
                      Rng300 = chanRng max300 min300
                      Rng120 = chanRng max120 min120
                      Rng60 = chanRng max60 min60
                      Rng30 = chanRng max30 min30
                      Hi60 = vv max60.State
                      BreachSess = brSess.BarsSinceBreach
                      Breach1200 = br1200.BarsSinceBreach
                      Breach600 = br600.BarsSinceBreach
                      Breach300 = br300.BarsSinceBreach
                      Breach120 = br120.BarsSinceBreach
                      Breach60 = br60.BarsSinceBreach
                      Breach30 = br30.BarsSinceBreach
                      BreachLoSess = brLoSess.BarsSinceBreach
                      BreachLo1200 = brLo1200.BarsSinceBreach
                      BreachLo600 = brLo600.BarsSinceBreach
                      BreachLo300 = brLo300.BarsSinceBreach
                      BreachLo120 = brLo120.BarsSinceBreach
                      BreachLo60 = brLo60.BarsSinceBreach
                      BreachLo30 = brLo30.BarsSinceBreach
                      // ⭐ read the counters NOW — the reset in step 7 must not
                      // affect this trip. The entry bar IS a low, so both >= 0.
                      BarsSinceFirstLow = counters.BarsSinceFirstLow
                      LowsSinceFirstLow = counters.LowsSinceFirstLow
                      BarsSinceFirstLow300 = counters300.BarsSinceFirstLow
                      LowsSinceFirstLow300 = counters300.LowsSinceFirstLow
                      BarsSinceFirstLow600 = counters600.BarsSinceFirstLow
                      LowsSinceFirstLow600 = counters600.LowsSinceFirstLow
                      TradeIdx = tradeIdx
                      OpenAtSignal = this.OpenCount
                      Vwap1200 =
                        (if volSum1200.Count = volSum1200.WindowSize then
                            match dvSum1200.State, volSum1200.State with
                            | ValueSome dv, ValueSome v when v > 0.0 -> dv / v
                            | _ -> nan
                         else nan)
                      ChanHi = vv priorEntryMax
                      ChanLo = vv priorEntryMin
                      ExitChanHi = vv sExitMax
                      Gap60 = gap60.Gaps
                      Gap30 = gap30.Gaps
                      Gap15 = gap15.Gaps
                      SessVwap = vv sessVwap.State
                      DistSessVwap =
                        (match sessVwap.State with
                         | ValueSome sv when sv > 0.0 -> log (bar.vwap / sv)
                         | _ -> nan)
                      PctChgOpen =
                        (match openVwap with
                         | ValueSome o when o > 0.0 -> bar.vwap / o - 1.0
                         | _ -> nan)
                      BarVol = bar.volume
                      BarTc = bar.tradeCount
                      Vol5 = vv volSum5.State
                      Vol10 = vv volSum10.State
                      Vol15 = vv volSum15.State
                      Vol30 = vv volSum30.State
                      Vol60 = vv volSum60.State
                      Vol600 = vv volSum600.State
                      Vol1200 = vv volSum1200.State
                      Tc5 = vv tcSum5.State
                      Tc10 = vv tcSum10.State
                      Tc15 = vv tcSum15.State
                      Tc30 = vv tcSum30.State
                      Tc60 = vv tcSum60.State
                      Tc600 = vv tcSum600.State
                      Tc1200 = vv tcSum1200.State
                      Vol60Prev = vv vol60Lag.Lagged
                      Tc60Prev = vv tc60Lag.Lagged
                      Vwap60 = vv vwap60Now
                      Vwap60Prev = vv vwap60Lag.Lagged
                      DollarVol60 = vv dvSum60.State
                      CumVol = cumVol
                      Dv0945Tape = dv0945Tape
                      OlsSlope300 =
                        (match ols300.State with
                         | ValueSome m when ols300.Count = ols300.WindowSize -> m
                         | _ -> nan)
                      OlsR300 =
                        (match ols300.State, ols300.R2 with
                         | ValueSome m, ValueSome r2 when ols300.Count = ols300.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      OlsSlope600 =
                        (match ols600.State with
                         | ValueSome m when ols600.Count = ols600.WindowSize -> m
                         | _ -> nan)
                      OlsR600 =
                        (match ols600.State, ols600.R2 with
                         | ValueSome m, ValueSome r2 when ols600.Count = ols600.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      OlsSlope1200 =
                        (match ols1200.State with
                         | ValueSome m when ols1200.Count = ols1200.WindowSize -> m
                         | _ -> nan)
                      OlsR1200 =
                        (match ols1200.State, ols1200.R2 with
                         | ValueSome m, ValueSome r2 when ols1200.Count = ols1200.WindowSize ->
                             (if m < 0.0 then -sqrt r2 else sqrt r2)
                         | _ -> nan)
                      NEffShannon600 =
                        (if neff600Count = 600 then let struct (_, s) = neff600.Query in s.Value else nan)
                      NEffHhi600 =
                        (if neff600Count = 600 then let struct (h, _) = neff600.Query in h.Value else nan)
                      NEffShannon1200 =
                        (if neff1200Count = 1200 then let struct (_, s) = neff1200.Query in s.Value else nan)
                      NEffHhi1200 =
                        (if neff1200Count = 1200 then let struct (h, _) = neff1200.Query in h.Value else nan)
                      NEffRet20m = (if neffRet40Count = 40 then neffRet40.Query.Value else nan)
                      NEffRet10m = (if neffRet20Count = 20 then neffRet20.Query.Value else nan)
                      CumTc = cumTc
                      FwdVwap60 = nan
                      FwdVwap300 = nan
                      FwdVwap600 = nan
                      FwdVwap1200 = nan
                      AuxHi120 = nan
                      AuxSec120 = -1
                      AuxHi300 = nan
                      AuxSec300 = -1
                      AuxHi600 = nan
                      AuxSec600 = -1
                      AuxHi1200 = nan
                      AuxSec1200 = -1
                      Ma10Px = nan
                      Ma10Sec = -1
                      Ma20Px = nan
                      Ma20Sec = -1
                      Ma30Px = nan
                      Ma30Sec = -1
                      Ma40Px = nan
                      Ma40Sec = -1
                      Ma50Px = nan
                      Ma50Sec = -1
                      Ma60Px = nan
                      Ma60Sec = -1
                      Vwma10Px = nan
                      Vwma10Sec = -1
                      Vwma20Px = nan
                      Vwma20Sec = -1
                      Vwma30Px = nan
                      Vwma30Sec = -1
                      Vwma40Px = nan
                      Vwma40Sec = -1
                      Vwma50Px = nan
                      Vwma50Sec = -1
                      Vwma60Px = nan
                      Vwma60Sec = -1
                      BarsHeld = 0
                      State = Holding }
            // ⭐ the trade counter advances on INITIATION (the signal), whether or
            // not the fill materializes — the (rare) end-of-tape dropped pending
            // entry still consumed its place in the leg's sequence.
            tradeIdx <- tradeIdx + 1

        // ===== 7. the leg reset fires LAST: a new entry-channel high ends the
        // down-leg. AFTER the entry block — an entry on this bar reads the
        // pre-reset counters (V6's ordering). isNewHigh and isNewLow are
        // mutually exclusive (prior max >= prior min), so the reset can never
        // clobber the very leg an entry just joined. =====
        if isNewHigh then
            counters.Reset()
            tradeIdx <- 0
        // ⭐ S38e tighter resets: the breach bar reads 0 (OnBreach fired in
        // step 4). A new 5m/10m high can't share a bar with an entry either
        // (vwap < prior min1200 <= prior max300/600), so ordering is safe; a
        // 20m-high bar implies both, keeping all three counters in sync.
        if br300.BarsSinceBreach = 0 then counters300.Reset()
        if br600.BarsSinceBreach = 0 then counters600.Reset()
        // the aux-mark lookback: remember this bar as "the previous bar"
        prevEtSec <- bar.etSec
        prevVwap <- bar.vwap
        prevXMa10 <- not (Double.IsNaN ma10) && bar.vwap > ma10
        prevXMa20 <- not (Double.IsNaN ma20) && bar.vwap > ma20
        prevXMa30 <- not (Double.IsNaN ma30) && bar.vwap > ma30
        prevXMa40 <- not (Double.IsNaN ma40) && bar.vwap > ma40
        prevXMa50 <- not (Double.IsNaN ma50) && bar.vwap > ma50
        prevXMa60 <- not (Double.IsNaN ma60) && bar.vwap > ma60
        prevXVw10 <- not (Double.IsNaN vwma10) && bar.vwap > vwma10
        prevXVw20 <- not (Double.IsNaN vwma20) && bar.vwap > vwma20
        prevXVw30 <- not (Double.IsNaN vwma30) && bar.vwap > vwma30
        prevXVw40 <- not (Double.IsNaN vwma40) && bar.vwap > vwma40
        prevXVw50 <- not (Double.IsNaN vwma50) && bar.vwap > vwma50
        prevXVw60 <- not (Double.IsNaN vwma60) && bar.vwap > vwma60

    /// Flatten at the tape's last bar: fill any pending exit and force-exit any
    /// holder at the last vwap ("moc" — covers early closes and thin tapes whose
    /// last print lands before MocSec). A pending ENTRY that never filled is
    /// dropped — there was no bar to trade into.
    member _.Flatten (lastBar: SecBar) =
        pendingEntry <- ValueNone
        for i in 0 .. active.Count - 1 do
            let p = active.[i]
            let p =
                match p.State with
                | Holding | PendingExit _ -> { p with State = ExitedAt (lastBar.etSec, lastBar.vwap, "moc") }
                | ExitedAt _ -> p
            // MA-exit marks: resolve stragglers at the day's last bar (early
            // closes / thin tapes whose final print lands before MocSec)
            let inline fin px = if Double.IsNaN px then lastBar.vwap else px
            let inline finSec px sec = if Double.IsNaN px then lastBar.etSec else sec
            active.[i] <-
                { p with
                    Ma10Sec = finSec p.Ma10Px p.Ma10Sec; Ma10Px = fin p.Ma10Px
                    Ma20Sec = finSec p.Ma20Px p.Ma20Sec; Ma20Px = fin p.Ma20Px
                    Ma30Sec = finSec p.Ma30Px p.Ma30Sec; Ma30Px = fin p.Ma30Px
                    Ma40Sec = finSec p.Ma40Px p.Ma40Sec; Ma40Px = fin p.Ma40Px
                    Ma50Sec = finSec p.Ma50Px p.Ma50Sec; Ma50Px = fin p.Ma50Px
                    Ma60Sec = finSec p.Ma60Px p.Ma60Sec; Ma60Px = fin p.Ma60Px
                    Vwma10Sec = finSec p.Vwma10Px p.Vwma10Sec; Vwma10Px = fin p.Vwma10Px
                    Vwma20Sec = finSec p.Vwma20Px p.Vwma20Sec; Vwma20Px = fin p.Vwma20Px
                    Vwma30Sec = finSec p.Vwma30Px p.Vwma30Sec; Vwma30Px = fin p.Vwma30Px
                    Vwma40Sec = finSec p.Vwma40Px p.Vwma40Sec; Vwma40Px = fin p.Vwma40Px
                    Vwma50Sec = finSec p.Vwma50Px p.Vwma50Sec; Vwma50Px = fin p.Vwma50Px
                    Vwma60Sec = finSec p.Vwma60Px p.Vwma60Sec; Vwma60Px = fin p.Vwma60Px }
