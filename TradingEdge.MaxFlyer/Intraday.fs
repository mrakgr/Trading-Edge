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

/// Intraday position life-cycle.
type IntraPosState =
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "moc" | "intraday_stop"

/// One intraday trip. `EntryMin`/`EntryPx` are the FILL (breakout-bar) values;
/// the *_at_entry metrics are the intraday indicator snapshots at the entry bar.
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      StopLo: float            // the 2-bar local low at entry (min of breakout bar + prior bar low) — the protective-stop level
      RunHiAtEntry: float      // running session high (strictly-prior bars) the breakout cleared
      AtrPctAtEntry: float     // intraday log-ATR snapshot at entry
      TightnessAtEntry: float  // intraday tightness snapshot at entry
      State: IntraPosState }   // immutable — advancing a position returns a NEW record (HighFlyer style)

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

    // session-cumulative running extremes over ALL bars seen so far (NOT windowed),
    // accumulating from SessionStartMin (08:30 ET). The breakout reference is the high
    // of all strictly-prior bars; runVolHi is the max single-bar VOLUME so far — the
    // volume-confirmation reference (a real breakout re-takes the session volume high,
    // which normally only happens at the open/close).
    let mutable runHi    : float voption = ValueNone
    let mutable runVolHi : int64 voption = ValueNone
    // the most recent bar folded. Read at the TOP of ProcessBar (before the current
    // bar is pushed) it IS the immediately-prior bar — the source of both the true-range
    // prior close and the 2-bar-stop prior low; read by Flatten it's the day's last bar.
    let mutable lastBar  : MinuteBar voption = ValueNone
    let mutable barsSeen  = 0

    // ----- pre-push snapshots (state going INTO the current bar; no lookahead) -----
    let mutable sRunHi     : float voption = ValueNone
    let mutable sRunVolHi  : int64 voption = ValueNone
    let mutable sLastBar   : MinuteBar voption = ValueNone   // the prior bar, as-of going into this bar (its low feeds the 2-bar stop)
    let mutable sAtrLog    : float voption = ValueNone
    let mutable sAtrLin    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone

    let positions = ResizeArray<IntradayPosition>()

    member _.Ticker = ticker
    member _.Day = day
    member _.BarsSeen = barsSeen

    // ----- read members (mirror the daily QullaSystem) -----
    member _.RunHigh = sRunHi
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

    /// Count of currently-Holding (open) positions.
    member _.OpenCount =
        let mutable n = 0
        for p in positions do (match p.State with Holding -> n <- n + 1 | _ -> ())
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
        // new session high vs strictly-prior bars (08:30-onward running high)
        && gate sRunHi (fun hi -> bar.close > hi)
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
    member _.ProcessBar (bar: MinuteBar) =
        // `lastBar` still holds the immediately-prior bar here (it's updated at the
        // very end), so it's the source of both the prior low and the prior close.
        // 1) snapshot pre-bar state
        sRunHi     <- runHi
        sRunVolHi  <- runVolHi
        sLastBar   <- lastBar
        sAtrLog    <- atrLog.State
        sAtrLin    <- atrLin.State
        sRangeHigh <- rangeHigh.State
        sRangeLow  <- rangeLow.State

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
        runVolHi <- match runVolHi with ValueSome v -> ValueSome (max v bar.volume) | ValueNone -> ValueSome bar.volume
        lastBar   <- ValueSome bar
        barsSeen  <- barsSeen + 1

    /// Advance one open position by the current bar, returning the (possibly
    /// state-changed) position — an immutable update, mirroring HighFlyer's
    /// `Update`. The protective stop (if armed) is the 2-bar local low at entry; a
    /// stop fires when bar.low reaches it, and fills at the bar's CLOSE — filling
    /// exactly at the stop price would assume we caught the precise low, which is
    /// too optimistic; the close is still optimistic but far more defensible.
    /// Otherwise hold; at/after the MOC cutoff flatten at the bar close. An
    /// already-exited position is returned unchanged.
    member _.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            if cfg.UseStop && bar.low <= pos.StopLo then
                { pos with State = ExitedAt (bar.etMin, bar.close, "intraday_stop") }
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
            // StopLo = the 2-bar local low at entry: min of the breakout bar's low and
            // the immediately-prior bar's low. A tight, self-calibrating stop just under
            // the breakout's base (replaces the wide 08:30-session low).
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  StopLo = min sLastBar.Value.low bar.low
                  RunHiAtEntry = sRunHi.Value
                  AtrPctAtEntry = this.AtrPct.Value
                  TightnessAtEntry = this.Tightness.Value
                  State = Holding }

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
                | ExitedAt _ -> ()

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
