module TradingEdge.LowFlyer.Intraday

open System
open TradingEdge.RollingMa

// =============================================================================
// MaxFlyer INTRADAY layer — Gate 3 (the trading engine).
//
// A separate per-(ticker, day) engine that mirrors the daily QullaSystem but
// folds 1-MINUTE bars instead of daily bars. The daily system SELECTS the day;
// this system TRADES it independently. Same snapshot-before-push (no-lookahead)
// discipline, same RollingMa structures, but with MULTIPLE concurrent open
// positions: every fresh new-session-high breakout (with the intraday gates
// satisfied) opens its own independent position, all held to MOC (or an
// optional intraday-low stop).
// =============================================================================

/// One 1-minute RTH bar, already converted to ET minutes-since-midnight and
/// split-adjusted to the candidate's daily scale.
type MinuteBar =
    { etMin: int
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// Intraday position life-cycle. The breakout may first ARM a position (no fill); it
/// transitions to Holding once its entry condition is met:
///   --trail-entry — a bar closes back through the ratcheting trail (rollover off the top).
///   --rise-entry  — price first runs a further `RiseEntry` fraction from the arm price
///                   (short into a now-parabolic move), then either fills immediately or,
///                   if --trail-entry is also on, waits for the rollover after that.
/// With neither flag the breakout creates a Holding position directly (Armed is unused).
type IntraPosState =
    // armMin = arming bar; trailLevel = ratcheting 2-bar extreme; armPrice = breakout-bar
    // price (the --rise-entry anchor); riseCleared = has the +RiseEntry move happened yet.
    | Armed of armMin: int * trailLevel: float * armPrice: float * riseCleared: bool
    | Skipped                                                     // armed but a gate refused the fill → no trip
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "moc"|"intraday_stop"|"session_stop"|"pct_stop"|"target"|"time_stop"

/// One intraday trip. `EntryMin`/`EntryPx` are the FILL values (the breakout bar, or
/// the rollover bar under trail-entry); the *_at_entry metrics are the intraday
/// indicator snapshots at the ARMING (breakout) bar.
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      StopLevel: float         // protective-stop level. Direct entry: the 2-bar local extreme at the
                               // breakout (upside = 2-bar low, downside = 2-bar high); fires when price
                               // reaches it (low<=level / high>=level). Under --trail-entry this is the
                               // SESSION extreme at fill instead (the high for a short, the low for a long):
                               // we're stopped out if price runs back to a fresh session extreme.
      BreakoutRef: float       // the running session extreme (strictly-prior bars) the breakout cleared —
                               // the session high for an upside breakout, the session low for a downside one
      AtrPctAtEntry: float     // intraday log-ATR snapshot at the arming bar
      TightnessAtEntry: float  // intraday tightness snapshot at the arming bar
      CumVolAtEntry: int64     // cumulative day volume THROUGH the entry bar (rvol numerator; recorded feature)
      BreakoutBarVol: int64    // the entry (breakout) bar's own volume (recorded feature)
      NewVolHigh: bool         // did the entry bar make a NEW session 1m-volume high (vs strictly-prior bars)?
                               // Always true at VolHighFrac>=1.0 (the gate enforces it); the informative case
                               // is a relaxed/off gate, where this flags which entries would pass the strict gate.
      VolVsHigh: float         // breakout-bar volume / running session 1m-vol high (pre-push). The CONTINUOUS
                               // version of NewVolHigh: >=1.0 = a new high; 0.8 = 80% of the high. Lets a
                               // relaxed run (--vol-high-frac 0.8) be sliced post-hoc to find the knee. (runVolHi
                               // is not in the CSV otherwise, so this must be recorded here.)
      BreakoutBarOpen: float   // the entry (breakout) bar's OPEN — for the 1m entry-bar %-change (close/open-1)
      PrevBarClose: float      // the strictly-prior 1m bar's CLOSE — for the 1m flush = close/prevClose-1
      Chg20mAtEntry: float     // 20-bar (20-minute) %-change into entry (entry close / close 20 bars ago - 1); nan if <20 bars
      State: IntraPosState }   // immutable — advancing a position returns a NEW record (HighFlyer style)

/// Mean-reversion TARGET — where a (short) position covers when price reverts back
/// to the anchor. For a short, "reverted" = price falls to/through the anchor, so
/// the target doubles as the loss-cut: if price never comes back, the trade rides to
/// MOC (the loss case). This retires the protective stop for the mean-reversion book.
type Target =
    | NoTarget                 // hold to MOC / time-stop only
    | Vwap                     // session VWAP (running, bar-typical-price weighted, from SessionStartMin)
    | Ma of window: int        // fast simple moving average of closes over `window` 1m bars
    | Channel of window: int   // Donchian low: min low over the prior `window` 1m bars (pre-breakout range floor)

/// Intraday Gate-3 + engine config.
type IntradayConfig =
    { VolWindow: int           // intraday ATR/tightness lookback in 1m BARS (swept; default ~20)
      MaxTightness: float      // intraday tightness gate (per-bar)
      MaxAtrPct: float         // intraday ATR% gate (log-space, per-bar)
      SessionStartMin: int     // first ET minute fed to the engine (510 = 08:30 ET — the SMB "1h
                               // opening range"; the running high/low/volume accumulate from here).
      EntryStartMin: int       // earliest ET minute an entry may fire (575 = 09:35). The engine
                               // PROCESSES premarket bars (to warm the running extremes) but does not
                               // TRADE before this wall-clock floor.
      UseStop: bool            // arm the intraday-low protective stop
      PctStop: float           // wide catastrophe %-stop: a fixed adverse excursion from entry (0 = off;
                               // e.g. 0.5 = stop if price moves 50% AGAINST the position). Independent of
                               // UseStop — meant to clip the run-over tail (a short that doubles on you)
                               // while leaving normal noise untouched. Short: stop on bar.high >= entry*(1+x);
                               // long: bar.low <= entry*(1-x). Fills at the level, gap-through at the open.
      TimeStopMin: int         // time-stop: flatten this many minutes after entry (capped at MOC);
                               // 0 = off. Side-independent. Fires before the protective stop / MOC
                               // only if it lands earlier.
      Downside: bool           // breakout DIRECTION. false (default) = upside breakout to a new session
                               // HIGH; true = downside breakout to a new session LOW. Independent of
                               // Short: Downside chooses the signal, Short chooses the P&L sign. The
                               // protective stop flips with it (2-bar low for upside, 2-bar high for down).
      WickBreakout: bool       // breakout TRIGGER style. false (default) = CLOSE through the prior
                               // session extreme; true = the bar's HIGH/LOW WICK merely pierces it (even
                               // if it closes back inside). Wick fires more/earlier; admits weaker pierces.
      ExtGate: float           // CONDITIONAL day-extension entry gate (0 = off; 0.5 = 50% vs prev close).
                               // At the breakout: if day-extension >= ExtGate already → enter DIRECT (the
                               // name is parabolic now). If < ExtGate → arm a ROLLOVER (trail-entry style)
                               // and take it ONLY if day-extension >= ExtGate by the time it triggers;
                               // otherwise skip (no trade). Short side; uses prevClose. Distinct from
                               // RiseEntry (which measures % from the breakout PRICE, not the day).
      RiseEntry: float         // entry GATE: require price to first run this fraction past the arm
                               // (breakout) price before a short may enter — i.e. wait for a further
                               // parabolic move and short THAT, not the breakout (0 = off; 0.5 = +50%).
                               // Short anchors above (armPrice*(1+x)), long below (armPrice*(1-x)). Arms
                               // a position like trail-entry; with --trail-entry it ALSO waits for the
                               // rollover after the rise, otherwise it fills immediately at the level.
      TrailEntry: bool         // entry MODEL. false (default) = enter immediately on the breakout bar.
                               // true = the breakout only ARMS; track a trailing 2-bar extreme that
                               // ratchets WITH the move (up for a short / down for a long), and enter only
                               // when a bar CLOSES back through it (the rollover off the top/bottom). The
                               // protective stop then sits at the SESSION extreme at fill. Opt-in.
      Short: bool              // trade the breakout SHORT (fade) instead of long. Flips the P&L sign
                               // only; the breakout entry signal is unchanged. The intraday-low
                               // protective stop is wrong-sided for a short, so leave UseStop off when
                               // Short (the first short experiments run stopless + time-stopped).
      Target: Target           // mean-reversion cover target (NoTarget by default). For a short, the
                               // position covers when the bar's low reaches the (snapshotted, strictly-
                               // prior) anchor; doubles as the loss-cut (no separate stop needed).
      MocMin: int              // MOC cutoff in ET minutes (960 = 16:00)
      MaxConcurrent: int       // cap on currently-OPEN (Holding) positions; 0 = unlimited
      MinBarFlush: float       // entry-bar FLUSH gate: require close/prevClose-1 <= this (a real flush
                               // candle, not a one-tick poke below the reference). 0.0 (default) = OFF.
                               // e.g. -0.007 rejects bars whose 1m move is softer than -0.7%. Uses the
                               // strictly-prior bar's close (sLastBar); ValueNone prior bar => not gated.
      MinBarFlushFloor: float  // entry-bar flush DEPTH floor (the falling-knife cut, Run 26): reject the
                               // breakout bar if its 1m move is DEEPER than this. 0.0 (default) = OFF.
                               // Long/Downside: require close/prevClose-1 >= MinBarFlushFloor (e.g. >= -0.12
                               // rejects flushes deeper than -12%). Upside/short: mirror (<= -MinBarFlushFloor).
                               // Pairs with MinBarFlush to make an entry-bar move BAND [floor, ceiling].
      MinCloseRef: bool        // breakout REFERENCE level. false (default) = the running min-LOW
                               // (max-HIGH upside) of strictly-prior bars. true = the running min-
                               // CLOSE (max-CLOSE upside) — closes-only, so a long lower/upper WICK
                               // can't push the channel boundary. The TRIGGER stays close-based;
                               // this only changes what level the close must clear.
      VolHighFrac: float }     // VOLUME-CONFIRMATION gate as a FRACTION of the running session max 1m-bar
                               // volume: require bar.volume >= VolHighFrac * runVolHi. 1.0 (default) = the
                               // original "must EXCEED the vol high" gate (a new-vol-high bar). 0.95 / 0.90
                               // relax it to "within 5% / 10% of the high" — probes whether the edge is in
                               // MAKING a new vol high or merely being NEAR elevated volume, recovering trips.
                               // 0.0 = OFF (fire on the first new-session-extreme bar regardless of volume).
                               // NOTE: below 1.0 the `new_vol_high` column still records the STRICT test
                               // (bar > runVolHi), so a relaxed run can still be split on true-new-high vs not.

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Finalize` and read `Trips()`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, prevClose: float) =

    // day extension at a bar = close vs the prior daily close (how far up/down on the day).
    // Used by --ext-gate to route direct-vs-rollover entry and gate the rollover fill.
    let extension (px: float) = if prevClose > 0.0 then px / prevClose - 1.0 else 0.0

    // ----- rolling intraday structures (1m timeframe) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow 1m bars
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear)
    let lag20     = LagMa<float>(20)            // 20-bar close delay line -> the 20-minute %-change into entry
    let rangeHigh = MaxMa(cfg.VolWindow)        // intraday tightness: max high over the window
    let rangeLow  = MinMa(cfg.VolWindow)        // intraday tightness: min low over the window

    // ----- mean-reversion TARGET anchors (only the one named by cfg.Target is fed) -----
    // session VWAP = Σ(typical_price · volume) / Σ(volume), typical = (h+l+c)/3, from
    // SessionStartMin. Bar-based (the 1m parquet carries no trade-exact VWAP) — adequate
    // for a 1m mean-reversion anchor.
    let mutable vwapNum  = 0.0                   // Σ tp·v
    let mutable vwapDen  = 0.0                   // Σ v
    let maTgt      = match cfg.Target with Ma w -> AvgMa(w)      | _ -> AvgMa(1)   // fast SMA of closes
    let chanTgt    = match cfg.Target with Channel w -> MinMa(w) | _ -> MinMa(1)   // Donchian low (pre-breakout floor)

    // session-cumulative running extremes over ALL bars seen so far (NOT windowed),
    // accumulating from SessionStartMin (08:30 ET). The breakout reference is the high
    // of all strictly-prior bars; runVolHi is the max single-bar VOLUME so far — the
    // volume-confirmation reference (a real breakout re-takes the session volume high,
    // which normally only happens at the open/close).
    let mutable runHi    : float voption = ValueNone
    let mutable runLo    : float voption = ValueNone
    // parallel CLOSE-based running extremes (min/max over strictly-prior CLOSES), used
    // as the breakout reference when cfg.MinCloseRef is on — closes-only, wick-immune.
    let mutable runHiClose : float voption = ValueNone
    let mutable runLoClose : float voption = ValueNone
    let mutable runVolHi : int64 voption = ValueNone
    // cumulative day volume from SessionStartMin (09:30). Read (post-fold) when a
    // position is created it is the cumulative volume THROUGH the entry bar (incl. it)
    // — the rvol numerator (a recorded feature, not a gate).
    let mutable cumVol      : int64 = 0L
    // the day's session open = the FIRST folded bar's open (09:30). Cross-checks the
    // SQL day_open (they must match); exposed for an optional engine-side %-since-open.
    let mutable sessionOpen : float voption = ValueNone
    // the most recent bar folded. Read at the TOP of ProcessBar (before the current
    // bar is pushed) it IS the immediately-prior bar — the source of both the true-range
    // prior close and the 2-bar-stop prior low; read by Flatten it's the day's last bar.
    let mutable lastBar  : MinuteBar voption = ValueNone
    let mutable barsSeen  = 0

    // ----- pre-push snapshots (state going INTO the current bar; no lookahead) -----
    let mutable sRunHi      : float voption = ValueNone
    let mutable sRunLo      : float voption = ValueNone
    let mutable sRunHiClose : float voption = ValueNone
    let mutable sRunLoClose : float voption = ValueNone
    let mutable sRunVolHi   : int64 voption = ValueNone
    let mutable sLastBar   : MinuteBar voption = ValueNone   // the prior bar, as-of going into this bar (its low feeds the 2-bar stop)
    let mutable sAtrLog    : float voption = ValueNone
    let mutable sAtrLin    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone
    // the mean-reversion cover anchor (cfg.Target), as-of going INTO this bar (strictly-
    // prior). ValueNone until the chosen anchor is warm / when Target = NoTarget.
    let mutable sTargetAnchor : float voption = ValueNone

    let positions = ResizeArray<IntradayPosition>()

    member _.Ticker = ticker
    member _.Day = day
    member _.BarsSeen = barsSeen
    /// The session open = the first folded bar's open (09:30). Cross-checks the SQL day_open.
    member _.SessionOpen = sessionOpen

    // ----- read members (mirror the daily QullaSystem) -----
    /// The strictly-prior session extreme the breakout must clear — the running high for
    /// an upside breakout, the running low for a downside one. cfg.MinCloseRef selects
    /// the CLOSE-based extreme (wick-immune) instead of the low/high (wick) extreme.
    member _.BreakoutRef =
        match cfg.Downside, cfg.MinCloseRef with
        | true,  false -> sRunLo
        | true,  true  -> sRunLoClose
        | false, false -> sRunHi
        | false, true  -> sRunHiClose
    /// Running max single-bar volume over strictly-prior bars (the volume-confirmation reference).
    member _.RunVolHigh = sRunVolHi
    member _.AtrLog = sAtrLog
    /// ATR% stand-in = the intraday log-ATR directly.
    member _.AtrPct = sAtrLog
    member _.RangeAbs =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo -> ValueSome (hi - lo)
        | _ -> ValueNone
    /// Intraday consolidation tightness = abs range / abs ATR (LINEAR; log mode removed).
    member this.Tightness =
        match this.RangeAbs, sAtrLin with
        | ValueSome r, ValueSome atr when atr <> 0.0 -> ValueSome (r / atr)
        | _ -> ValueNone

    /// The mean-reversion cover anchor (cfg.Target), as-of going INTO the current bar.
    member _.TargetAnchor = sTargetAnchor

    /// The LIVE target anchor (post-fold) for the chosen cfg.Target — used by ProcessBar
    /// to snapshot the strictly-prior value. ValueNone for NoTarget / before warm.
    member private _.LiveTargetAnchor : float voption =
        match cfg.Target with
        | NoTarget -> ValueNone
        | Vwap -> if vwapDen > 0.0 then ValueSome (vwapNum / vwapDen) else ValueNone
        | Ma _ -> maTgt.State
        | Channel _ -> chanTgt.State

    /// Count of currently-open positions — Holding, plus Armed (a committed pending
    /// trail-entry signal counts against concurrency too).
    member _.OpenCount =
        let mutable n = 0
        for p in positions do (match p.State with Holding | Armed _ -> n <- n + 1 | ExitedAt _ | Skipped -> ())
        n

    /// Gate 3 — does THIS bar trigger a new-high breakout entry? Reads pre-push
    /// snapshots, so it must be evaluated AFTER ProcessBar(bar). A new SESSION high
    /// (close above the running high of strictly-prior bars from 08:30) that ALSO
    /// re-takes the session 1m-volume high, with the intraday tightness/ATR% gates
    /// satisfied, past the 09:35 trading floor, and concurrency room.
    member this.ShouldEnter (bar: MinuteBar) : bool =
        let inline gate (v: _ voption) (test: _ -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // past the wall-clock trading floor (09:35). The engine processes premarket
        // bars from 08:30 to warm the running extremes, but does not TRADE before this.
        bar.etMin >= cfg.EntryStartMin
        // new session extreme vs strictly-prior bars (08:30-onward running high/low).
        // Two trigger styles (cfg.WickBreakout):
        //   false (default) — CLOSE breakout: the bar must CLOSE through the prior extreme
        //                     (close > runHi upside / close < runLo downside).
        //   true            — WICK breakout: the bar's HIGH (upside) / LOW (downside) merely
        //                     PIERCES the prior extreme, even if it closes back inside.
        // The wick trigger fires more often (and earlier) and admits the weaker pierce-but-
        // close-inside breakouts.
        && gate this.BreakoutRef (fun ext ->
            match cfg.Downside, cfg.WickBreakout with
            | false, false -> bar.close > ext
            | false, true  -> bar.high  > ext
            | true,  false -> bar.close < ext
            | true,  true  -> bar.low   < ext)
        // VOLUME CONFIRMATION (cfg.VolHighFrac): the breakout bar's volume vs the strictly-prior
        // session 1m-volume high. 1.0 (default) = must EXCEED it (a new-vol-high bar) — most
        // breakouts never re-take the vol high, so a morning bar that does is a significant event.
        // <1.0 relaxes to "within (1-frac) of the high" (0.95/0.90); 0.0 = OFF (--no-vol-high).
        && (cfg.VolHighFrac <= 0.0
            || gate sRunVolHi (fun vh ->
                   if cfg.VolHighFrac >= 1.0 then float bar.volume > float vh          // strict >, original behavior
                   else float bar.volume >= cfg.VolHighFrac * float vh))
        // ENTRY-BAR FLUSH gate (cfg.MinBarFlush, 0 = off): the breakout bar's own 1m move
        // = close/prevClose-1 must be a real flush, not a one-tick poke below the reference.
        // Downside/long: require the move <= MinBarFlush (e.g. <= -0.7%). Upside: mirror
        // (>= -MinBarFlush). ValueNone prior close (only the session-open bar) => not gated,
        // but entries can't fire that early anyway.
        && (cfg.MinBarFlush = 0.0
            || gate (sLastBar |> ValueOption.map (fun p -> p.close)) (fun pc ->
                   pc > 0.0 &&
                   let mv = bar.close / pc - 1.0
                   if cfg.Downside then mv <= cfg.MinBarFlush else mv >= -cfg.MinBarFlush))
        // ENTRY-BAR FLUSH-DEPTH floor (cfg.MinBarFlushFloor, 0 = off): reject a flush DEEPER than
        // the floor (the Run 26 falling-knife cut). Long/Downside: mv >= floor (e.g. >= -0.12).
        // Upside/short: mirror (mv <= -floor). Together with MinBarFlush this bands the entry move.
        && (cfg.MinBarFlushFloor = 0.0
            || gate (sLastBar |> ValueOption.map (fun p -> p.close)) (fun pc ->
                   pc > 0.0 &&
                   let mv = bar.close / pc - 1.0
                   if cfg.Downside then mv >= cfg.MinBarFlushFloor else mv <= -cfg.MinBarFlushFloor))
        // intraday tightness gate
        && gate this.Tightness (fun t -> t < cfg.MaxTightness)
        // intraday ATR% gate (log-space)
        && gate this.AtrPct (fun a -> a < cfg.MaxAtrPct)
        // concurrency room (count only currently-OPEN positions)
        && (cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent)

    /// Fold the current bar into the intraday indicators. Snapshots taken BEFORE
    /// the push (prior-bars / no-lookahead), then the bar is folded in.
    member this.ProcessBar (bar: MinuteBar) =
        // `lastBar` still holds the immediately-prior bar here (it's updated at the
        // very end), so it's the source of both the prior low and the prior close.
        // 1) snapshot pre-bar state
        sRunHi        <- runHi
        sRunLo        <- runLo
        sRunHiClose   <- runHiClose
        sRunLoClose   <- runLoClose
        sRunVolHi     <- runVolHi
        sLastBar      <- lastBar
        sAtrLog       <- atrLog.State
        sAtrLin       <- atrLin.State
        sRangeHigh    <- rangeHigh.State
        sRangeLow     <- rangeLow.State
        sTargetAnchor <- this.LiveTargetAnchor

        // 2) fold the bar in. True range vs the prior 1m close.
        match lastBar with
        | ValueSome prev when bar.high > 0.0 && bar.low > 0.0 && prev.close > 0.0 ->
            let pc = prev.close
            (max bar.high pc - min bar.low pc) |> atrLin.Push
            log (max bar.high pc / min bar.low pc) |> atrLog.Push
        | _ -> ()

        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        runHi    <- match runHi with ValueSome h -> ValueSome (max h bar.high) | ValueNone -> ValueSome bar.high
        runLo    <- match runLo with ValueSome l -> ValueSome (min l bar.low)  | ValueNone -> ValueSome bar.low
        runHiClose <- match runHiClose with ValueSome h -> ValueSome (max h bar.close) | ValueNone -> ValueSome bar.close
        runLoClose <- match runLoClose with ValueSome l -> ValueSome (min l bar.close) | ValueNone -> ValueSome bar.close
        runVolHi <- match runVolHi with ValueSome v -> ValueSome (max v bar.volume) | ValueNone -> ValueSome bar.volume
        cumVol   <- cumVol + bar.volume
        lag20.Push bar.close      // 20-bar delay line; read post-fold at entry = the 20m %-change into it
        if sessionOpen.IsNone then sessionOpen <- ValueSome bar.``open``

        // fold the chosen mean-reversion anchor (only the active one accumulates).
        match cfg.Target with
        | NoTarget -> ()
        | Vwap ->
            let tp = (bar.high + bar.low + bar.close) / 3.0
            vwapNum <- vwapNum + tp * float bar.volume
            vwapDen <- vwapDen + float bar.volume
        | Ma _ -> maTgt.Push bar.close
        | Channel _ -> chanTgt.Push bar.low

        lastBar   <- ValueSome bar
        barsSeen  <- barsSeen + 1

    /// Advance one open position by the current bar, returning the (possibly
    /// state-changed) position — an immutable update, mirroring HighFlyer's
    /// `Update`. Exit precedence (first to fire wins):
    ///   1. protective stop (if armed) — the 2-bar local low at entry; fires when
    ///      bar.low reaches it, fills at the bar's CLOSE (filling exactly at the stop
    ///      price would assume we caught the precise low — too optimistic; the close
    ///      is still optimistic but far more defensible).
    ///   2. mean-reversion TARGET (short, if armed) — cover when the bar's low reaches
    ///      the snapshotted (strictly-prior) anchor; fills at the anchor as a resting
    ///      limit, but no better than the bar OPEN if price gapped through it. Doubles
    ///      as the loss-cut (price that never reverts rides to the time-stop / MOC).
    ///   3. time-stop (if armed) — `TimeStopMin` minutes after entry, fills at close.
    ///   4. MOC — at/after the cutoff, flatten at close.
    /// An already-exited position is returned unchanged.
    member _.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        // the 2-bar local extreme AT this bar (min/max of this bar + the prior bar).
        // sLastBar is the strictly-prior bar (ValueNone only on the 08:30 open bar, well
        // before any entry can fire). For a SHORT (upside breakout) the trail is a 2-bar
        // LOW; for a LONG (downside breakout) a 2-bar HIGH.
        let twoBarExtreme =
            match sLastBar with
            | ValueSome prev -> if cfg.Downside then max prev.high bar.high else min prev.low bar.low
            | ValueNone      -> if cfg.Downside then bar.high else bar.low
        match pos.State with
        | ExitedAt _ | Skipped -> pos
        | Armed (armMin, trail, armPrice, riseCleared) ->
            // ratchet the trailing 2-bar extreme WITH the move (a short's trail only rises;
            // a long's only falls).
            let trail' = if cfg.Downside then min trail twoBarExtreme else max trail twoBarExtreme
            // session extreme at fill — the --trail-entry protective stop reference.
            let sessionStop () =
                match (if cfg.Downside then sRunLo else sRunHi) with
                | ValueSome x -> x
                | ValueNone   -> if cfg.Downside then bar.low else bar.high
            if cfg.ExtGate > 0.0 then
                // --ext-gate (<ExtGate-at-breakout) branch: this arm was created because
                // day-extension was BELOW ExtGate at the breakout. Two confirmations race:
                //   (1) if RiseEntry>0 and price runs +RiseEntry past the breakout price FIRST
                //       → take it (short into the parabolic move at the rise level).
                //   (2) else if a ROLLOVER fires (close back through the trail) → take it ONLY
                //       if day-extension has reached ExtGate by then; otherwise SKIP (no trade).
                let riseLevel = if cfg.Downside then armPrice * (1.0 - cfg.RiseEntry) else armPrice * (1.0 + cfg.RiseEntry)
                let riseHit = cfg.RiseEntry > 0.0 && (if cfg.Downside then bar.low <= riseLevel else bar.high >= riseLevel)
                let rolled  = if cfg.Downside then bar.close > trail' else bar.close < trail'
                let extOk   = (if cfg.Downside then -(extension bar.close) else extension bar.close) >= cfg.ExtGate
                if riseHit then
                    let fill = if cfg.Downside then min riseLevel bar.``open`` else max riseLevel bar.``open``
                    { pos with EntryMin = bar.etMin; EntryPx = fill; StopLevel = sessionStop (); State = Holding }
                elif rolled && extOk then
                    { pos with EntryMin = bar.etMin; EntryPx = bar.close; StopLevel = sessionStop (); State = Holding }
                elif rolled then
                    // rolled over but not extended enough → no trade. Terminal Skipped state
                    // (never re-triggers, never becomes a trip).
                    { pos with State = Skipped }
                elif bar.etMin >= cfg.MocMin then pos   // neither fired by MOC → dropped (no trip)
                else { pos with State = Armed (armMin, trail', armPrice, riseCleared) }
            else
            // --rise-entry GATE: require a further RiseEntry move past the arm price first
            // (short above armPrice*(1+x), long below armPrice*(1-x)). Once a bar's extreme
            // reaches that level the gate is cleared (latched). With RiseEntry=0 it's open.
            let riseLevel = if cfg.Downside then armPrice * (1.0 - cfg.RiseEntry) else armPrice * (1.0 + cfg.RiseEntry)
            let riseCleared' =
                riseCleared
                || cfg.RiseEntry <= 0.0
                || (if cfg.Downside then bar.low <= riseLevel else bar.high >= riseLevel)
            if not riseCleared' then
                // rise gate still pending → can't enter this bar; just ratchet & wait.
                if bar.etMin >= cfg.MocMin then pos
                else { pos with State = Armed (armMin, trail', armPrice, false) }
            elif cfg.TrailEntry then
                // wait for the rollover: enter on the first close back through the trail.
                let triggered = if cfg.Downside then bar.close > trail' else bar.close < trail'
                if triggered then
                    { pos with EntryMin = bar.etMin; EntryPx = bar.close; StopLevel = sessionStop (); State = Holding }
                elif bar.etMin >= cfg.MocMin then pos
                else { pos with State = Armed (armMin, trail', armPrice, true) }
            else
                // immediate fill the moment the rise gate clears: short INTO the parabolic
                // move at the rise level (no better than the bar OPEN if it gapped through).
                // (Reached only with RiseEntry>0 & not trail — RiseEntry=0 direct entry never arms.)
                let fill = if cfg.Downside then min riseLevel bar.``open`` else max riseLevel bar.``open``
                { pos with EntryMin = bar.etMin; EntryPx = fill; StopLevel = sessionStop (); State = Holding }
        | Holding ->
            // a short cover-target: bar.low reaching the strictly-prior anchor.
            let targetHit =
                cfg.Short &&
                match sTargetAnchor with ValueSome a -> bar.low <= a | ValueNone -> false
            // protective stop: directional, fires when price reaches pos.StopLevel. Opt-in
            // via UseStop in BOTH entry models (so --trail-entry alone = backside entry,
            // stopless, hold-to-MOC; add --intraday-stop for the session stop).
            //   direct entry  — the 2-bar local extreme at the breakout (short stops on
            //                   bar.low <= level; long on bar.high >= level).
            //   --trail-entry / --rise-entry — the SESSION extreme at fill (short stops on
            //                   bar.high >= the session high → ran back over the top; long on
            //                   bar.low <= the session low).
            // Both Armed paths (trail, rise) set StopLevel to the session extreme, so the
            // session-stop direction applies whenever either is on.
            let sessionStopMode = cfg.TrailEntry || cfg.RiseEntry > 0.0
            let stopHit =
                if sessionStopMode then
                    if cfg.Downside then bar.low <= pos.StopLevel else bar.high >= pos.StopLevel
                elif cfg.Downside then bar.high >= pos.StopLevel
                else bar.low <= pos.StopLevel
            // wide %-stop: a fixed adverse excursion from the entry (e.g. 0.5 = 50% against
            // us). Independent of UseStop — a catastrophe stop meant to clip the run-over
            // tail while leaving normal noise untouched. Short stops if price rises PctStop
            // above entry; long if it falls PctStop below. Fills at the level, but no better
            // than the bar OPEN if the bar gapped clean through it.
            let pctStopLevel =
                if cfg.Short then pos.EntryPx * (1.0 + cfg.PctStop)
                else pos.EntryPx * (1.0 - cfg.PctStop)
            let pctStopHit =
                cfg.PctStop > 0.0 &&
                (if cfg.Short then bar.high >= pctStopLevel else bar.low <= pctStopLevel)
            if cfg.UseStop && stopHit then
                let reason = if cfg.TrailEntry then "session_stop" else "intraday_stop"
                { pos with State = ExitedAt (bar.etMin, bar.close, reason) }
            elif pctStopHit then
                let fill = if cfg.Short then max pctStopLevel bar.``open`` else min pctStopLevel bar.``open``
                { pos with State = ExitedAt (bar.etMin, fill, "pct_stop") }
            elif targetHit then
                let anchor = sTargetAnchor.Value
                let fill = min anchor bar.``open``   // limit fill at anchor; gap-through fills at the open
                { pos with State = ExitedAt (bar.etMin, fill, "target") }
            elif cfg.TimeStopMin > 0 && bar.etMin >= pos.EntryMin + cfg.TimeStopMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "time_stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar: fold the bar in, advance every
    /// open position, then (if Gate 3 fires) open a new independent position.
    /// Ordering mirrors the daily engine — the new position is appended AFTER the
    /// existing positions advance, so an entry bar is never its own first exit-check.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar positions.[i]
        if this.ShouldEnter bar then
            // ShouldEnter passed => sRunHi / this.Tightness / this.AtrPct are all
            // ValueSome (each is gated). sLastBar is ValueSome too: it's ValueNone only
            // on the 08:30 session-open bar, but entries can't fire before 09:35
            // (EntryStartMin), by which point a prior bar always exists. The .Value
            // reads are therefore safe — no sentinel needed.
            // StopLevel = the 2-bar local extreme at entry. Upside: min of the breakout
            // bar's low and the prior bar's low (a tight stop just under the breakout base).
            // Downside: max of the two highs (just above the breakdown base). Self-
            // calibrating; replaces the wide 08:30-session extreme.
            // the 2-bar local extreme at the breakout bar (upside = 2-bar low, downside =
            // 2-bar high): the direct-entry protective stop, AND the initial trail level
            // for --trail-entry.
            let twoBar =
                if cfg.Downside then max sLastBar.Value.high bar.high
                else min sLastBar.Value.low bar.low
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  StopLevel = twoBar
                  BreakoutRef = this.BreakoutRef.Value
                  AtrPctAtEntry = this.AtrPct.Value
                  TightnessAtEntry = this.Tightness.Value
                  // cumVol was folded by ProcessBar (called before this append), so it
                  // already includes THIS breakout bar → cumulative volume through entry.
                  CumVolAtEntry = cumVol
                  BreakoutBarVol = bar.volume
                  // sRunVolHi is the pre-push snapshot (excludes this bar), so this is a true
                  // "new session vol high" test. ValueNone (first bar) counts as a new high.
                  NewVolHigh = (match sRunVolHi with ValueSome vh -> bar.volume > vh | ValueNone -> true)
                  VolVsHigh = (match sRunVolHi with ValueSome vh when vh > 0L -> float bar.volume / float vh | _ -> nan)
                  BreakoutBarOpen = bar.``open``
                  PrevBarClose = sLastBar.Value.close
                  Chg20mAtEntry = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                  // entry routing:
                  //   --ext-gate — if day-extension already >= ExtGate at the breakout, enter
                  //                DIRECT (parabolic now); else arm a ROLLOVER gated on reaching
                  //                ExtGate by fill time.
                  //   --trail-entry / --rise-entry — arm (wait for the rollover and/or rise gate).
                  //   otherwise — Holding now (fill at the breakout close).
                  State =
                    if cfg.ExtGate > 0.0 then
                        if extension bar.close >= cfg.ExtGate then Holding
                        else Armed (bar.etMin, twoBar, bar.close, false)
                    elif cfg.TrailEntry || cfg.RiseEntry > 0.0 then Armed (bar.etMin, twoBar, bar.close, false)
                    else Holding }

    /// Flatten any still-open positions at the last folded bar's close (MOC /
    /// hold-to-close). The last bar is held in state (set by ProcessBar), so no
    /// argument is needed. A no-op if no bar was ever processed. (Named `Flatten`,
    /// not `Finalize`, to avoid colliding with Object.Finalize, the GC finalizer.)
    member _.Flatten () =
        match lastBar with
        | ValueNone -> ()
        | ValueSome lb ->
            for i in 0 .. positions.Count - 1 do
                match positions.[i].State with
                | Holding -> positions.[i] <- { positions.[i] with State = ExitedAt (lb.etMin, lb.close, "moc") }
                | Armed _ | Skipped | ExitedAt _ -> ()   // never-filled / skipped → no trip (dropped below)

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
