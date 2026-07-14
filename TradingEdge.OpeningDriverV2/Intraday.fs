module TradingEdge.OpeningDriverV2.Intraday

open System
open TradingEdge.RollingMa

// =============================================================================
// OpeningDriverV2 — the TRADABLE opening-drive engine (long-only).
//
// V1 (OpeningDriver) was a pure recorder: it emitted EVERY bar in the entry window
// [open+15, open+60] as a candidate trip, and all gating was done POST-HOC on the
// CSV. That overcounts — the same drive is counted at every minute it stays in the
// window (a +20%-day stock that qualifies at 09:45 also qualifies at 09:46, …),
// inflating trip counts, PF and net.
//
// V2 restores an ARM/DISARM state machine. Per (ticker, day) the engine scans the
// entry window and, on the FIRST bar where ALL settled gates hold, opens ONE
// position at that bar's close, then DISARMS for the rest of the day — no re-arm
// (yet). One trip per drive-day = the real book.
//
// The settled gates (the F1–F24 headline book), all this-bar-inclusive:
//   chg_1d      >= MinChg1d              (up >=20% on the day)
//   chg_3d      in [MinChg3d, MaxChg3d]  (day-strength band [0, 1.5])
//   log_atr_20  >= MinLogAtr             (a token jumpiness guard, 0.013)
//   stop_dist   >= MinStopDistPct        (>=3% room to the 9-EMA session-min stop)
//   bl (bars-since-EMA-low)  <  BlMax    (freshness: drop the bl=15 pile, bottomed at open)
//   bh (bars-since-EMA-high) >= BhMin    (any pullback off the high, not chasing bh=0)
//   vol_slope_20             >= MinVolSlope   — GATE or SKIP (a switch, see below)
//
// vol_slope gate vs skip (VolSlopeAsGate):
//   GATE (VolSlopeAsGate=true):  vol_slope ANDs into the arm condition. A bar that
//     fails it is not an arm candidate; the engine KEEPS SCANNING to a later bar.
//   SKIP (default, VolSlopeAsGate=false): the arm condition is everything EXCEPT
//     vol_slope. The first bar that meets it DISARMS the day; a position opens only
//     if vol_slope ALSO passed there. A vol_slope failure on the armed bar burns the
//     day (no later re-arm) — the post-hoc-filter-equivalent behaviour.
//
// The 9-EMA stop reference (StopMode): BelowVwap (live VWAP) | BelowSessEmaLow
// (the 9-EMA session-min frozen at entry). sess-ema-low DOMINATES (it lets the drive
// run) and is the V2 default. Snapshots follow the DipRiderV4 fold-then-snapshot rule:
// features INCLUDE the current (closed) bar — not lookahead.
// =============================================================================

/// One 1-minute RTH bar, split-adjusted to the candidate's daily scale.
type MinuteBar =
    { etMin: int
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// The 9-EMA stop reference: the level the live 9-EMA must drop below to stop out.
type StopMode =
    | BelowVwap        // the live session VWAP (dynamic; recomputed each bar).
    | BelowSessEmaLow  // the 9-EMA session-min, frozen at entry (a static level).

/// Intraday position life-cycle. OpeningDriver positions are Holding immediately.
type IntraPosState =
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "stop" | "moc"

/// One intraday trip. EntryMin/EntryPx = the entry bar's close (fill); the *AtEntry
/// metrics are the this-bar-inclusive indicator snapshots at that bar (no-lookahead).
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      StopLevel: float           // the stop reference at entry. BelowSessEmaLow: the frozen 9-EMA session-min.
                                 // BelowVwap: the VWAP at entry (recorded; the LIVE VWAP is used at exit).
      StopDistPct: float         // (entry - stopLevel)/entry at entry; nan if no finite stop
      // recorded feature snapshot at entry (this-bar-inclusive — the study's levers)
      LogAtr20: float            // 20m mean log-true-range (ATR%)
      PriceSlope20: float        // 20m OLS log-price slope
      VolSlope20: float          // 20m OLS log-volume slope
      EmaAtEntry: float          // the 9-EMA at entry
      VwapAtEntry: float         // session VWAP at entry
      SessEmaLowAtEntry: float   // the 9-EMA session-min at entry (the BelowSessEmaLow stop level)
      BarsSinceEmaHigh: int      // bars since the 9-EMA last made a new session HIGH (0 = this bar; -1 = none yet)
      BarsSinceEmaLow: int       // bars since the 9-EMA last made a new session LOW (0 = this bar; -1 = none yet)
      CumVolAtEntry: int64       // cumulative session volume through the entry bar (rvol_cum numerator)
      EntryType: string          // "close" (market at the arm bar's close) | "limit" (filled at a resting 9-EMA limit)
      EntryIndex: int            // 0 = first entry of the day; 1 = first re-entry; 2 = second (re-arm expectancy split)
      State: IntraPosState }

/// OpeningDriverV2 engine config = timing + windows + stop + the settled arm gates.
type IntradayConfig =
    { VolWindow: int             // trailing ATR / OLS-slope lookback in 1m BARS (default 20)
      EmaPeriod: int             // the 9-EMA period
      SessionStartMin: int       // first ET minute fed to the engine (session anchor; extremes warm from here)
      FeatureStartMin: int       // ET minute the features start folding (09:30 = the RTH open, no premarket)
      EntryStartMin: int         // earliest ET minute a window entry fires (09:30 + 15 = 09:45)
      EntryEndMin: int           // latest ET minute a window entry fires (09:30 + 60 = 10:30)
      MocMin: int                // MOC cutoff in ET minutes (960 = 16:00)
      StopMode: StopMode         // 9-EMA stop reference: BelowVwap | BelowSessEmaLow
      // ----- the settled arm gates (the F1–F24 headline book) -----
      MinChg1d: float            // day-direction floor: entry/close_1d - 1 >= this (0.20 = up >=20%)
      MinChg3d: float            // 3-day-trend band floor: entry/close_3d - 1 >= this (0.0)
      MaxChg3d: float            // 3-day-trend band ceiling: entry/close_3d - 1 <= this (1.5)
      MinLogAtr: float           // 20m log-ATR jumpiness guard (0.013)
      MinStopDistPct: float      // room to the stop: (entry-stop)/entry >= this (0.03). Ignored when TightStopFloor>0.
      TightStopFloor: float      // 0 = off (the MinStopDistPct GATE skips no-room entries). >0 (e.g. 0.03) = replace
                                 // the gate with a formulaic FLOOR: stop = max(sess-ema-low, ema_at_entry·(1−this)),
                                 // so EVERY setup trades — no-room ones get a synthetic stop 'this' below the 9-EMA.
      BlMax: int                 // bars-since-EMA-low freshness cap: bl < this (15). <=0 disables.
      BhMin: int                 // bars-since-EMA-high pullback floor: bh >= this (1). <=0 disables.
      MinVolSlope: float         // 20m OLS log-volume slope floor
      VolSlopeAsGate: bool       // true = vol_slope ANDs into the arm (keep scanning on fail);
                                 // false (default) = skip filter (first arm bar disarms; no position if vol_slope fails)
      // ----- the blow-off EXHAUSTION kill-switch (ported from MaxFlyerV3's short-arm signature) -----
      ExhaustBrv20d: float       // once ANY bar prints a new session high on brv20d >= this (single-bar vol vs the
                                 // per-minute 20d ADV: bar.volume / (avgvol20·adjRatio/390)) AND atrPct >= the floor
                                 // below, the day is LATCHED exhausted and NO further arm can fire. 0 = off (default).
      ExhaustMinAtrPct: float    // the climax bar must also have 20m log-ATR >= this (MaxFlyerV3 A-book floor 0.03).
      LimitEntry: bool           // true = PATIENT entry: when a bar's gates pass but its close > 9-EMA, rest a limit
                                 // at that bar's 9-EMA good for the NEXT bar only (fill if next low <= it, at the
                                 // 9-EMA price). Close <= 9-EMA fills at close immediately. Unfilled -> re-test gates
                                 // next bar (stays armed). false (default) = market entry at the arm bar's close.
      MaxReEntries: int          // 0 (default) = one shot per day (disarm forever after the first entry). >0 = after a
                                 // STOP exit, re-arm on the next NEW 9-EMA session low, up to this many re-entries (the
                                 // 3% stop-dist gate keeps us out until there's room again). Exhaust flushes do NOT re-arm.
      ReEntryCooldownBars: int   // min bars that must pass AFTER a stop-exit before the day can re-arm (default 0). >0
                                 // prevents same-flush re-fires (a new low prints instantly during the down-move that
                                 // stopped us, stacking 3x correlated entries within 1-3 bars) — forces a genuine reset.
      SizeUpFactor: float        // position-size multiplier when VWAP > 9-EMA at entry (F17: the trend below fair value
                                 // = the A+ cell). 1.0 (default) = flat sizing; e.g. 3.0 = 3x notional on those trades.
      ExhaustExit: bool }        // true = when the blow-off latch fires, CLOSE any open position at that bar (an
                                 // exhaustion EXIT). Independent of the arm CUT — the latch always blocks new arms
                                 // when ExhaustBrv20d>0; ExhaustExit additionally flushes the held position.

/// Snapshot of the state going INTO the current bar (strictly-prior; the true-range
/// prior close). Everything else is read live post-fold (this-bar-inclusive).
type State =
    { bar : MinuteBar voption }

/// Per-(ticker, day) intraday engine. Feed the day's RTH MinuteBar[] in time order
/// via `Process`, then `Flatten` and read `Positions`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, close1d: float, close3d: float, permin20d: float, avgVol20: float, adjRatio: float) =
    // ----- rolling intraday structures (1m timeframe; fed from the feature anchor) -----
    let atrLog   = AvgMa(cfg.VolWindow)         // mean LOG true range over the last VolWindow bars (ATR%)
    let atrLin   = AvgMa(cfg.VolWindow)         // mean ABSOLUTE true range (linear) — kept for parity/room
    let ema      = EmaMa(cfg.EmaPeriod)         // the 9-EMA
    // trailing-window OLS log slopes (fixed VolWindow bars, fed together only when both logs valid so they
    // share the identical push-index x-axis). log-close = price trend; log-volume = the volume trend.
    let priceOls = OlsSlopeMa(cfg.VolWindow)
    let volOls   = OlsSlopeMa(cfg.VolWindow)

    // session VWAP = Σ(typical·volume)/Σ(volume), typical = (h+l+c)/3, from the feature anchor.
    let vwap = RatioMa()

    // SESSION-min of the 9-EMA (running min from the anchor; ratchets DOWN only) — the BelowSessEmaLow stop.
    let mutable sessEmaLow : float voption = ValueNone
    // SESSION-max of the 9-EMA (running max, ratchets UP only) — the reference for "bars since a new EMA high".
    let mutable sessEmaHigh : float voption = ValueNone
    // BARS-SINCE the 9-EMA last made a new session HIGH / LOW. 0 on the bar that set the new extreme, +1 each
    // bar after (a momentum/exhaustion timer: bars-since-high small = just broke out; bars-since-low small =
    // just bounced off the floor). -1 until the first feature bar seeds the extremes.
    let mutable barsSinceEmaHigh = -1
    let mutable barsSinceEmaLow  = -1
    // cumulative session volume from the feature anchor (the rvol_cum numerator; read post-fold at entry).
    let mutable cumVol : int64 = 0L

    // ----- the blow-off EXHAUSTION kill-switch (MaxFlyerV3 short-arm signature: new session high + brv20d + ATR%) -----
    // brv20d baseline = the per-minute 20d ADV, split-adjusted: bar.volume (RAW) / (avgvol20·adjRatio/390). A single
    // 1m bar at brv20d=100 traded ~25.6% of a whole average day in that minute. Matches MaxFlyerV3's formula exactly.
    let brv20dBaseline = if avgVol20 > 0.0 && adjRatio > 0.0 then avgVol20 * adjRatio / 390.0 else nan
    // SESSION-high of the raw CLOSE (ratchets up) — the new-high reference for the climax test (prior-bar running high).
    let mutable sessHigh : float voption = ValueNone
    // the LATCH: set once a bar prints close > prior sessHigh with brv20d >= threshold AND atrPct >= floor. Blocks all arms.
    let mutable exhausted = false

    let mutable bar : MinuteBar voption = ValueNone
    let mutable barsSeen = 0
    // the strictly-prior snapshot (only the prior bar is needed — for the true-range prior close).
    let mutable sState : State = { bar = ValueNone }
    // the day's arm state: once the engine fires (or a skip-filter burns the day) it DISARMS.
    let mutable armed = true
    // RE-ARM state (MaxReEntries>0). entryCount = entries taken so far (cap = 1+MaxReEntries). reArmEligible is TRUE
    // by default (a new 9-EMA session low always re-arms) and is REVOKED PERMANENTLY by an exhaustion trigger (the
    // blow-off ends the day). sessLowAtExit anchors the "new low AFTER the last stop-exit" trigger (must ratchet
    // below this) so we re-arm on a FRESH low, not the level we already stopped at.
    let mutable entryCount = 0
    let mutable reArmEligible = true
    let mutable sessLowAtExit : float voption = ValueNone
    let mutable stopExitMin = -1            // etMin of the last stop-exit (for the ReEntryCooldownBars gate; -1 = none).
    // PATIENT limit entry (LimitEntry mode): a limit resting at the placing bar's 9-EMA, good for the NEXT bar
    // only. `pendingLimit` = the resting price (the 9-EMA); `pendingArm` = the arm-bar's feature snapshot to
    // stamp on the fill (so the trip records the SIGNAL bar's context, not the fill bar's). Cleared each bar.
    let mutable pendingLimit : float voption = ValueNone
    let mutable pendingArm : IntradayPosition voption = ValueNone

    let positions = ResizeArray<IntradayPosition>()

    member _.Ticker = ticker
    member _.Day = day

    /// Fold one minute bar into all rolling state (features INCLUDE the current bar — it has closed, so
    /// this is not lookahead), THEN snapshot the prior-bar pointer for the next bar's true-range read.
    member _.ProcessBar (curBar: MinuteBar) =
        let prevBar = sState.bar
        if curBar.etMin >= cfg.FeatureStartMin then
            // session VWAP (typical price · volume), cumulative from the feature anchor.
            let tp = (curBar.high + curBar.low + curBar.close) / 3.0
            vwap.Push(tp * float curBar.volume, float curBar.volume)
            // true range vs the PRIOR bar's close (linear & log ATR); needs a valid prior bar.
            match prevBar with
            | ValueSome prev when curBar.high > 0.0 && curBar.low > 0.0 && prev.close > 0.0 ->
                let pc = prev.close
                (max curBar.high pc - min curBar.low pc) |> atrLin.Push
                log (max curBar.high pc / min curBar.low pc) |> atrLog.Push
            | _ -> ()
            // trailing-window OLS log-price & log-volume slopes (fed together; volume needs a positive bar).
            priceOls.Push (log curBar.close)
            if curBar.volume > 0L then volOls.Push (log (float curBar.volume))
            // the 9-EMA, then its session extremes + the bars-since-new-extreme timers. A new session HIGH
            // (or LOW) resets that timer to 0; otherwise it counts up. Checked against the PRIOR extreme
            // (captured before the update), so the bar that MAKES the new extreme reads 0.
            ema.Push curBar.close
            ema.State |> ValueOption.iter (fun e ->
                // session LOW (ratchets down) — also the BelowSessEmaLow stop reference.
                let isNewLow = match sessEmaLow with ValueSome m -> e < m | ValueNone -> true
                sessEmaLow <- match sessEmaLow with ValueSome m -> ValueSome (min m e) | ValueNone -> ValueSome e
                barsSinceEmaLow <- if isNewLow then 0 else barsSinceEmaLow + 1
                // session HIGH (ratchets up).
                let isNewHigh = match sessEmaHigh with ValueSome m -> e > m | ValueNone -> true
                sessEmaHigh <- match sessEmaHigh with ValueSome m -> ValueSome (max m e) | ValueNone -> ValueSome e
                barsSinceEmaHigh <- if isNewHigh then 0 else barsSinceEmaHigh + 1)
            // cumulative session volume (the rvol_cum numerator).
            cumVol <- cumVol + curBar.volume
            // EXHAUSTION kill-switch: does THIS bar print a new session high on a brv20d volume spike + ATR%?
            // Tested against the STRICTLY-PRIOR sessHigh (captured before the ratchet), so a bar making the new
            // high is compared to the prior high — the MaxFlyerV3 short-arm signature. Once latched, blocks arms.
            if cfg.ExhaustBrv20d > 0.0 && not exhausted then
                let isNewHigh = match sessHigh with ValueSome h -> curBar.close > h | ValueNone -> true
                if isNewHigh then
                    let brv20d = if Double.IsNaN brv20dBaseline || brv20dBaseline <= 0.0 then nan else float curBar.volume / brv20dBaseline
                    let atrOk = match atrLog.State with ValueSome a -> a >= cfg.ExhaustMinAtrPct | ValueNone -> false
                    if not (Double.IsNaN brv20d) && brv20d >= cfg.ExhaustBrv20d && atrOk then
                        exhausted <- true
            // ratchet the raw session-high (AFTER the climax test, so the new-high bar compares to the prior high).
            sessHigh <- match sessHigh with ValueSome h -> ValueSome (max h curBar.close) | ValueNone -> ValueSome curBar.close
        // advance the prior-bar pointer for EVERY bar (so the ATR prior-close is the immediately-prior bar).
        bar      <- ValueSome curBar
        barsSeen <- barsSeen + 1
        sState   <- { bar = ValueSome curBar }

    /// Advance one open position by the current bar. Exit precedence: 9-EMA stop -> exhaust flush -> MOC. All fill
    /// at close. The exhaust flush fires (only when ExhaustExit) once the day's blow-off latch is set — the latch
    /// is flipped in ProcessBar, which runs BEFORE Advance, so the climax bar itself flushes the position.
    member private _.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            // the stop level: BelowVwap uses the LIVE VWAP (dynamic); BelowSessEmaLow uses the frozen level.
            let stopLevel =
                match cfg.StopMode with
                | BelowVwap       -> vwap.State
                | BelowSessEmaLow -> ValueSome pos.StopLevel
            let stopHit =
                match ema.State, stopLevel with
                | ValueSome e, ValueSome lvl -> e < lvl
                | _ -> false
            if stopHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "stop") }
            elif cfg.ExhaustExit && exhausted then
                { pos with State = ExitedAt (bar.etMin, bar.close, "exhaust") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar: fold the bar in, advance the open position (if any), then
    /// (if still armed and inside the entry window) test the settled arm gates. The FIRST bar that passes
    /// opens ONE position and DISARMS the day (no re-arm). Appended AFTER the exit-advance, so an entry bar is
    /// never its own first exit-check.
    member this.Process (curBar: MinuteBar) =
        this.ProcessBar curBar
        for i in 0 .. positions.Count - 1 do
            let before = positions.[i].State
            let after = this.Advance curBar positions.[i]
            positions.[i] <- after
            match before, after.State with
            // a fresh STOP-exit is what MAKES the day re-arm-eligible: anchor the low at the stop (the next re-arm
            // needs a NEW low strictly below it) and record the stop bar (for the cooldown). MOC doesn't re-arm.
            | Holding, ExitedAt (m, _, "stop") -> sessLowAtExit <- sessEmaLow; stopExitMin <- m
            // an EXHAUSTION flush ends the day: revoke re-arm eligibility permanently (the blow-off climaxed).
            | Holding, ExitedAt (_, _, "exhaust") -> reArmEligible <- false
            | _ -> ()
        // RE-ARM trigger (requires an actual prior STOP-exit — sessLowAtExit is set ONLY by a stop). Conditions:
        // eligible (revoked only by exhaustion), disarmed, re-entries remain, cooldown elapsed since the stop, AND
        // the 9-EMA session-min has made a NEW low strictly below the stop level (a fresh pullback reset). On re-arm
        // clear the anchor so the NEXT re-arm needs its own stop. The gate scan + 3% stop-dist govern the re-entry.
        if reArmEligible && not armed && entryCount <= cfg.MaxReEntries then
            match sessEmaLow, sessLowAtExit with
            | ValueSome cur, ValueSome atExit
                    when cur < atExit && (curBar.etMin - stopExitMin) >= cfg.ReEntryCooldownBars ->
                armed <- true
                sessLowAtExit <- ValueNone      // consumed — the next re-arm needs a fresh stop-exit.
            | _ -> ()
        // (1) PATIENT limit entry: a limit rested last bar (at that bar's 9-EMA) is live for THIS bar only.
        // Fill if this bar traded down to it (low <= limit), at the limit price, stamped with the arm-bar
        // snapshot. Whether or not it fills, the one-bar limit is now consumed (cleared below).
        match pendingLimit, pendingArm with
        | ValueSome lim, ValueSome arm when armed ->
            if curBar.low <= lim then
                positions.Add { arm with EntryMin = curBar.etMin; EntryPx = lim; EntryType = "limit"
                                         StopDistPct = (if lim > 0.0 && not (Double.IsNaN arm.StopLevel) && arm.StopLevel > Double.NegativeInfinity
                                                        then (lim - arm.StopLevel) / lim else nan) }
                entryCount <- entryCount + 1
                armed <- false
        | _ -> ()
        pendingLimit <- ValueNone
        pendingArm <- ValueNone
        if armed then
            let inWindow =
                curBar.etMin >= cfg.EntryStartMin
                && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || curBar.etMin <= cfg.EntryEndMin)
            if inWindow then
                let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
                // this-bar-inclusive feature snapshots (the arm-gate inputs).
                let px = curBar.close
                let logAtr = vv atrLog.State
                let volSlope = vv volOls.Slope
                let chg1d = if close1d > 0.0 then px / close1d - 1.0 else nan
                let chg3d = if close3d > 0.0 then px / close3d - 1.0 else nan
                // the natural stop reference at THIS bar (the sess-ema-low, or the VWAP for BelowVwap mode).
                let stopRef =
                    match cfg.StopMode with
                    | BelowVwap       -> vwap.State
                    | BelowSessEmaLow -> sessEmaLow
                let natStop = vv stopRef
                // TIGHT-STOP FLOOR: when on, the stop is floored at ema_at_entry·(1−floor), so a session-min
                // within `floor` of the 9-EMA is replaced by that synthetic level (guaranteeing room) rather
                // than skipped. When off, `stop` = the raw session-min and the MinStopDistPct GATE applies.
                let stop =
                    if cfg.TightStopFloor > 0.0 then
                        match ema.State with
                        | ValueSome e ->
                            let floorLvl = e * (1.0 - cfg.TightStopFloor)
                            if Double.IsNaN natStop then floorLvl else max natStop floorLvl
                        | ValueNone -> natStop
                    else natStop
                let stopDistPct = if px > 0.0 && not (Double.IsNaN stop) then (px - stop) / px else nan
                // finite-value guards fold into each comparison (a nan feature fails its gate). The stop-dist
                // gate is SKIPPED in TightStopFloor mode (every setup trades with the floored stop).
                let dayStrengthOk =
                    chg1d >= cfg.MinChg1d
                    && chg3d >= cfg.MinChg3d && chg3d <= cfg.MaxChg3d
                    && logAtr >= cfg.MinLogAtr
                    && (cfg.TightStopFloor > 0.0 || stopDistPct >= cfg.MinStopDistPct)
                let timingOk =
                    (cfg.BlMax <= 0 || (barsSinceEmaLow >= 0 && barsSinceEmaLow < cfg.BlMax))
                    && (cfg.BhMin <= 0 || barsSinceEmaHigh >= cfg.BhMin)
                // vol_slope filter OFF when MinVolSlope = -infinity (the default). Otherwise a nan slope fails.
                let volSlopeOk =
                    Double.IsNegativeInfinity cfg.MinVolSlope
                    || (not (Double.IsNaN volSlope) && volSlope >= cfg.MinVolSlope)
                // the ARM condition. GATE: vol_slope ANDs in (keep scanning on fail). SKIP: everything but
                // vol_slope arms (the first such bar disarms); the position opens only if vol_slope also passed.
                // The EXHAUSTION latch blocks EVERYTHING: once the day has climaxed (blow-off high), no arm fires.
                let armCondition =
                    not exhausted && dayStrengthOk && timingOk && (not cfg.VolSlopeAsGate || volSlopeOk)
                if armCondition then
                    // the arm-bar feature snapshot (shared by market fill, immediate limit fill, and pending limit).
                    let emaLvl = vv ema.State
                    let snap =
                        { EntryMin = curBar.etMin
                          EntryPx = px
                          StopLevel = stop
                          StopDistPct = stopDistPct
                          LogAtr20 = logAtr
                          PriceSlope20 = vv priceOls.Slope
                          VolSlope20 = volSlope
                          EmaAtEntry = emaLvl
                          VwapAtEntry = vv vwap.State
                          SessEmaLowAtEntry = vv sessEmaLow
                          BarsSinceEmaHigh = barsSinceEmaHigh
                          BarsSinceEmaLow = barsSinceEmaLow
                          CumVolAtEntry = cumVol
                          EntryType = "close"
                          EntryIndex = entryCount            // 0 = first entry; 1/2 = re-entries (stamped at fill time).
                          State = Holding }
                    if not volSlopeOk then
                        // SKIP mode, vol_slope failed: burn the day, no position (matches market-mode behaviour).
                        armed <- false
                    elif not cfg.LimitEntry then
                        armed <- false                       // MARKET entry at the arm bar's close (default).
                        entryCount <- entryCount + 1
                        positions.Add snap
                    else
                        // PATIENT limit entry. If the close is already <= the 9-EMA, the limit would fill at once —
                        // enter at the close now. Otherwise rest a limit at the 9-EMA, good for the NEXT bar only;
                        // if unfilled the day stays armed and re-tests gates next bar.
                        match ema.State with
                        | ValueSome e when px <= e ->
                            armed <- false
                            entryCount <- entryCount + 1
                            positions.Add snap               // close already at/below the limit -> fill at close.
                        | ValueSome e ->
                            pendingLimit <- ValueSome e       // rest the limit at this bar's 9-EMA for the next bar.
                            pendingArm   <- ValueSome snap
                        | ValueNone ->
                            armed <- false                    // no 9-EMA yet (shouldn't happen post-warmup) -> at close.
                            entryCount <- entryCount + 1
                            positions.Add snap

    /// Flatten any still-open positions at the last folded bar's close (MOC / hold-to-close).
    member _.Flatten () =
        match bar with
        | ValueNone -> ()
        | ValueSome lb ->
            for i in 0 .. positions.Count - 1 do
                match positions.[i].State with
                | Holding -> positions.[i] <- { positions.[i] with State = ExitedAt (lb.etMin, lb.close, "moc") }
                | ExitedAt _ -> ()

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
