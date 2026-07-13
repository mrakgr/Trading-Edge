module TradingEdge.OpeningDriver.Intraday

open System
open TradingEdge.RollingMa

// =============================================================================
// OpeningDriver — an OPENING-DRIVE study (long-only).
//
// Forked from DipRiderV4 for its clean feature-folding, but the arm/re-arm state
// machine and the breakout timers are GONE. This engine is a pure recorder: for
// each candidate day it folds the intraday indicators, and for EVERY bar in the
// entry window [EntryStartMin, EntryEndMin] it opens a Holding position at that
// bar's close (feature snapshot attached), held to MOC or a 9-EMA stop. There are
// NO entry gates in the engine — all feature/gate analysis is done POST-HOC on the
// emitted CSV. (One position per window bar; unlimited concurrency by design.)
//
// The 9-EMA stop fires when the live 9-EMA drops below a chosen reference:
//   BelowVwap       — the live session VWAP (dynamic).
//   BelowSessEmaLow — the 9-EMA session-min, FROZEN at entry (static level).
//
// Recorded features (the study's levers): 20m log-ATR, 20m OLS log-price slope,
// 20m OLS log-volume slope, chg_1d, chg_3d, and cumulative session volume vs the
// 20d average (rvol_cum). Snapshots follow the DipRiderV4 fold-then-snapshot rule:
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
      CumVolAtEntry: int64       // cumulative session volume through the entry bar (rvol_cum numerator)
      State: IntraPosState }

/// OpeningDriver engine config. No entry gates (post-hoc study); the only knobs are
/// timing, the ATR/slope windows, the notional, and the stop mode.
type IntradayConfig =
    { VolWindow: int             // trailing ATR / OLS-slope lookback in 1m BARS (default 20)
      EmaPeriod: int             // the 9-EMA period
      SessionStartMin: int       // first ET minute fed to the engine (session anchor; extremes warm from here)
      FeatureStartMin: int       // ET minute the features start folding (09:30 = the RTH open, no premarket)
      EntryStartMin: int         // earliest ET minute a window entry fires (09:30 + 15 = 09:45)
      EntryEndMin: int           // latest ET minute a window entry fires (09:30 + 60 = 10:30)
      MocMin: int                // MOC cutoff in ET minutes (960 = 16:00)
      StopMode: StopMode }       // 9-EMA stop reference: BelowVwap | BelowSessEmaLow

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
    // cumulative session volume from the feature anchor (the rvol_cum numerator; read post-fold at entry).
    let mutable cumVol : int64 = 0L

    let mutable bar : MinuteBar voption = ValueNone
    let mutable barsSeen = 0
    // the strictly-prior snapshot (only the prior bar is needed — for the true-range prior close).
    let mutable sState : State = { bar = ValueNone }

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
            // the 9-EMA, then its session-min (ratchets down only) — the BelowSessEmaLow stop reference.
            ema.Push curBar.close
            ema.State |> ValueOption.iter (fun e ->
                sessEmaLow <- match sessEmaLow with ValueSome m -> ValueSome (min m e) | ValueNone -> ValueSome e)
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

    /// Advance the whole system by one minute bar: fold the bar in, advance every open position, then
    /// (if this bar is inside the entry window) open ONE new position at its close. Appended AFTER the
    /// existing positions advance, so an entry bar is never its own first exit-check.
    member this.Process (curBar: MinuteBar) =
        this.ProcessBar curBar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance curBar positions.[i]
        let inWindow =
            curBar.etMin >= cfg.EntryStartMin
            && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || curBar.etMin <= cfg.EntryEndMin)
        if inWindow then
            let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
            // the stop level at entry: BelowSessEmaLow freezes the current 9-EMA session-min; BelowVwap
            // records the current VWAP (the live VWAP drives the exit). Recorded either way for post-hoc.
            let stopRef =
                match cfg.StopMode with
                | BelowVwap       -> vwap.State
                | BelowSessEmaLow -> sessEmaLow
            let stop = vv stopRef
            positions.Add
                { EntryMin = curBar.etMin
                  EntryPx = curBar.close
                  StopLevel = stop
                  StopDistPct = (if curBar.close > 0.0 && not (Double.IsNaN stop) then (curBar.close - stop) / curBar.close else nan)
                  LogAtr20 = vv atrLog.State
                  PriceSlope20 = vv priceOls.Slope
                  VolSlope20 = vv volOls.Slope
                  EmaAtEntry = vv ema.State
                  VwapAtEntry = vv vwap.State
                  SessEmaLowAtEntry = vv sessEmaLow
                  CumVolAtEntry = cumVol
                  State = Holding }

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
