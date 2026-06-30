module TradingEdge.MaxFlyer.Intraday

open System
open TradingEdge.MaxFlyer.RollingMa

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

/// Intraday position life-cycle. With `--trail-entry`, the breakout first ARMS a
/// position (no fill); it transitions to Holding only when a bar closes back through
/// the ratcheting trailing level (short on the rollover, not into the thrust). Without
/// trail-entry the breakout creates a Holding position directly (Armed is never used).
type IntraPosState =
    | Armed of armMin: int * trailLevel: float    // breakout fired; waiting for a close through the trail
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "moc" | "intraday_stop" | "session_stop"

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
      MaxConcurrent: int }     // cap on currently-OPEN (Holding) positions; 0 = unlimited

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Finalize` and read `Trips()`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly) =

    // ----- rolling intraday structures (1m timeframe) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow 1m bars
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear)
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
    let mutable runVolHi : int64 voption = ValueNone
    // the most recent bar folded. Read at the TOP of ProcessBar (before the current
    // bar is pushed) it IS the immediately-prior bar — the source of both the true-range
    // prior close and the 2-bar-stop prior low; read by Flatten it's the day's last bar.
    let mutable lastBar  : MinuteBar voption = ValueNone
    let mutable barsSeen  = 0

    // ----- pre-push snapshots (state going INTO the current bar; no lookahead) -----
    let mutable sRunHi     : float voption = ValueNone
    let mutable sRunLo     : float voption = ValueNone
    let mutable sRunVolHi  : int64 voption = ValueNone
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

    // ----- read members (mirror the daily QullaSystem) -----
    /// The strictly-prior session extreme the breakout must clear — the running high for
    /// an upside breakout, the running low for a downside one.
    member _.BreakoutRef = if cfg.Downside then sRunLo else sRunHi
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
        for p in positions do (match p.State with Holding | Armed _ -> n <- n + 1 | ExitedAt _ -> ())
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
        // VOLUME CONFIRMATION: the breakout bar must EXCEED the strictly-prior session
        // 1m-volume high. Most breakouts never re-take the volume high (which normally
        // prints at the open/close), so a morning bar that does is a significant event.
        // This + the tightness filter is the intended edge.
        && gate sRunVolHi (fun vh -> bar.volume > vh)
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
        runVolHi <- match runVolHi with ValueSome v -> ValueSome (max v bar.volume) | ValueNone -> ValueSome bar.volume

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
        | ExitedAt _ -> pos
        | Armed (armMin, trail) ->
            // trail-entry: ratchet the trailing 2-bar extreme WITH the move (a short's
            // trail only rises; a long's only falls), then enter when a bar CLOSES back
            // through it (the rollover). Fill at that bar's close; the protective stop is
            // the SESSION extreme at fill (sRunHi for a short, sRunLo for a long).
            let trail' = if cfg.Downside then min trail twoBarExtreme else max trail twoBarExtreme
            let triggered = if cfg.Downside then bar.close > trail' else bar.close < trail'
            if triggered then
                let sessionStop =
                    match (if cfg.Downside then sRunLo else sRunHi) with
                    | ValueSome x -> x
                    | ValueNone   -> if cfg.Downside then bar.low else bar.high
                { pos with EntryMin = bar.etMin; EntryPx = bar.close; StopLevel = sessionStop; State = Holding }
            elif bar.etMin >= cfg.MocMin then pos   // never filled by MOC → stays Armed, dropped (no trip)
            else { pos with State = Armed (armMin, trail') }
        | Holding ->
            // a short cover-target: bar.low reaching the strictly-prior anchor.
            let targetHit =
                cfg.Short &&
                match sTargetAnchor with ValueSome a -> bar.low <= a | ValueNone -> false
            // protective stop: directional, fires when price reaches pos.StopLevel.
            //   direct entry  — the 2-bar local extreme at the breakout (short stops on
            //                   bar.low <= level; long on bar.high >= level), opt-in (UseStop).
            //   --trail-entry — the SESSION extreme at fill (short stops on bar.high >= the
            //                   session high → ran back over the top; long on bar.low <= the
            //                   session low). Intrinsic to the model: always armed.
            let stopHit =
                if cfg.TrailEntry then
                    if cfg.Downside then bar.low <= pos.StopLevel else bar.high >= pos.StopLevel
                elif cfg.Downside then bar.high >= pos.StopLevel
                else bar.low <= pos.StopLevel
            if (cfg.UseStop || cfg.TrailEntry) && stopHit then
                let reason = if cfg.TrailEntry then "session_stop" else "intraday_stop"
                { pos with State = ExitedAt (bar.etMin, bar.close, reason) }
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
                  // direct entry → Holding now (fill at the breakout close). --trail-entry →
                  // Armed: wait for a close back through the (ratcheting) trail before filling.
                  State = if cfg.TrailEntry then Armed (bar.etMin, twoBar) else Holding }

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
                | Armed _ | ExitedAt _ -> ()   // Armed but never filled → no trip (dropped below)

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
