module TradingEdge.MaxRiderV1.Intraday

open System
open TradingEdge.RollingMa

// ===========================================================================
// MaxRiderV1 — INTRADAY MEAN REVERSION, THE SHORT SIDE. A mirror of DipRiderV6.
//
// WHY (user, 2026-07-17): "We're going to do more research on mean reversion, but
// this time from the short side. That will be one way to get more trades."
//
// This is DipRiderV6 with every direction flipped. The naming follows the existing
// convention: LowFlyer (long) / MaxFlyer (short) => DipRider (long) / MaxRider (short).
//
// THE ENTIRE SYSTEM — the exact MIRROR of V6:
//   ENTRY: SHORT when close >= the STRICTLY-PRIOR N-minute MAX of 1m-bar CLOSES
//          (fade the pop to a new N-minute HIGH)
//   EXIT:  COVER when close <= the STRICTLY-PRIOR M-minute MIN of closes ("target"),
//          or MOC.
//
// ⚠ WHY THE SHORT SIDE IS NOT A FREE MIRROR — read before trusting any number here:
//   * BORROW: a short needs locate + borrow cost. The thin/high-ATR names where V6
//     found its best cells (F14: the PF 3.291 cell is $1.13 stocks; F17: the 0.06-0.08
//     ATR tail) are exactly the HARDEST to borrow, and the most likely to be on the
//     hard-to-borrow list at punitive rates. NONE of that is modelled here.
//   * ASYMMETRIC TAIL: a long is bounded at -100%; a SHORT IS UNBOUNDED. V6 runs with
//     NO STOP (its p01 was -17.8% and that was survivable). An unstopped short into a
//     squeeze is not. See project_next_session_2026-07-10: "SHORT book needs STOPS
//     (extreme DD, worth PF sacrifice; long MR bounded -100% = not urgent)."
//   * UPTICK/SSR: a name down >=10% on the day triggers SSR the next session — shorts
//     can only be filled on an uptick. Not modelled.
//   * PRIOR ART: MaxFlyerV2/V3 already fade pops (short). MaxFlyerV3 is ⚠ UNCONFIRMED
//     (its brv20d lever fails the lookahead audit). Do not assume that book's findings
//     port; this is a clean-sheet measurement.
//
// Everything else — the SAMPLER contract, the reset counters, the recorded feature set
// — is V6's, mirrored. See docs/diprider_v6_results.md for the long-side findings this
// exists to test against.
//
// ⭐ V6 IS A SAMPLER, NOT A BOOK. It runs at MaxConcurrent = 0 (unlimited) BY DESIGN
// (user): with no arm/re-arm throttle, every consecutive new low opens another
// position — i.e. it AVERAGES DOWN. That is NOT tradable as-is (unbounded capital),
// and V5 F6 measured the inflation: PF 1.429 (mc=0) vs 1.285 (mc=1).
// The POINT is that it removes PATH DEPENDENCY: every (bar, feature-vector) that
// fires becomes an INDEPENDENT row with its own forward outcome, so the whole study
// runs as post-hoc SQL over one CSV instead of re-running the engine per parameter.
//   ⚠ THEREFORE: PF/net on the raw V6 book are ATTRIBUTION numbers, NOT portfolio
//   numbers. Any cell the analysis selects MUST be re-run at mc=1 before it is
//   believed as a tradable result.
//
// ⭐ THE RESET COUNTERS (user's idea) make the averaging-down MEASURABLE rather than
// merely a bias to suppress. See NewLowCounters.
//
// Live-safe: every feature folds from D's own realized bars. No avgvol20 anywhere
// (docs/lookahead_protocol.md).
// ===========================================================================

/// One 1-minute RTH bar, already converted to ET minutes-since-midnight and
/// split-adjusted to the candidate's daily scale.
type MinuteBar =
    { etMin: int
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// ⭐ THE NEW-HIGH RESET MACHINE — the mirror of V6's NewLowCounters.
///
/// State machine (both counters share it):
///   * start UNSET (-1 = disarmed).
///   * on a NEW N-minute HIGH: ARM (-1 -> 0) if not already armed.
///   * while armed:
///       - `BarsSinceFirstHigh` increments on EVERY bar.
///       - `HighsSinceFirstHigh` increments ONLY on each further new high.
///   * on a NEW M-minute LOW: RESET both to unset — the up-leg is over.
///
/// `HighsSinceFirstHigh = 0` -> the FIRST high of the leg (the initial pop).
/// `HighsSinceFirstHigh = 3` -> the 4th consecutive new high (averaged UP 3x).
/// `BarsSinceFirstHigh`      -> how long the up-leg has been running, in clock time.
///
/// ⭐ THE QUESTION THIS EXISTS TO ANSWER: V6 F1 found the LONG edge is in the BLEEDERS
/// (PF 1.239 at the first dip -> 1.872 at 5-7 lows -> 0.513 at 13+). Does the short side
/// mirror that (fade the 5th consecutive pop, not the first)? Or does it INVERT — i.e.
/// is a name making its 5th new high a SQUEEZE that keeps going, and the first pop the
/// only safe fade? The asymmetry of the short tail makes this the load-bearing question.
[<Sealed>]
type NewHighCounters() =
    let mutable bars = -1
    let mutable lows = -1
    /// Bars since the FIRST new high of this up-leg. -1 = disarmed (no leg open).
    member _.BarsSinceFirstHigh = bars
    /// Further new highs since the first of this leg. -1 = disarmed; 0 = this IS the
    /// first high; N = the (N+1)th high, i.e. averaged UP N times.
    member _.HighsSinceFirstHigh = lows
    /// Is an up-leg currently open?
    member _.Armed = bars >= 0
    /// A new high fired: arm the leg (idempotent), or count another high into it.
    member _.OnNewHigh () =
        if bars < 0 then
            bars <- 0
            lows <- 0
        else
            lows <- lows + 1
    /// Advance one bar. No-op while disarmed.
    member _.Step () = if bars >= 0 then bars <- bars + 1
    /// A new M-minute low fired: the up-leg is over — disarm.
    member _.Reset () =
        bars <- -1
        lows <- -1

/// Intraday position life-cycle.
type IntraPosState =
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "target" | "moc"

/// One intraday trip. `EntryMin`/`EntryPx` are the FILL values (the entry bar's close).
/// Every field below is the feature state INCLUDING the entry bar (it has closed —
/// using it is not lookahead). ⭐ NOTHING here GATES; it is all recorded for post-hoc.
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      // ----- ⭐ the reset counters (the point of V6) -----
      BarsSinceFirstHigh: int    // bars since this up-leg's first new high
      HighsSinceFirstHigh: int   // 0 = the FIRST high of the leg; N = averaged UP N times
      // ----- location -----
      VwapAtEntry: float
      DistVwap: float            // close/vwap - 1 (> 0 by construction when RequireBelowVwap)
      DistVwapZ: float           // ⭐ session-cumulative z-score of DistVwap (linear, close/vwap-1)
      DistVwapZLog: float        // ⭐ session-cumulative z-score of LOG(close/vwap) — the log-space twin
      IsNewSessHigh: bool        // ⭐ is this entry (a new 20m high) ALSO a new SESSION close-high?
      PrevSessVolHigh: float     // ⭐ the session 1m-volume high AS OF THE PRIOR BAR (user's feature)
      IsNewSessVolHigh: bool     // ⭐ did THIS bar make a new SESSION 1m-volume high?
      VolZLog: float             // ⭐ session-cum z-score of LOG(bar volume) — volume abnormality, log space
      VolZLin: float             // ⭐ session-cum z-score of RAW bar volume — normal space (to compare)
      EmaAtEntry: float
      PctChgSinceOpen: float
      Chg1d: float               // entry / prev daily close - 1
      Chg3d: float               // entry / close-3d-ago - 1
      // ----- volatility / trend -----
      LogAtr20: float
      Adx14: float               // ⭐ trend STRENGTH (direction-agnostic)
      PlusDi14: float
      MinusDi14: float
      // ----- price slopes (OLS on log close) -----
      PriceSlopeOpen: float
      PriceSlope60: float
      PriceSlope20: float
      // ----- volume slopes (OLS on log volume) -----
      VolSlopeOpen: float
      VolSlope60: float
      VolSlope20: float
      // ----- volume level -----
      BarVol: float              // the entry bar's raw 1m volume
      Brv15m: float              // bar volume / permin15m — LIVE-SAFE
      Rvol5m: float              // trailing-5m avg vol / permin15m — the V5 F3 exhaustion ratio
      VolClimb: float            // recorded ONLY (V5 F4: it INVERTS as a gate)
      CumVol: float              // session cumulative volume to entry
      State: IntraPosState }

/// MaxRiderV1 config — the MIRROR of V6's. Deliberately TINY: everything else is a
/// RECORDED feature, sliced post-hoc in SQL.
type IntradayConfig =
    { EntryHighWindow: int     // ⭐ ENTRY reference: SHORT when close >= the prior N-minute MAX of closes.
                               // The mirror of V6's EntryLowWindow. Default 20.
      ExitLowWindow: int       // COVER TARGET: close <= the prior N-minute MIN of closes. Default 5 —
                               // V6 F16/F17 found the FAST target dominates on the LONG side (PF 1.723 @5m
                               // vs 1.257 @60m): the edge IS the snap-back and it is spent early.
                               // ⚠ A LONG-side finding. F16 showed the exit window CHANGES WHICH FEATURES
                               // MATTER, so re-measure it here rather than assuming it ports.
      RequireBelowVwap: bool   // require close > VWAP. DEFAULT TRUE — V5 F6 proved it LOAD-BEARING
                               // (dropping it: PF 1.429 -> 1.319 AND avg/tr 0.670% -> 0.561% across 8x the
                               // trips). It improves PF as it tightens = a REAL lever, not a weak proxy.
      MaxConcurrent: int       // 0 = unlimited (THE SAMPLER DEFAULT — see the header). 1 = a real book.
      VolWindow: int           // the 20m window for ATR / slopes / vol_climb
      EmaPeriod: int           // the 9-EMA (recorded only)
      AdxPeriod: int           // Wilder ADX/DI period (default 14)
      SessionStartMin: int
      FeatureStartMin: int     // 570 = 09:30, the RTH open
      EntryStartMin: int       // 600 = 10:00
      EntryEndMin: int         // 810 = 13:30
      MocMin: int              // 960 = 16:00
      MinHighsIntoLeg: int     // ⭐ WAIT FOR THE Kth HIGH (mirror of V6 F3). Require highs_since_first_high >= this, and take
                               // only ONE position per up-leg (the leg is then consumed until a new low
                               // resets it). 0 = off (take every high = the sampler).
                               //   F1 found the edge is in the BLEEDERS, not the first dip; F3 proved that cell
                               //   is harvestable WITHOUT averaging down. K=5: PF 1.968 / +1.48%/tr / 1068 trips,
                               //   positive EVERY year — vs K=0's 1.255 / +0.47%. The PF humps at K=5 and rolls
                               //   off by K=8 (2 independent selection rules agree on the peak).
                               //   ⚠ At K>0 this is a REAL mc=1-style book: one position per leg, no averaging
                               //   down. Set --max-concurrent 1 as well to enforce it globally.
      MaxAtrPct: float         // ⭐ log-ATR CEILING (F8): reject if 20m log-ATR >= this. Default 0.035.
                               // HIGH ATR INVERTS on MR — the exact MIRROR of V4's momentum book, where PF
                               // scaled MONOTONICALLY with ATR ("THE MAIN LEVER", V4 F3). Momentum wanted the
                               // violent thrust; MR wants RANGE but NOT CHAOS. Past ~3.5% the name is not
                               // oscillating, it is BROKEN: >=0.05 -> PF 0.755 at -1.66%/trade, win 52.4%;
                               // >=0.07 -> PF 0.541 at -5.83%/trade. +inf = off.
      MinAtrPct: float }       // the ONLY other gate: 20m log-ATR >= this. MR needs RANGE to revert
                               // across. 0 = off. (V5 F6: it does far LESS work here than in momentum —
                               // 1.350 ungated vs 1.429 — so it is a CAPACITY dial, not the edge.)

/// The V6 engine. One instance per (ticker, day).
///
/// `permin15m` = D's OWN mean 1m volume over 09:30-09:45 (vol_0945 / nbar_0945) — the
/// LIVE-SAFE per-minute volume baseline. Complete at 09:45, and EntryStartMin = 10:00,
/// so it clears the knowability clock (R3). ⚠ That alignment is LOAD-BEARING: drop
/// EntryStartMin below 09:45 and it silently becomes a lookahead.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, close1d: float, close3d: float, permin15m: float) =
    // ----- the two windows that ARE the system -----
    let closeHighN = MaxMa(cfg.EntryHighWindow)  // N-minute MAX of closes — the ENTRY (short) reference
    let closeLowN  = MinMa(cfg.ExitLowWindow)    // N-minute MIN of closes — the COVER target
    // ⭐ SESSION extremes (running from the feature anchor), for the post-hoc breakdowns (user, 2026-07-17):
    //   sessHigh    = running MAX of closes — "is this new 20m high ALSO the SESSION high?"
    //   sessVolHigh = running MAX of 1m bar VOLUME — "did this bar make a NEW SESSION VOLUME high?"
    let sessHigh    = RunMaxMa<float>()
    let sessVolHigh = RunMaxMa<int64>()
    // ----- recorded features -----
    let vwap       = RatioMa()
    let ema        = EmaMa(cfg.EmaPeriod)
    let atrLog     = AvgMa(cfg.VolWindow)
    let adx        = AdxMa(cfg.AdxPeriod)
    let distZ      = CumStdMa()                  // ⭐ session mean/σ of dist_vwap (close/vwap-1) → the z-score
    let distZLog   = CumStdMa()                  // ⭐ session mean/σ of LOG(close/vwap) → the LOG-space VWAP z.
                                                 // The true log analogue: symmetric (a +x% and -x% move are
                                                 // equal-and-opposite in log space). log(1+x)~=x for the ~1-3%
                                                 // distances here, so it tracks distZ closely except in the fat
                                                 // tail — recorded to VALIDATE, not assume, like vol_z (F6).
    let volZLog    = CumStdMa()                  // ⭐ session mean/σ of LOG(bar volume) → the volume z (log space)
    let volZLin    = CumStdMa()                  // ⭐ session mean/σ of RAW bar volume → the volume z (normal space)
    let priceOls20 = OlsSlopeMa(cfg.VolWindow)
    let priceOls60 = OlsSlopeMa(60)
    let priceOlsOpen = OlsSlopeMa(400)           // ~the whole RTH session (390 bars) = "from open"
    let volOls20   = OlsSlopeMa(cfg.VolWindow)
    let volOls60   = OlsSlopeMa(60)
    let volOlsOpen = OlsSlopeMa(400)
    let volEma     = EmaMa(cfg.EmaPeriod)
    let volEmaMin  = MinMa(cfg.VolWindow)
    let vol5avg    = AvgMa(5)
    let counters   = NewHighCounters()           // ⭐ the reset machine
    // ⭐ F3: at MinLowsIntoLeg > 0 a leg yields at most ONE position. Set when that leg fires; cleared by
    // the same 20m-high reset that clears the counters. This is what makes "wait for the Kth low" a real
    // book (no averaging down) rather than a post-hoc slice of the sampler.
    let mutable legConsumed = false

    let positions = ResizeArray<IntradayPosition>()
    let mutable dayOpen : float voption = ValueNone
    let mutable cumVol = 0.0
    let mutable prevBar : MinuteBar voption = ValueNone
    // STRICTLY-PRIOR window snapshots — captured BEFORE this bar's close is pushed.
    // ⚠ If the current close were inside its own window, "close >= N-max" would be
    // TRIVIALLY TRUE on every bar and the trigger would fire constantly.
    let mutable sCloseLowN  : float voption = ValueNone
    let mutable sCloseHighN : float voption = ValueNone
    // strictly-prior session extremes — captured BEFORE this bar folds. sSessVolHigh is exactly "the
    // session volume high AS OF THE PRIOR BAR" the user asked to record; the new-high tests compare THIS
    // bar against the session that EXCLUDES it.
    let mutable sSessHigh    : float voption = ValueNone
    let mutable sSessVolHigh : int64 voption = ValueNone

    let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan

    member _.Ticker = ticker
    member _.Day = day
    member _.Positions = positions :> seq<IntradayPosition>
    member _.OpenCount =
        let mutable k = 0
        for p in positions do (match p.State with Holding -> k <- k + 1 | ExitedAt _ -> ())
        k
    member this.HasSlot = cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent

    member private _.DistVwapNow (close: float) : float voption =
        match vwap.State with
        | ValueSome v when v > 0.0 -> ValueSome (close / v - 1.0)
        | _ -> ValueNone

    /// Advance one open position. Exit precedence: target -> moc. Both fill at the bar close.
    /// NOTE: NO STOP. V5 F6 measured the 9-EMA stop as DESTRUCTIVE here (PF 1.429 -> 1.164,
    /// win 65% -> 47.7%) — it arms off the 20m-EMA-LOW, exactly what MR buys INTO, so it fires
    /// at entry. ⚠ That leaves the tail unbounded (V5 F6: p01 = -17.8%). A proper MR stop is owed.
    member private _.Advance (bar: MinuteBar) (priorLow: float voption) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            // SHORT cover: price has fallen back to the prior M-minute MIN of closes.
            let targetHit = match priorLow with ValueSome lo -> bar.close <= lo | ValueNone -> false
            if targetHit then { pos with State = ExitedAt (bar.etMin, bar.close, "target") }
            elif bar.etMin >= cfg.MocMin then { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar.
    member this.Process (bar: MinuteBar) =
        if bar.etMin < cfg.FeatureStartMin then () else

        // ===== 1. capture the STRICTLY-PRIOR windows BEFORE this close folds =====
        sCloseLowN  <- closeLowN.State
        sCloseHighN <- closeHighN.State
        sSessHigh    <- sessHigh.State       // session close-high as of the PRIOR bar
        sSessVolHigh <- sessVolHigh.State     // session volume-high as of the PRIOR bar (user's feature)
        let priorLow  = sCloseLowN
        let priorHigh = sCloseHighN

        // ===== 2. fold this bar into every rolling structure =====
        if dayOpen.IsNone then dayOpen <- ValueSome bar.``open``
        cumVol <- cumVol + float bar.volume
        let tp = (bar.high + bar.low + bar.close) / 3.0
        vwap.Push(tp * float bar.volume, float bar.volume)
        // the z accumulator reads dist AFTER the VWAP push (this-bar-inclusive)
        this.DistVwapNow bar.close |> ValueOption.iter distZ.Push
        (match vwap.State with ValueSome v when v > 0.0 && bar.close > 0.0 -> distZLog.Push (log (bar.close / v)) | _ -> ())
        // volume z accumulators: log space handles the heavy tail (bar_vol spans 100 -> 39M); linear is
        // recorded alongside so the log transform can be VALIDATED, not assumed (user).
        volZLog.Push (log (float (max bar.volume 1L)))
        volZLin.Push (float bar.volume)
        match prevBar with
        | ValueSome p when bar.high > 0.0 && bar.low > 0.0 && p.close > 0.0 ->
            log (max bar.high p.close / min bar.low p.close) |> atrLog.Push
        | _ -> ()
        ema.Push bar.close
        adx.Push(bar.high, bar.low, bar.close)
        if bar.close > 0.0 then
            let lc = log bar.close
            priceOls20.Push lc
            priceOls60.Push lc
            priceOlsOpen.Push lc
        let lv = log (float (max bar.volume 1L))
        volOls20.Push lv
        volOls60.Push lv
        volOlsOpen.Push lv
        volEma.Push (float bar.volume)
        volEma.State |> ValueOption.iter volEmaMin.Push
        vol5avg.Push (float bar.volume)
        closeLowN.Push  bar.close
        closeHighN.Push bar.close
        sessHigh.Push    bar.close
        sessVolHigh.Push bar.volume

        // ===== 3. advance open positions (an exit and an entry may share a bar) =====
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar priorLow positions.[i]

        // ===== 4. ⭐ the reset machine =====
        // Step FIRST so BarsSinceFirstLow counts bars ELAPSED since the leg's first low.
        counters.Step()
        let isNewLow  = match priorLow  with ValueSome lo -> bar.close <= lo | ValueNone -> false
        let isNewHigh = match priorHigh with ValueSome hi -> bar.close >= hi | ValueNone -> false
        if isNewHigh then counters.OnNewHigh()

        // ===== 5. entry =====
        let inWindow =
            bar.etMin >= cfg.EntryStartMin
            && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || bar.etMin <= cfg.EntryEndMin)
        // ⭐ THE ATR BAND (F8/F10): a FLOOR and a CEILING. Both are needed — sub-floor is dead
        // (PF ~1.03, below costs) and supra-ceiling INVERTS (PF 0.755 at -1.66%/tr).
        let atrOk =
            (cfg.MinAtrPct <= 0.0
             || (match atrLog.State with ValueSome a -> a >= cfg.MinAtrPct | ValueNone -> false))
            && (Double.IsPositiveInfinity cfg.MaxAtrPct
                || (match atrLog.State with ValueSome a -> a < cfg.MaxAtrPct | ValueNone -> false))
        // ⭐ F3 "wait for the Kth low": deep enough into THIS leg, and the leg not already used.
        let legOk =
            cfg.MinHighsIntoLeg <= 0
            || (not legConsumed && counters.HighsSinceFirstHigh >= cfg.MinHighsIntoLeg)
        let vwapOk =
            not cfg.RequireBelowVwap
            || (match vwap.State with ValueSome v -> v > 0.0 && bar.close < v | ValueNone -> false)
        if inWindow && atrOk && vwapOk && legOk && isNewHigh && this.HasSlot then
            if cfg.MinHighsIntoLeg > 0 then legConsumed <- true
            let dist = this.DistVwapNow bar.close
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  // ⭐ read the counters NOW — the reset in step 6 must not affect this trip.
                  BarsSinceFirstHigh = counters.BarsSinceFirstHigh
                  HighsSinceFirstHigh = counters.HighsSinceFirstHigh
                  VwapAtEntry = vv vwap.State
                  DistVwap = vv dist
                  DistVwapZ = vv (match dist with ValueSome d -> distZ.Z d | ValueNone -> ValueNone)
                  DistVwapZLog = vv (match vwap.State with ValueSome v when v > 0.0 && bar.close > 0.0 -> distZLog.Z (log (bar.close / v)) | _ -> ValueNone)
                  // ⭐ SESSION-HIGH features (strictly-prior comparisons — no lookahead):
                  IsNewSessHigh = (match sSessHigh with ValueSome h -> bar.close >= h | ValueNone -> true)
                  PrevSessVolHigh = (match sSessVolHigh with ValueSome v -> float v | ValueNone -> nan)
                  IsNewSessVolHigh = (match sSessVolHigh with ValueSome h -> bar.volume >= h | ValueNone -> true)
                  VolZLog = vv (volZLog.Z (log (float (max bar.volume 1L))))
                  VolZLin = vv (volZLin.Z (float bar.volume))
                  EmaAtEntry = vv ema.State
                  PctChgSinceOpen = (match dayOpen with ValueSome o when o > 0.0 -> bar.close / o - 1.0 | _ -> nan)
                  Chg1d = (if close1d > 0.0 then bar.close / close1d - 1.0 else nan)
                  Chg3d = (if close3d > 0.0 then bar.close / close3d - 1.0 else nan)
                  LogAtr20 = vv atrLog.State
                  Adx14 = vv adx.State
                  PlusDi14 = vv adx.PlusDi
                  MinusDi14 = vv adx.MinusDi
                  PriceSlopeOpen = vv priceOlsOpen.Slope
                  PriceSlope60 = vv priceOls60.Slope
                  PriceSlope20 = vv priceOls20.Slope
                  VolSlopeOpen = vv volOlsOpen.Slope
                  VolSlope60 = vv volOls60.Slope
                  VolSlope20 = vv volOls20.Slope
                  BarVol = float bar.volume
                  Brv15m = (if permin15m > 0.0 then float bar.volume / permin15m else nan)
                  Rvol5m = (match vol5avg.State with ValueSome v when permin15m > 0.0 -> v / permin15m | _ -> nan)
                  VolClimb =
                    (match volEma.State, volEmaMin.State with
                     | ValueSome v, ValueSome m when v > 0.0 -> (v - m) / v
                     | _ -> nan)
                  CumVol = cumVol
                  State = Holding }

        // ===== 6. the reset fires LAST: a new M-minute low closes the up-leg =====
        if isNewLow then
            counters.Reset()
            legConsumed <- false      // a fresh leg may fire again
        prevBar <- ValueSome bar

    /// Flatten any still-open position at the last bar (MOC).
    member _.Flatten (lastBar: MinuteBar) =
        for i in 0 .. positions.Count - 1 do
            match positions.[i].State with
            | Holding -> positions.[i] <- { positions.[i] with State = ExitedAt (lastBar.etMin, lastBar.close, "moc") }
            | ExitedAt _ -> ()
