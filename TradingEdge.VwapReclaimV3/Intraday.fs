module TradingEdge.VwapReclaimV3.Intraday

open System
open TradingEdge.RollingMa

// =============================================================================
// VwapReclaimV3 — SMB-style intraday VWAP × 9-EMA reclaim scalp (LONG-ONLY).
//
// A debloated fork of VwapReclaim: the breakout engine and the ReclaimShort
// mirror are gone. The ONLY entry path is the long reclaim — go LONG when the
// fast 9-EMA crosses ABOVE the session VWAP after sustained weakness below it.
// One position may open per cross; all held to MOC (or the stop / time-stop).
//
// The stop is a 9-EMA PULLBACK-LOW stop (V3's whole point): the running MIN of
// the 9-EMA over the current below-VWAP run (reset at each reclaim), snapshotted
// at entry. It fires when the 9-EMA falls back BELOW that level — the reclaim
// failed and price is rolling back over. Replaces V1's geometric VWAP-low stop.
//
// Snapshot state is factored into a `State` record (DipRiderV4 style): ProcessBar
// builds `currentState` from the live indicators and assigns it to `sState` at the
// end, so every gate/entry reads strictly-prior values (no lookahead).
// =============================================================================

/// One 1-minute RTH bar, split-adjusted to the candidate's daily scale.
type MinuteBar =
    { etMin: int
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// Intraday position life-cycle. The reclaim opens a Holding position directly
/// (no arming); it transitions to ExitedAt on the stop / time-stop / MOC.
type IntraPosState =
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "stop"|"time_stop"|"moc"

/// One intraday trip. The *_at_entry metrics are the indicator snapshots at the
/// reclaim (cross) bar; EntryMin/EntryPx are the fill (the cross bar's close).
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      StopLevel: float         // the 9-EMA pullback-low stop level (run-min of the 9-EMA at entry). The
                               // position is stopped when the LIVE 9-EMA falls back below this level.
      VwapAtEntry: float       // session VWAP at the cross (the reclaim level; recorded)
      RunBelowVwapAtEntry: int // consecutive bars EMA<VWAP right before the cross (the weakness immediacy)
      StopDistPct: float       // stop distance as a fraction of entry = (entry - stopLevel)/entry (recorded)
      AtrPctAtEntry: float     // intraday log-ATR snapshot at the cross bar
      TightnessAtEntry: float  // intraday tightness snapshot at the cross bar
      CumVolAtEntry: int64     // cumulative day volume THROUGH the entry bar (rvol numerator; recorded)
      BreakoutBarVol: int64    // the cross bar's own volume (recorded)
      BelowVwapFrac: float     // fraction of pre-cross bars the EMA spent below VWAP (recorded)
      BreakoutBarOpen: float   // the cross bar's OPEN — for the 1m entry-bar %-change (close/open-1)
      PrevBarClose: float      // the strictly-prior 1m bar's CLOSE — for the 1m flush = close/prevClose-1
      Chg20mAtEntry: float     // 20-bar %-change into entry (entry close / close 20 bars ago - 1); nan if <20 bars
      Vol20mAvgAtEntry: float  // trailing 20-bar MEAN 1m volume at entry (incl. the cross bar); nan if <20 bars
      RunMaxDistAtEntry: float // max (VWAP-EMA)/VWAP over the pre-cross run — how DEEP the 9-EMA fell below VWAP
      RunAtrAtEntry: float     // mean per-bar log true range OVER the run bars (reset at each cross)
      RunUpVolAtEntry: float   // mean 1m volume of ABOVE-9EMA bars since the last VWAP cross (accumulation)
      RunDnVolAtEntry: float   // mean 1m volume of BELOW-9EMA bars since the last VWAP cross (distribution)
      State: IntraPosState }   // immutable — advancing a position returns a NEW record

/// Intraday reclaim engine config (long-only). The breakout/short/target knobs are gone.
type IntradayConfig =
    { VolWindow: int           // intraday ATR/tightness lookback in 1m BARS (default ~20)
      MaxTightness: float      // intraday tightness CEILING (per-bar; +inf = off)
      MaxAtrPct: float         // intraday ATR% CEILING (log-space, per-bar; +inf = off)
      SessionStartMin: int     // first ET minute fed to the engine (VWAP/EMA/session-low anchor; 09:30 = 570)
      EntryStartMin: int       // earliest ET minute an entry may fire (warms VWAP/EMA/run before it)
      EntryEndMin: int         // LATEST ET minute an entry may fire (0 / >=MocMin = no upper bound)
      TimeStopMin: int         // time-stop: flatten this many minutes after entry (0 = off; capped at MOC)
      MocMin: int              // MOC cutoff in ET minutes (960 = 16:00)
      MaxConcurrent: int       // cap on currently-OPEN positions; 0 = unlimited
      EmaPeriod: int           // the fast reclaim EMA period (SMB 9)
      BelowVwapFrac: float     // require the EMA below VWAP for > this FRACTION of pre-cross bars (0 = off)
      MinRunBelowVwap: int     // require >= this many CONSECUTIVE bars EMA<VWAP into the cross (0 = off)
      MinTightness: float      // MIN intraday tightness at entry (real range, not dead chop; 0 = off)
      StopBuffer: float        // pullback-stop BUFFER as a fraction: the stop fires when the 9-EMA falls
                               // below run-min * (1 - StopBuffer). 0 (default) = fire the instant the EMA
                               // dips under the run-low; a small buffer tolerates a 1-tick undershoot.
      StopOnClose: bool }      // true (default) = the stop triggers only when the 9-EMA reads below the
                               // level after the bar CLOSES (fills at that close). The 9-EMA is a close-
                               // based series, so this is the natural mode; the flag is kept for parity.

/// Snapshot of the engine state going INTO the current bar (strictly-prior — no
/// lookahead). ProcessBar builds a fresh `currentState` from the live indicators
/// after folding the bar, then assigns it to `sState`; the NEXT bar reads it.
type State =
    { bar          : MinuteBar voption   // the immediately-prior bar (true-range prior close, prev-bar refs)
      ema          : float voption       // the 9-EMA going into this bar
      vwap         : float voption       // session VWAP going into this bar
      atrLog       : float voption       // mean log-TR (VolWindow) going in
      atrLin       : float voption       // mean abs-TR (VolWindow) — the tightness denominator
      rangeHigh    : float voption       // VolWindow window high (tightness numerator)
      rangeLow     : float voption       // VolWindow window low  (tightness numerator)
      belowVwapFrac: float voption       // fraction of pre-cross bars with EMA<VWAP
      runBelowVwap : int }               // CONSECUTIVE bars EMA<VWAP going in

/// Per-(ticker, day) intraday engine. Feed the day's RTH MinuteBar[] in time
/// order via `Process`, then `Flatten` and read `Positions`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, prevClose: float) =

    // ----- rolling intraday structures (1m timeframe) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow bars
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (the tightness denominator)
    let lag20     = LagMa<float>(20)            // 20-bar close delay line -> the 20-minute %-change into entry
    let vol20     = AvgMa(20)                   // trailing 20-bar mean 1m volume ("volume during the convergence")
    let rangeHigh = MaxMa(cfg.VolWindow)        // tightness: window high
    let rangeLow  = MinMa(cfg.VolWindow)        // tightness: window low
    let ema       = EmaMa(cfg.EmaPeriod)        // the fast reclaim EMA (9)

    // session VWAP = Σ(typical·volume)/Σ(volume), typical = (h+l+c)/3, from SessionStartMin.
    let vwap = RatioMa()

    // ----- session-cumulative running extremes (RunMin/RunMax; from SessionStartMin) -----
    let sessLow  = RunMinMa<float>()            // running SESSION LOW (recorded run-depth reference)
    let sessHi   = RunMaxMa<float>()            // running SESSION HIGH (recorded)
    let volHi    = RunMaxMa<int64>()            // running max single-bar VOLUME (recorded)

    // ----- below-VWAP RUN accumulators (reset when the EMA reclaims VWAP; see resetRun) -----
    let mutable belowVwapBars = 0               // # bars EMA<VWAP from session start (the frac numerator)
    let mutable emaVwapBars   = 0               // # bars both EMA & VWAP defined (the frac denominator)
    let mutable runBelowVwap  = 0               // CONSECUTIVE bars EMA<VWAP up to now (resets on EMA>=VWAP)
    let mutable runMaxDist = 0.0                // max fractional (VWAP-EMA)/VWAP this run (the run's DEPTH)
    let mutable runAtrSum  = 0.0                // Σ per-bar log-TR over this run's bars
    let mutable runAtrN    = 0                  // # bars contributing to runAtrSum
    let mutable runUpVolSum = 0.0               // Σ volume of above-EMA (rising) bars this run
    let mutable runUpVolN   = 0
    let mutable runDnVolSum = 0.0               // Σ volume of below-EMA (falling) bars this run
    let mutable runDnVolN   = 0
    // the run-scoped 9-EMA pullback low: the MIN of the 9-EMA over the current below-VWAP run. It's the
    // stop level snapshotted at the reclaim; reset (with the other run accumulators) at each reclaim.
    let emaRunLow = RunMinMa<float>()

    // cumulative day volume from SessionStartMin (read post-fold at entry = cumVol THROUGH the entry bar).
    let mutable cumVol      : int64 = 0L
    let mutable sessionOpen : float voption = ValueNone   // first folded bar's open (cross-checks SQL day_open)
    let mutable lastBar     : MinuteBar voption = ValueNone
    let mutable barsSeen    = 0

    // the snapshot state going INTO the current bar (strictly-prior; no lookahead).
    let mutable sState : State =
        { bar = ValueNone
          ema = ValueNone
          vwap = ValueNone
          atrLog = ValueNone
          atrLin = ValueNone
          rangeHigh = ValueNone
          rangeLow = ValueNone
          belowVwapFrac = ValueNone
          runBelowVwap = 0 }

    let positions = ResizeArray<IntradayPosition>()

    member _.Ticker = ticker
    member _.Day = day
    member _.BarsSeen = barsSeen
    member _.SessionOpen = sessionOpen

    // ----- live reads (post-fold, incl. the current bar) -----
    /// The LIVE session VWAP (post-fold). ValueNone before any volume.
    member private _.LiveVwap : float voption = vwap.State
    /// The running SESSION LOW (post-fold) — recorded run-depth reference.
    member _.SessionLow = sessLow.State

    // ----- snapshot reads (strictly-prior; used by ShouldEnter) -----
    member _.AtrPct = sState.atrLog
    /// Intraday consolidation tightness = abs range / abs ATR (LINEAR).
    member _.Tightness =
        match sState.rangeHigh, sState.rangeLow, sState.atrLin with
        | ValueSome hi, ValueSome lo, ValueSome atr when atr <> 0.0 -> ValueSome ((hi - lo) / atr)
        | _ -> ValueNone
    member _.BelowVwapFrac = sState.belowVwapFrac
    member _.RunBelowVwap = sState.runBelowVwap

    /// Count of currently-open (Holding) positions.
    member _.OpenCount =
        let mutable n = 0
        for p in positions do (match p.State with Holding -> n <- n + 1 | ExitedAt _ -> ())
        n

    /// Gate 3 — the reclaim entry. Fires when the fast EMA crosses ABOVE the session
    /// VWAP on THIS bar (prior EMA <= prior VWAP; this-bar EMA > this-bar VWAP — both
    /// this-bar values incorporate this bar's own close, the fill point, so no lookahead),
    /// having spent enough of the pre-cross session below VWAP, in the trading window, with
    /// tightness/ATR% ok and concurrency room. Reads snapshots, so call AFTER ProcessBar.
    member this.ShouldEnter (bar: MinuteBar) : bool =
        let inline gate (v: _ voption) (test: _ -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // inside the wall-clock trading window [EntryStartMin, EntryEndMin].
        bar.etMin >= cfg.EntryStartMin
        && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || bar.etMin <= cfg.EntryEndMin)
        // the CROSS: EMA at/below VWAP going in, now strictly ABOVE (the reclaim).
        && (match sState.ema, sState.vwap, ema.State, this.LiveVwap with
            | ValueSome ep, ValueSome vp, ValueSome ec, ValueSome vc -> ep <= vp && ec > vc
            | _ -> false)
        // sustained weakness: > BelowVwapFrac of pre-cross bars below VWAP.
        && (cfg.BelowVwapFrac <= 0.0 || gate sState.belowVwapFrac (fun f -> f > cfg.BelowVwapFrac))
        // IMMEDIATE weakness: >= MinRunBelowVwap CONSECUTIVE bars below VWAP right before the cross.
        && (cfg.MinRunBelowVwap <= 0 || sState.runBelowVwap >= cfg.MinRunBelowVwap)
        // intraday tightness FLOOR (real range, not dead chop).
        && (cfg.MinTightness <= 0.0 || gate this.Tightness (fun t -> t >= cfg.MinTightness))
        // intraday tightness / ATR% CEILINGS (+inf disables).
        && (Double.IsInfinity cfg.MaxTightness || gate this.Tightness (fun t -> t < cfg.MaxTightness))
        && (Double.IsInfinity cfg.MaxAtrPct   || gate this.AtrPct    (fun a -> a < cfg.MaxAtrPct))
        // concurrency room.
        && (cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent)

    /// Live snapshot of the engine state BEFORE the current bar folds in — captured
    /// at the top of ProcessBar and stored into `sState` at the bottom, so `sState`
    /// always holds the STRICTLY-PRIOR state (excludes the current bar). The entry
    /// gate reads `sState.*` for prior values and the live objects for current ones.
    member private _.CaptureState () : State =
        { bar = lastBar
          ema = ema.State
          vwap = vwap.State
          atrLog = atrLog.State
          atrLin = atrLin.State
          rangeHigh = rangeHigh.State
          rangeLow = rangeLow.State
          belowVwapFrac = (if emaVwapBars > 0 then ValueSome (float belowVwapBars / float emaVwapBars) else ValueNone)
          runBelowVwap = runBelowVwap }

    /// Fold the current bar into the intraday indicators. The strictly-prior state
    /// is captured at the TOP (before any push) and stored into `sState` at the
    /// BOTTOM, so `sState` excludes the current bar — the entry gate's cross compares
    /// `sState.ema/vwap` (prior) with the live post-fold `ema.State`/VWAP (current).
    member this.ProcessBar (bar: MinuteBar) =
        let priorState = this.CaptureState ()
        // 1) fold the bar in. True range vs the prior 1m close.
        let mutable barLogTr = nan               // this bar's log true range (for the run-ATR accumulator)
        match lastBar with
        | ValueSome prev when bar.high > 0.0 && bar.low > 0.0 && prev.close > 0.0 ->
            let pc = prev.close
            (max bar.high pc - min bar.low pc) |> atrLin.Push
            barLogTr <- log (max bar.high pc / min bar.low pc)
            barLogTr |> atrLog.Push
        | _ -> ()

        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        sessLow.Push bar.low
        sessHi.Push  bar.high
        volHi.Push   bar.volume
        cumVol <- cumVol + bar.volume
        lag20.Push bar.close
        vol20.Push (float bar.volume)
        if sessionOpen.IsNone then sessionOpen <- ValueSome bar.``open``

        // below-VWAP RUN tally: read the PRIOR EMA & VWAP (captured at the top, strictly-prior) BEFORE
        // folding this bar's VWAP/EMA. The run accumulators track the current below-VWAP streak (reset when
        // the EMA reclaims VWAP), including the run-scoped 9-EMA low that backs the pullback stop.
        match priorState.ema, priorState.vwap with
        | ValueSome e, ValueSome v ->
            emaVwapBars <- emaVwapBars + 1
            if e < v then
                // on the below-VWAP run: deepen the depth, accumulate the run's ATR, extend the EMA-run-low.
                let dist = if v > 0.0 then (v - e) / v else 0.0
                if dist > runMaxDist then runMaxDist <- dist
                if not (Double.IsNaN barLogTr) then
                    runAtrSum <- runAtrSum + barLogTr
                    runAtrN   <- runAtrN + 1
                emaRunLow.Push e                 // the 9-EMA pullback low over this below-VWAP run
            // Up/Down volume split over the whole cross-to-cross regime (bars oscillate around the
            // declining/converging EMA within the run). CLOSE vs the strictly-prior 9-EMA `e`.
            if bar.close > e then
                runUpVolSum <- runUpVolSum + float bar.volume
                runUpVolN   <- runUpVolN + 1
            elif bar.close < e then
                runDnVolSum <- runDnVolSum + float bar.volume
                runDnVolN   <- runDnVolN + 1
            let resetRun () =
                runMaxDist <- 0.0; runAtrSum <- 0.0; runAtrN <- 0
                runUpVolSum <- 0.0; runUpVolN <- 0; runDnVolSum <- 0.0; runDnVolN <- 0
                emaRunLow.Reset ()               // the pullback-low resets with the run at each reclaim
            if e < v then
                belowVwapBars <- belowVwapBars + 1
                runBelowVwap  <- runBelowVwap + 1
            elif e > v then
                runBelowVwap  <- 0
                resetRun ()                       // the below-run just broke (EMA reclaimed VWAP)
            else
                runBelowVwap  <- 0
                resetRun ()
        | _ -> ()

        // session VWAP + 9-EMA fold in.
        let tp = (bar.high + bar.low + bar.close) / 3.0
        vwap.Push (tp * float bar.volume, float bar.volume)
        ema.Push bar.close

        lastBar  <- ValueSome bar
        barsSeen <- barsSeen + 1

        // 2) store the strictly-prior snapshot (captured at the top) for the entry gate. `sState` thus
        // holds the state EXCLUDING the current bar; the gate's cross reads it against the live objects.
        sState <- priorState

    /// Advance one open position by the current bar. Exit precedence:
    ///   1. 9-EMA pullback stop — the LIVE 9-EMA falls below the snapshotted run-low
    ///      (times 1-StopBuffer). Fills at this bar's close (the 9-EMA is a close-based
    ///      series so the level is only knowable post-close).
    ///   2. time-stop — TimeStopMin minutes after entry, fills at close.
    ///   3. MOC — at/after the cutoff, flatten at close.
    member _.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            let stopLevel = pos.StopLevel * (1.0 - cfg.StopBuffer)
            let stopHit =
                match ema.State with
                | ValueSome e -> e < stopLevel
                | ValueNone   -> false
            if stopHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "stop") }
            elif cfg.TimeStopMin > 0 && bar.etMin >= pos.EntryMin + cfg.TimeStopMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "time_stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar: fold the bar in, advance every
    /// open position, then (if the reclaim fires) open a new position. The new
    /// position is appended AFTER the existing positions advance, so an entry bar
    /// is never its own first exit-check.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar positions.[i]
        if this.ShouldEnter bar then
            // the reclaim required the cross => LiveVwap is ValueSome. The stop is the run-scoped
            // 9-EMA pullback low; if the run is somehow empty (defensive), fall back to the session low.
            let vwapNow = this.LiveVwap.Value
            let stop =
                match emaRunLow.State with
                | ValueSome lo -> lo
                | ValueNone    -> (match sessLow.State with ValueSome l -> l | ValueNone -> bar.low)
            let stopDistPct = if bar.close > 0.0 then abs (bar.close - stop) / bar.close else nan
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  StopLevel = stop
                  VwapAtEntry = vwapNow
                  RunBelowVwapAtEntry = sState.runBelowVwap
                  StopDistPct = stopDistPct
                  AtrPctAtEntry = (match this.AtrPct with ValueSome a -> a | ValueNone -> nan)
                  TightnessAtEntry = (match this.Tightness with ValueSome t -> t | ValueNone -> nan)
                  CumVolAtEntry = cumVol
                  BreakoutBarVol = bar.volume
                  BelowVwapFrac = (match sState.belowVwapFrac with ValueSome f -> f | ValueNone -> nan)
                  BreakoutBarOpen = bar.``open``
                  PrevBarClose = (match sState.bar with ValueSome p -> p.close | ValueNone -> nan)
                  Chg20mAtEntry = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                  Vol20mAvgAtEntry = (if vol20.Count >= 20 then (match vol20.State with ValueSome v -> v | ValueNone -> nan) else nan)
                  RunMaxDistAtEntry = runMaxDist
                  RunAtrAtEntry = (if runAtrN > 0 then runAtrSum / float runAtrN else nan)
                  RunUpVolAtEntry = (if runUpVolN > 0 then runUpVolSum / float runUpVolN else nan)
                  RunDnVolAtEntry = (if runDnVolN > 0 then runDnVolSum / float runDnVolN else nan)
                  State = Holding }

    /// Flatten any still-open positions at the last folded bar's close (MOC).
    member _.Flatten () =
        match lastBar with
        | ValueNone -> ()
        | ValueSome lb ->
            for i in 0 .. positions.Count - 1 do
                match positions.[i].State with
                | Holding -> positions.[i] <- { positions.[i] with State = ExitedAt (lb.etMin, lb.close, "moc") }
                | ExitedAt _ -> ()

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
