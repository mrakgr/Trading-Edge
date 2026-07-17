module TradingEdge.DipRiderV6.Intraday

open System
open TradingEdge.RollingMa

// ===========================================================================
// DipRiderV6 — INTRADAY MEAN REVERSION. A clean-sheet debloat of DipRiderV5.
//
// WHY V6 EXISTS (user, 2026-07-17): "Momentum trading just keeps failing us
// persistently intraday, so we'll refocus on intraday mean reversion."
//
// V5's momentum machinery is GONE — not disabled, DELETED: the arm/re-arm state
// machine, all three EMA-high breakout timers, the per-window vol_climb floors,
// sum6, the price-slope gate, the stop-distance floor, the vol-stop scalp exit,
// and every V4-fitted entry gate (chg1d / chg3d / ema-vs-vwap / tightness). All of
// it was tuned for MOMENTUM against a CONTAMINATED universe, and V5 F4 proved at
// least one V4 "dominant lever" (vol_climb) INVERTS once the lookahead is removed.
//
// THE ENTIRE SYSTEM:
//   ENTRY: close <= the STRICTLY-PRIOR N-minute MIN of 1m-bar CLOSES  (N = 20 or 60)
//          AND close > VWAP
//   EXIT:  close >= the STRICTLY-PRIOR 20m MAX of closes ("target"), or MOC.
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

/// ⭐ THE NEW-LOW RESET MACHINE (user, 2026-07-17). Adopted from the old DipRider
/// reset machinery but INVERTED: the original reset its timers on every new LOW;
/// V6 arms on a new low and resets on a new 20m HIGH.
///
/// State machine (both counters share it):
///   * start UNSET (-1 = disarmed).
///   * on a NEW N-minute LOW: ARM (-1 -> 0) if not already armed.
///   * while armed:
///       - `BarsSinceFirstLow` increments on EVERY bar.
///       - `LowsSinceFirstLow` increments ONLY on each further new low.
///   * on a NEW 20m HIGH: RESET both to unset — the down-leg is over.
///
/// The two together separate HOW DEEP INTO THE AVERAGING-DOWN SEQUENCE a trade is
/// from HOW STALE the leg is — different questions a single counter would conflate:
///   `LowsSinceFirstLow = 0` -> this is the FIRST low of the leg (the initial dip).
///   `LowsSinceFirstLow = 3` -> the 4th consecutive new low (averaged down 3x).
///   `BarsSinceFirstLow`     -> how long the leg has been running, in clock time.
///
/// This directly answers the question mc=0 was hiding: is the edge in the FIRST dip,
/// or in the persistent bleeders?
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
    /// A new 20m high fired: the down-leg is over — disarm.
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
      BarsSinceFirstLow: int     // bars since this down-leg's first new low
      LowsSinceFirstLow: int     // 0 = the FIRST low of the leg; N = averaged down N times
      // ----- location -----
      VwapAtEntry: float
      DistVwap: float            // close/vwap - 1 (> 0 by construction when RequireAboveVwap)
      DistVwapZ: float           // ⭐ session-cumulative z-score of DistVwap (linear, close/vwap-1)
      DistVwapZLog: float        // ⭐ session-cumulative z-score of LOG(close/vwap) — the log-space twin
      VolZLog: float             // ⭐ session-cum z-score of LOG(bar volume) — volume abnormality, log space
      VolZLin: float             // ⭐ session-cum z-score of RAW bar volume — normal space (to compare)
      IsNewSessLow: bool         // ⭐ is this entry (a new 20m low) ALSO a new SESSION close-low? (free-fall flag)
      PrevSessVolHigh: float     // ⭐ the session 1m-volume high as of the PRIOR bar
      IsNewSessVolHigh: bool     // ⭐ did THIS bar make a new SESSION 1m-volume high? (volume spike)
      EmaAtEntry: float
      PctChgSinceOpen: float
      BarPct: float              // ⭐ the ENTRY BAR's single-bar move: close/prev-bar-close - 1. For a long
                                 // this is the FLUSH depth (LowFlyer's main lever); for a short, the pop.
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

/// V6 config. Deliberately TINY — if a knob is not here, it was momentum machinery
/// and it is gone. Everything else is a RECORDED feature, sliced post-hoc in SQL.
type IntradayConfig =
    { EntryLowWindow: int      // ⭐ ENTRY reference: close <= the prior N-minute MIN of closes. 20 = the
                               // V5 F6 book; 60 = a deeper, rarer dip (user's idea).
      ExitHighWindow: int      // TARGET: close >= the prior N-minute MAX of closes. Default 20.
      RequireAboveVwap: bool   // require close > VWAP. DEFAULT TRUE — V5 F6 proved it LOAD-BEARING
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
      MinLowsIntoLeg: int      // ⭐ WAIT FOR THE Kth LOW (F3). Require lows_since_first_low >= this, and take
                               // only ONE position per down-leg (the leg is then consumed until a 20m high
                               // resets it). 0 = off (take every low = the sampler).
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
    let closeLowN  = MinMa(cfg.EntryLowWindow)   // N-minute MIN of closes — the ENTRY reference
    let closeHighN = MaxMa(cfg.ExitHighWindow)   // N-minute MAX of closes — the EXIT target
    // ----- recorded features -----
    let vwap       = RatioMa()
    let ema        = EmaMa(cfg.EmaPeriod)
    let atrLog     = AvgMa(cfg.VolWindow)
    let adx        = AdxMa(cfg.AdxPeriod)
    let distZ      = CumStdMa()                  // ⭐ session mean/σ of dist_vwap (close/vwap-1) → the z-score
    let distZLog   = CumStdMa()                  // ⭐ session mean/σ of LOG(close/vwap) → the LOG-space VWAP z.
    let volZLog    = CumStdMa()                  // ⭐ session mean/σ of LOG(bar volume) → the volume z (log space)
    let volZLin    = CumStdMa()                  // ⭐ session mean/σ of RAW bar volume → the volume z (normal space)
    // ⭐ SESSION LOW (running MIN of closes) — the MIRROR of MaxRider's sessHigh. "is this new 20m-low dip
    // ALSO a new SESSION low?" A session-low dip is a name in free-fall — the long-side analogue of the
    // short's don't-fade-the-session-high (F9).
                                                 // The true log analogue: symmetric (a +x% and -x% move are
                                                 // equal-and-opposite in log space). log(1+x)~=x for the ~1-3%
                                                 // distances here, so it tracks distZ closely except in the fat
                                                 // tail — recorded to VALIDATE, not assume, like vol_z (F6).
    let priceOls20 = OlsSlopeMa(cfg.VolWindow)
    let priceOls60 = OlsSlopeMa(60)
    let priceOlsOpen = OlsSlopeMa(400)           // ~the whole RTH session (390 bars) = "from open"
    let volOls20   = OlsSlopeMa(cfg.VolWindow)
    let volOls60   = OlsSlopeMa(60)
    let volOlsOpen = OlsSlopeMa(400)
    let volEma     = EmaMa(cfg.EmaPeriod)
    let volEmaMin  = MinMa(cfg.VolWindow)
    let vol5avg    = AvgMa(5)
    let sessLow    = RunMinMa<float>()           // ⭐ running session MIN of closes (mirror of MaxRider sessHigh)
    let sessVolHigh = RunMaxMa<int64>()          // ⭐ running session MAX of 1m bar VOLUME (a volume SPIKE flag;
                                                 // same on both sides — volume has no directional "low" mirror)
    let counters   = NewLowCounters()            // ⭐ the reset machine
    // ⭐ F3: at MinLowsIntoLeg > 0 a leg yields at most ONE position. Set when that leg fires; cleared by
    // the same 20m-high reset that clears the counters. This is what makes "wait for the Kth low" a real
    // book (no averaging down) rather than a post-hoc slice of the sampler.
    let mutable legConsumed = false

    let positions = ResizeArray<IntradayPosition>()
    let mutable dayOpen : float voption = ValueNone
    let mutable cumVol = 0.0
    let mutable prevBar : MinuteBar voption = ValueNone
    // STRICTLY-PRIOR window snapshots — captured BEFORE this bar's close is pushed.
    // ⚠ If the current close were inside its own window, "close <= N-min" would be
    // TRIVIALLY TRUE on every bar and the trigger would fire constantly.
    let mutable sCloseLowN  : float voption = ValueNone
    let mutable sCloseHighN : float voption = ValueNone
    let mutable sSessLow    : float voption = ValueNone   // session low as of the PRIOR bar (strictly-prior)
    let mutable sSessVolHigh : int64 voption = ValueNone  // session vol-high as of the PRIOR bar

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
    member private _.Advance (bar: MinuteBar) (priorHigh: float voption) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            let targetHit = match priorHigh with ValueSome hi -> bar.close >= hi | ValueNone -> false
            if targetHit then { pos with State = ExitedAt (bar.etMin, bar.close, "target") }
            elif bar.etMin >= cfg.MocMin then { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar.
    member this.Process (bar: MinuteBar) =
        if bar.etMin < cfg.FeatureStartMin then () else

        // ===== 1. capture the STRICTLY-PRIOR windows BEFORE this close folds =====
        sCloseLowN  <- closeLowN.State
        sCloseHighN <- closeHighN.State
        sSessLow    <- sessLow.State
        sSessVolHigh <- sessVolHigh.State
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
        sessLow.Push    bar.close
        sessVolHigh.Push bar.volume

        // ===== 3. advance open positions (an exit and an entry may share a bar) =====
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar priorHigh positions.[i]

        // ===== 4. ⭐ the reset machine =====
        // Step FIRST so BarsSinceFirstLow counts bars ELAPSED since the leg's first low.
        counters.Step()
        // ⭐ STRICT inequality (user, 2026-07-17): a new 20m low must be GENUINELY lower than the prior
        // floor, not merely TIE it. `<=` re-fired on `bar.close == priorLow` — two consecutive identical
        // closes — which turned out to be thin-tape round-number pinning on high-priced names in a quiet
        // minute (F21), NOT a real dip. Strict `<` removes those phantom entries at the source. The exit
        // target below stays inclusive (a tie-to-target is a legitimate cover). isNewHigh is the reset event,
        // also made strict so "new high" means genuinely higher everywhere the counter uses it.
        let isNewLow  = match priorLow  with ValueSome lo -> bar.close < lo | ValueNone -> false
        let isNewHigh = match priorHigh with ValueSome hi -> bar.close > hi | ValueNone -> false
        if isNewLow then counters.OnNewLow()

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
            cfg.MinLowsIntoLeg <= 0
            || (not legConsumed && counters.LowsSinceFirstLow >= cfg.MinLowsIntoLeg)
        let vwapOk =
            not cfg.RequireAboveVwap
            || (match vwap.State with ValueSome v -> v > 0.0 && bar.close > v | ValueNone -> false)
        if inWindow && atrOk && vwapOk && legOk && isNewLow && this.HasSlot then
            if cfg.MinLowsIntoLeg > 0 then legConsumed <- true
            let dist = this.DistVwapNow bar.close
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  // ⭐ read the counters NOW — the reset in step 6 must not affect this trip.
                  BarsSinceFirstLow = counters.BarsSinceFirstLow
                  LowsSinceFirstLow = counters.LowsSinceFirstLow
                  VwapAtEntry = vv vwap.State
                  DistVwap = vv dist
                  DistVwapZ = vv (match dist with ValueSome d -> distZ.Z d | ValueNone -> ValueNone)
                  DistVwapZLog = vv (match vwap.State with ValueSome v when v > 0.0 && bar.close > 0.0 -> distZLog.Z (log (bar.close / v)) | _ -> ValueNone)
                  VolZLog = vv (volZLog.Z (log (float (max bar.volume 1L))))
                  VolZLin = vv (volZLin.Z (float bar.volume))
                  IsNewSessLow = (match sSessLow with ValueSome lo -> bar.close <= lo | ValueNone -> true)
                  PrevSessVolHigh = (match sSessVolHigh with ValueSome v -> float v | ValueNone -> nan)
                  IsNewSessVolHigh = (match sSessVolHigh with ValueSome h -> bar.volume >= h | ValueNone -> true)
                  EmaAtEntry = vv ema.State
                  PctChgSinceOpen = (match dayOpen with ValueSome o when o > 0.0 -> bar.close / o - 1.0 | _ -> nan)
                  BarPct = (match prevBar with ValueSome pb when pb.close > 0.0 -> bar.close / pb.close - 1.0 | _ -> nan)
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

        // ===== 6. the reset fires LAST: a new 20m high closes the down-leg =====
        if isNewHigh then
            counters.Reset()
            legConsumed <- false      // a fresh leg may fire again
        prevBar <- ValueSome bar

    /// Flatten any still-open position at the last bar (MOC).
    member _.Flatten (lastBar: MinuteBar) =
        for i in 0 .. positions.Count - 1 do
            match positions.[i].State with
            | Holding -> positions.[i] <- { positions.[i] with State = ExitedAt (lastBar.etMin, lastBar.close, "moc") }
            | ExitedAt _ -> ()
