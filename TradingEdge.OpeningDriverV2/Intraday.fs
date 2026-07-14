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
      MinStopDistPct: float      // room to the stop: (entry-stop)/entry >= this (0.03)
      BlMax: int                 // bars-since-EMA-low freshness cap: bl < this (15). <=0 disables.
      BhMin: int                 // bars-since-EMA-high pullback floor: bh >= this (1). <=0 disables.
      MinVolSlope: float         // 20m OLS log-volume slope floor
      VolSlopeAsGate: bool }     // true = vol_slope ANDs into the arm (keep scanning on fail);
                                 // false (default) = skip filter (first arm bar disarms; no position if vol_slope fails)

/// Snapshot of the state going INTO the current bar (strictly-prior; the true-range
/// prior close). Everything else is read live post-fold (this-bar-inclusive).
type State =
    { bar : MinuteBar voption }

/// Per-(ticker, day) intraday engine. Feed the day's RTH MinuteBar[] in time order
/// via `Process`, then `Flatten` and read `Positions`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, close1d: float, close3d: float, permin20d: float) =
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

    let mutable bar : MinuteBar voption = ValueNone
    let mutable barsSeen = 0
    // the strictly-prior snapshot (only the prior bar is needed — for the true-range prior close).
    let mutable sState : State = { bar = ValueNone }
    // the day's arm state: once the engine fires (or a skip-filter burns the day) it DISARMS — no re-arm.
    let mutable armed = true

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
        // advance the prior-bar pointer for EVERY bar (so the ATR prior-close is the immediately-prior bar).
        bar      <- ValueSome curBar
        barsSeen <- barsSeen + 1
        sState   <- { bar = ValueSome curBar }

    /// Advance one open position by the current bar. Exit precedence: 9-EMA stop -> MOC. Both fill at close.
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
            positions.[i] <- this.Advance curBar positions.[i]
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
                // the stop reference at THIS bar (for the room gate + the frozen level if we fire).
                let stopRef =
                    match cfg.StopMode with
                    | BelowVwap       -> vwap.State
                    | BelowSessEmaLow -> sessEmaLow
                let stop = vv stopRef
                let stopDistPct = if px > 0.0 && not (Double.IsNaN stop) then (px - stop) / px else nan
                // finite-value guards fold into each comparison (a nan feature fails its gate).
                let dayStrengthOk =
                    chg1d >= cfg.MinChg1d
                    && chg3d >= cfg.MinChg3d && chg3d <= cfg.MaxChg3d
                    && logAtr >= cfg.MinLogAtr
                    && stopDistPct >= cfg.MinStopDistPct
                let timingOk =
                    (cfg.BlMax <= 0 || (barsSinceEmaLow >= 0 && barsSinceEmaLow < cfg.BlMax))
                    && (cfg.BhMin <= 0 || barsSinceEmaHigh >= cfg.BhMin)
                // vol_slope filter OFF when MinVolSlope = -infinity (the default). Otherwise a nan slope fails.
                let volSlopeOk =
                    Double.IsNegativeInfinity cfg.MinVolSlope
                    || (not (Double.IsNaN volSlope) && volSlope >= cfg.MinVolSlope)
                // the ARM condition. GATE: vol_slope ANDs in (keep scanning on fail). SKIP: everything but
                // vol_slope arms (the first such bar disarms); the position opens only if vol_slope also passed.
                let armCondition =
                    dayStrengthOk && timingOk && (not cfg.VolSlopeAsGate || volSlopeOk)
                if armCondition then
                    armed <- false                          // DISARM for the day (no re-arm) — gate or skip.
                    if volSlopeOk then                       // in GATE mode volSlopeOk is already implied here.
                        positions.Add
                            { EntryMin = curBar.etMin
                              EntryPx = px
                              StopLevel = stop
                              StopDistPct = stopDistPct
                              LogAtr20 = logAtr
                              PriceSlope20 = vv priceOls.Slope
                              VolSlope20 = volSlope
                              EmaAtEntry = vv ema.State
                              VwapAtEntry = vv vwap.State
                              SessEmaLowAtEntry = vv sessEmaLow
                              BarsSinceEmaHigh = barsSinceEmaHigh
                              BarsSinceEmaLow = barsSinceEmaLow
                              CumVolAtEntry = cumVol
                              State = Holding }
                    // else (SKIP mode, vol_slope failed): day is burned, no position opens.

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
