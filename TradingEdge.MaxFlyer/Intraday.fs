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
      StopLo: float            // intraday session low at entry (the protective-stop floor; not capped)
      RunHiAtEntry: float      // running session high (strictly-prior bars) the breakout cleared
      AtrPctAtEntry: float     // intraday log-ATR snapshot at entry
      TightnessAtEntry: float  // intraday tightness snapshot at entry
      mutable State: IntraPosState }

/// Intraday Gate-3 + engine config.
type IntradayConfig =
    { VolWindow: int           // intraday ATR/tightness lookback in 1m BARS (swept; default ~20)
      MaxTightness: float      // intraday tightness gate (per-bar)
      MaxAtrPct: float         // intraday ATR% gate (log-space, per-bar)
      MinHighBars: int         // require N prior RTH bars before a "new high" counts (warmup)
      UseStop: bool            // arm the intraday-low protective stop
      MocMin: int              // MOC cutoff in ET minutes (960 = 16:00)
      RthOpenMin: int          // RTH open in ET minutes (570 = 09:30)
      MaxConcurrent: int }     // cap on currently-OPEN (Holding) positions; 0 = unlimited

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Finalize` and read `Trips()`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly) =

    // ----- rolling intraday structures (1m timeframe) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow 1m bars
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear)
    let rangeHigh = MaxMa(cfg.VolWindow)        // intraday tightness: max high over the window
    let rangeLow  = MinMa(cfg.VolWindow)        // intraday tightness: min low over the window

    // session-cumulative running high/low of RTH bars seen so far (NOT windowed):
    // the new-high breakout reference is the high of ALL strictly-prior RTH bars today.
    let mutable runHi    : float voption = ValueNone
    let mutable runLo    : float voption = ValueNone
    let mutable prevClose : float voption = ValueNone
    let mutable barsSeen  = 0

    // ----- pre-push snapshots (state going INTO the current bar; no lookahead) -----
    let mutable sRunHi     : float voption = ValueNone
    let mutable sRunLo     : float voption = ValueNone
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
    /// (close above the running high of strictly-prior bars) with the intraday
    /// tightness/ATR% gates satisfied, enough warmup bars, and concurrency room.
    member this.ShouldEnter (bar: MinuteBar) : bool =
        let inline gate (v: float voption) (test: float -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // new session high vs strictly-prior RTH bars
        gate sRunHi (fun hi -> bar.close > hi)
        // enough prior bars this session (warmup; also lets the windows warm).
        // barsSeen is post-ProcessBar (includes the current bar), so STRICTLY-greater
        // means "MinHighBars bars came before this one" — earliest entry at
        // 09:30 + MinHighBars minutes (matches the daily engine's `> MinPriorDays`).
        && barsSeen > cfg.MinHighBars
        // intraday tightness gate
        && gate this.Tightness (fun t -> t < cfg.MaxTightness)
        // intraday ATR% gate (log-space)
        && gate this.AtrPct (fun a -> a < cfg.MaxAtrPct)
        // concurrency room (count only currently-OPEN positions)
        && (cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent)

    /// Fold the current bar into the intraday indicators. Snapshots taken BEFORE
    /// the push (prior-bars / no-lookahead), then the bar is folded in.
    member _.ProcessBar (bar: MinuteBar) =
        // 1) snapshot pre-bar state
        sRunHi     <- runHi
        sRunLo     <- runLo
        sAtrLog    <- atrLog.State
        sAtrLin    <- atrLin.State
        sRangeHigh <- rangeHigh.State
        sRangeLow  <- rangeLow.State

        // 2) fold the bar in. True range vs the prior 1m close.
        match prevClose with
        | ValueSome pc when bar.high > 0.0 && bar.low > 0.0 && pc > 0.0 ->
            (max bar.high pc - min bar.low pc) |> atrLin.Push
            log (max bar.high pc / min bar.low pc) |> atrLog.Push
        | _ -> ()

        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        runHi <- match runHi with ValueSome h -> ValueSome (max h bar.high) | ValueNone -> ValueSome bar.high
        runLo <- match runLo with ValueSome l -> ValueSome (min l bar.low)  | ValueNone -> ValueSome bar.low
        prevClose <- ValueSome bar.close
        barsSeen  <- barsSeen + 1

    /// Advance one open position by the current bar. The protective stop (if
    /// armed) is the intraday session low at entry; a stop fires when bar.low
    /// reaches it (fill at min(stop, open) — a gap-down through the stop fills
    /// at the open, no top-tick credit). Otherwise hold; at/after the MOC cutoff
    /// flatten at the bar close.
    member _.Advance (bar: MinuteBar) (pos: IntradayPosition) =
        match pos.State with
        | ExitedAt _ -> ()
        | Holding ->
            if cfg.UseStop && bar.low <= pos.StopLo then
                pos.State <- ExitedAt (bar.etMin, min pos.StopLo bar.``open``, "intraday_stop")
            elif bar.etMin >= cfg.MocMin then
                pos.State <- ExitedAt (bar.etMin, bar.close, "moc")

    /// Advance the whole system by one minute bar: fold the bar in, advance every
    /// open position, then (if Gate 3 fires) open a new independent position.
    /// Ordering mirrors the daily engine — the new position is appended AFTER the
    /// existing positions advance, so an entry bar is never its own first exit-check.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            this.Advance bar positions.[i]
        if this.ShouldEnter bar then
            let orNan = function ValueSome v -> v | ValueNone -> nan
            // StopLo = the intraday session low going INTO this bar (sRunLo, pre-push,
            // strictly-prior bars). The self-calibrating range analog (ORB doc §9) — not capped.
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  StopLo = orNan sRunLo
                  RunHiAtEntry = orNan sRunHi
                  AtrPctAtEntry = orNan this.AtrPct
                  TightnessAtEntry = orNan this.Tightness
                  State = Holding }

    /// Flatten any still-open positions at the final bar's close (MOC / hold-to-close).
    member _.Finalize (lastBar: MinuteBar) =
        for p in positions do
            match p.State with
            | Holding -> p.State <- ExitedAt (lastBar.etMin, lastBar.close, "moc")
            | ExitedAt _ -> ()

    /// All positions for this (ticker, day), in entry order.
    member _.Positions = positions
