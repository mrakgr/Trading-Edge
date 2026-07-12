module TradingEdge.DipRiderV4.Intraday

open System
open TradingEdge.RollingMa

/// One 1-minute RTH bar, already converted to ET minutes-since-midnight and
/// split-adjusted to the candidate's daily scale.
type MinuteBar =
    { etMin: int
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// Intraday position life-cycle. DipRiderV3 positions are Holding immediately
/// (no Armed/Skipped arming states — those were the breakout engine's, deleted).
type IntraPosState =
    | Holding
    | ExitedAt of exitMin: int * exitPx: float * reason: string   // "stop"|"pct_stop"|"exhaust"|"vwap_lost"|"time_stop"|"moc"

/// One intraday trip. `EntryMin`/`EntryPx` are the FILL values (the entry bar's
/// close); the *AtEntry metrics are the strictly-prior indicator snapshots at the
/// entry bar (no-lookahead).
type IntradayPosition =
    { EntryMin: int
      EntryPx: float
      StopLevel: float
      State: IntraPosState }   // immutable — advancing a position returns a NEW record (HighFlyer style)

/// Intraday engine config — DipRiderV3 (pure trailing-window momentum).
type IntradayConfig =
    { 
        VolWindow: int           // trailing ATR / tightness / OLS-slope lookback in 1m BARS (default 20)
        EmaPeriod: int           // the 9-EMA period (the closes-above-EMA reference)
        SessionStartMin: int     // first ET minute fed to the engine (510 = 08:30 ET). The session extremes
                                 // (min/max close, max volume) accumulate from here.
        FeatureStartMin: int     // ET minute the OTHER features start folding (570 = 09:30 ET, the RTH open).
                                 // VWAP, both OLS slopes, ATR, tightness, EMA, SumMa above-EMA, 15m-init-vol
                                 // all fold only once bar.etMin >= this (no premarket contamination).
        EntryStartMin: int       // earliest ET minute an entry may fire (600 = 10:00). Bars before are
                                 // processed (to warm the windows) but do not TRADE.
        EntryEndMin: int         // LATEST ET minute an entry may fire (810 = 13:30). 0 / >=MocMin = no upper bound.
        MocMin: int              // MOC cutoff in ET minutes (960 = 16:00)
        MinVolClimb: float       // ⭐ F32 MAIN VOLUME GATE (REPLACES vol_slope): require vol_climb = (volEma −
                                 // volEmaMin)/volEma >= this. volEma = 9-EMA of raw volume, volEmaMin = its 20m min.
                                 // Fractional [0,1); 0.6 = volume-EMA is 2.5× its 20m floor. Beats vol_slope (clip PF
                                 // 2.07 vs 1.70), wins every year, fixes 2021 (1.07->1.42). 0 = off.
        MinChg1d: float          // DAY-DIRECTION FLOOR (F17): reject if the entry px is < this fraction vs the
                                 // PREV daily close (entry/prevClose - 1 < this). A stock RED on the day is
                                 // fighting its own daily trend — buying its intraday bounce loses. Default 0.0
                                 // (must be green on the day). NaN/-inf = off.
        MinChg3d: float          // 3-DAY TREND FLOOR (F28): reject if entry / close-3d-ago - 1 < this. A name
                                 // FALLING over 3 days is a poor momentum buy in BOTH regimes (bad-everywhere,
                                 // durable). Default 0.0 (3-day trend must be up). NaN/-inf = off. Needs close3d.
        MinEmaVsVwap: float      // VWAP-LOCATION FLOOR (F27, REPLACES MinEntryVsVwap): reject if the current 9-EMA
                                 // is more than |this| below the session VWAP (ema/vwap − 1 < this). The smoothed
                                 // TREND vs VWAP (ignores single wicks) — cleaner than the price-vs-VWAP floor.
                                 // Default −0.02. NaN/-inf = off.
        MinAtrPct: float         // log-ATR FLOOR: require the 20m log-ATR >= this. THE MAIN LEVER (F3: PF scales
                                 // monotonically with ATR; sub-0.013 is flat/dead). 0 = off. Default 0.013.
        MaxAtrPct: float         // log-ATR CEILING (+inf disables).
        MinCloseAbove6: int      // require SumAbove6 >= this (>= N of the last 6 bars closed above the EMA).
                                 // 0 = DISABLED (V3 default — start off, tune later).
      }

type State = 
    {
        bar  : MinuteBar voption
        vwap : float voption
        ema : float voption
        emaLow : float voption
        atrLin : float voption
        atrLog : float voption
        sum6 : int voption
        sPriceSlope : float voption
        volEma : float voption
        volEmaMin : float voption
        barsSeen : int
        armed : bool
    }

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Flatten` and read `Positions`.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, prevClose: float) =
    // ----- rolling intraday structures (1m timeframe; all fed ONLY from the 09:30 feature anchor) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow bars (the volatility feature)
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear) — the tightness DENOMINATOR
    let ema       = EmaMa(cfg.EmaPeriod)        // the 9-EMA (closes-above-EMA reference)
    let emaLow    = MinMa(cfg.VolWindow)        // the 20m trailing MIN of the 9-EMA — the ema_climb feature denominator base
    let volEma    = EmaMa(cfg.EmaPeriod)        // 9-EMA of raw 1m VOLUME — the volume analogue of the price 9-EMA
    let volEmaMin = MinMa(cfg.VolWindow)        // 20m trailing MIN of the volume-9-EMA — the vol_climb base (mirrors emaMin)
    let sum6    = SumMa(6)
    // trailing-window OLS slopes (fixed VolWindow bars, fed every VALID feature-bar). Log-close = trend slope,
    // log-volume = the volume-trend slope (the F14 lever). Both pushed TOGETHER only when both logs are valid,
    // so they stay on the identical push-index x-axis (directly comparable).
    let priceOls  = OlsSlopeMa(cfg.VolWindow)
    // trailing 5-bar raw VOLUME avg — the recent-tempo numerator for the exhaustion cut (5m-avg vs the 15m
    // opening-avg and the 20d per-minute avg). Fed every VALID feature-bar.
    let vol5avg   = AvgMa(5)

    // session VWAP = Σ(typical_price · volume) / Σ(volume), typical = (h+l+c)/3, from the 09:30 feature anchor.
    let mutable vwap  = RatioMa()

    let mutable sState : State = 
        {
            bar = ValueNone
            vwap = ValueNone
            ema = ValueNone
            emaLow = ValueNone
            atrLin = ValueNone
            atrLog = ValueNone
            sum6 = ValueNone
            sPriceSlope = ValueNone
            volEma = ValueNone
            volEmaMin = ValueNone
            barsSeen = 0
            armed = true
        }

    let mutable position : IntradayPosition voption = ValueNone
    let mutable armed = true
    let mutable bar = ValueNone
    let mutable barsSeen = 0

    member _.Ticker = ticker
    member _.Day = day

    member _.State : State =
        {
            bar = bar 
            vwap = vwap.State
            ema = ema.State
            emaLow = emaLow.State
            atrLin = atrLin.State
            atrLog = atrLog.State
            sum6 = ValueOption.map (round >> int) sum6.State
            sPriceSlope = priceOls.Slope
            volEma = volEma.State
            volEmaMin = volEmaMin.State
            barsSeen = barsSeen
            armed = armed
        }

    /// Fold one minute bar into all rolling state (snapshot-before-push, no-lookahead).
    member this.ProcessBar (bar: MinuteBar) =
        if bar.etMin >= cfg.EntryStartMin then
            ()
        if bar.etMin >= cfg.FeatureStartMin then
            vwap.Push(bar.close * float bar.volume, float bar.volume)
            ema.Push(bar.close)
            ema.State |> ValueOption.iter emaLow.Push
            failwith "Fill in..."


    /// Advance one open position by the current bar (immutable update). Exit precedence (first to fire wins):
    ///   stop -> pct_stop -> exhaust -> vwap_lost -> time_stop -> moc.
    member private this.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            // EmaStop mode: the trigger is the LIVE 9-EMA closing below the frozen level (like BreakoutTimer),
            // NOT the bar price. Fills at the bar close. Otherwise: geometry/pct stop on close-or-low.
            let stopHit =
                pos.StopLevel > Double.NegativeInfinity
                && (if cfg.EmaStop then (match ema.State with ValueSome e -> e < pos.StopLevel | ValueNone -> false)
                    elif cfg.StopOnClose then bar.close <= pos.StopLevel
                    else bar.low <= pos.StopLevel)
            let pctStopLevel = pos.EntryPx * (1.0 - cfg.PctStop)
            let pctStopHit = cfg.PctStop > 0.0 && bar.low <= pctStopLevel
            // EXHAUSTION EXIT: this bar makes a NEW SESSION HIGH (wick > strictly-prior session high) AND is a
            // VOLUME BLOW-OFF (>= mult × BOTH per-minute baselines). Fill at the bar close.
            let exhaustHit =
                cfg.ExhaustExit
                && permin20d > 0.0 && permin15m > 0.0
                && float bar.volume >= cfg.ExhaustVolMult * permin20d
                && float bar.volume >= cfg.ExhaustVolMult * permin15m
                && (match sRunHi with ValueSome h -> bar.high > h | ValueNone -> false)
            // LOSS-OF-VWAP exit: the 9-EMA had been above VWAP for >= VwapExitBars bars going into this bar
            // (strictly-prior) AND on THIS bar the live EMA is now BELOW VWAP. Fill at this bar's close.
            let vwapLostHit =
                cfg.VwapExitBars > 0
                && sEmaAboveVwapBars >= cfg.VwapExitBars
                && (match ema.State, this.LiveVwap with ValueSome e, ValueSome v -> e < v | _ -> false)
            if stopHit then
                let fill =
                    if cfg.EmaStop || cfg.StopOnClose then bar.close
                    else min pos.StopLevel bar.``open``
                { pos with State = ExitedAt (bar.etMin, fill, "stop") }
            elif pctStopHit then
                { pos with State = ExitedAt (bar.etMin, min pctStopLevel bar.``open``, "pct_stop") }
            elif exhaustHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "exhaust") }
            elif vwapLostHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "vwap_lost") }
            elif cfg.TimeStopMin > 0 && bar.etMin >= pos.EntryMin + cfg.TimeStopMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "time_stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// Advance the whole system by one minute bar: fold the bar in, advance every open position, then
    /// (if Gate 3 fires) open a new independent position. The new position is appended AFTER existing
    /// positions advance, so an entry bar is never its own first exit-check.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar positions.[i]
        // SMB backside: a price pattern with a free slot ALWAYS opens a position — REAL (Reported) if vol_climb
        // passes, else a SHADOW (Reported=false) that holds the slot and runs the full exit logic but isn't
        // reported. Same entry/stop/exits either way, so timing == the mc1 base book; the reported subset == the
        // post-hoc vol filter.
        if this.PricePatternFires bar then
            let reported = this.VolumePasses
            // STOP (same geometry as the settled DRV3 stop): d = entry - floor; stop = entry - d*StopDistFrac (2/3).
            let stopFloor =
                if cfg.StopFloorSessMin then sSessMinClose
                else (match sCloseLow with ValueSome l -> l | ValueNone -> nan)
            let rawStop =
                if cfg.EmaStop then
                    // frozen 20m-min-9EMA used DIRECTLY as the stop (no session-low geometry).
                    match sEmaMin with
                    | ValueSome m when m > 0.0 && bar.close > m -> m
                    | _ -> Double.NegativeInfinity
                elif cfg.GeomStop && not (Double.IsNaN stopFloor) && stopFloor > 0.0 && bar.close > stopFloor then
                    let d = bar.close - stopFloor
                    bar.close - d * cfg.StopDistFrac
                else Double.NegativeInfinity
            let stop = if rawStop > 0.0 then rawStop else Double.NegativeInfinity
            let stopDistPct = if bar.close > 0.0 && stop > Double.NegativeInfinity then (bar.close - stop) / bar.close else nan
            let slopePerAtr =
                match sAtrLog with
                | ValueSome a when a > 0.0 && not (Double.IsNaN sPriceSlope) -> sPriceSlope / a
                | _ -> nan
            positions.Add
                { EntryMin = bar.etMin
                  EntryPx = bar.close
                  StopLevel = stop
                  StopDistPct = stopDistPct
                  PriceSlope20AtEntry = sPriceSlope
                  VolSlope20AtEntry = sVolSlope
                  LogAtr20AtEntry = (match sAtrLog with ValueSome a -> a | ValueNone -> nan)
                  Tightness20AtEntry = (match this.Tightness with ValueSome t -> t | ValueNone -> nan)
                  SlopePerAtrAtEntry = slopePerAtr
                  SumAbove6AtEntry = sSumAbove6
                  SumAbove40AtEntry = sSumAbove40
                  SumAbove60AtEntry = sSumAbove60
                  EmaVwap30AtEntry = sEmaVwap30
                  EmaVwap60AtEntry = sEmaVwap60
                  TrailVol20mAtEntry = sTrailVol20m
                  SessMaxVol20AtEntry = sSessMaxVol20
                  EmaAtEntry = (match sEmaPrev with ValueSome e -> e | ValueNone -> nan)
                  EmaMinAtEntry = (match sEmaMin with ValueSome m -> m | ValueNone -> nan)
                  VolEmaAtEntry = (match sVolEma with ValueSome v -> v | ValueNone -> nan)
                  VolEmaMinAtEntry = (match sVolEmaMin with ValueSome m -> m | ValueNone -> nan)
                  SessMaxEmaAtEntry = sSessMaxEma
                  UpdnWAtEntry = Array.copy sUpdnW
                  SessMaxLogAtrAtEntry = sSessMaxLogAtr
                  SessMinCloseAtEntry = sSessMinClose
                  SessMaxCloseAtEntry = sSessMaxClose
                  SessMaxVolAtEntry = sSessMaxVol
                  VwapAtEntry = (match sVwapNow with ValueSome v -> v | ValueNone -> nan)
                  InitVol15mAtEntry = sInitVol15m
                  TrailVol5mAtEntry = sTrailVol5m
                  EntryVsSessHighAtEntry = (match sRunHi with ValueSome h when h > 0.0 -> bar.close / h - 1.0 | _ -> nan)
                  Chg20mAtEntry = (match lagPctChange lag20 with ValueSome p -> p | ValueNone -> nan)
                  CumVolAtEntry = cumVol
                  MktChgOpenAtEntry = this.MktChgOpen bar.etMin
                  MktChgPrevAtEntry = this.MktChgPrev bar.etMin
                  Reported = reported
                  State = Holding }

    /// Flatten any still-open positions at the last folded bar's close (MOC / hold-to-close).
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
