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
      StopDistPct: float         // (entry - stopLevel)/entry at entry; nan if no finite stop
      // core feature snapshot at entry (all this-bar-inclusive — the values the gates actually read)
      PriceSlope20: float        // 20m OLS log-price slope
      LogAtr20: float            // 20m mean log-true-range
      Sum6: int                  // # of the last 6 bars closed >= the 9-EMA
      VolClimb: float            // (volEma - volEmaMin)/volEma
      EmaAtEntry: float          // the 9-EMA at entry
      VwapAtEntry: float         // session VWAP at entry
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
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, close1d: float, close3d: float) =
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

    let mutable positions : IntradayPosition ResizeArray = ResizeArray()
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

    /// Fold one minute bar into all rolling state, THEN snapshot. Features INCLUDE the current
    /// bar (it has already closed — using it is not lookahead). The one strictly-prior read is the
    /// true-range prior close (`sState.bar`) and the closes-above-EMA reference (the EMA as it stood
    /// BEFORE this bar folds), captured before their respective pushes below.
    member this.ProcessBar (curBar: MinuteBar) =
        let currentState = this.State
        // the immediately-prior bar (from the last snapshot) — the true-range prior-close reference.
        let prevBar = sState.bar
        if curBar.etMin >= cfg.FeatureStartMin then
            // session VWAP (typical price · volume) — cumulative from the 09:30 feature anchor.
            let tp = (curBar.high + curBar.low + curBar.close) / 3.0
            vwap.Push(tp * float curBar.volume, float curBar.volume)
            // true range vs the PRIOR bar's close (both linear & log ATR); needs a valid prior bar.
            match prevBar with
            | ValueSome prev when curBar.high > 0.0 && curBar.low > 0.0 && prev.close > 0.0 ->
                let pc = prev.close
                (max curBar.high pc - min curBar.low pc) |> atrLin.Push
                log (max curBar.high pc / min curBar.low pc) |> atrLog.Push
            | _ -> ()
            // closes-above-9EMA count, vs the STRICTLY-PRIOR EMA (before this bar folds).
            let prevEma = ema.State
            let aboveInd = match prevEma with ValueSome e -> (if curBar.close >= e then 1.0 else 0.0) | ValueNone -> 0.0
            sum6.Push aboveInd
            // trailing-window OLS log-price slope (the trend feature).
            priceOls.Push (log curBar.close)
            // trailing 5-bar raw-volume avg (recent-tempo numerator).
            vol5avg.Push (float curBar.volume)
            // the 9-EMA (fed AFTER the above-EMA indicator reads the prior EMA), then its 20m trailing MIN.
            ema.Push curBar.close
            ema.State |> ValueOption.iter emaLow.Push
            // volume analogue: 9-EMA of raw volume, then its 20m trailing MIN (the vol_climb base).
            volEma.Push (float curBar.volume)
            volEma.State |> ValueOption.iter volEmaMin.Push
        // advance the prior-bar pointer & counter for EVERY bar (an invalid/pre-feature bar still
        // advances wall-clock time so the ATR prior-close is the immediately-prior bar), THEN snapshot
        // the post-fold state — the entry gate reads features that INCLUDE this bar.
        bar      <- ValueSome curBar
        barsSeen <- barsSeen + 1
        sState   <- currentState


    /// Advance one open position by the current bar (immutable update). Exit precedence
    /// (first to fire wins): stop -> moc. Both fill at the bar CLOSE.
    member private this.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            // STOP: the current 9-EMA has closed BELOW the frozen stop level. Fills at the bar close.
            let stopHit = match ema.State with ValueSome e -> e < pos.StopLevel | ValueNone -> false
            if stopHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "stop") }
            elif bar.etMin >= cfg.MocMin then
                { pos with State = ExitedAt (bar.etMin, bar.close, "moc") }
            else pos

    /// The PRICE pattern — does the CURRENT (post-fold, this-bar-inclusive) feature state clear every
    /// PRICE gate? Time-window, price/ATR core, day-trend/VWAP floors. This ALONE drives arm/disarm (an
    /// armed price trigger consumes the setup whether or not volume passes) — the SMB-backside split.
    /// The volume gate is separate (VolumePasses). Any undefined feature = fail (ValueNone → false).
    member private this.PricePatternFires (bar: MinuteBar) : bool =
        let inline gate (v: float voption) (test: float -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // inside the wall-clock entry window [EntryStartMin, EntryEndMin].
        bar.etMin >= cfg.EntryStartMin
        && (cfg.EntryEndMin <= 0 || cfg.EntryEndMin >= cfg.MocMin || bar.etMin <= cfg.EntryEndMin)
        // --- price / ATR core ---
        // positive 20m OLS log-price slope (an up-trend into the entry).
        && gate priceOls.Slope (fun s -> s > 0.0)
        // log-ATR FLOOR (the main lever) + optional CEILING.
        && (cfg.MinAtrPct <= 0.0 || gate atrLog.State (fun a -> a >= cfg.MinAtrPct))
        && (Double.IsInfinity cfg.MaxAtrPct || gate atrLog.State (fun a -> a < cfg.MaxAtrPct))
        // >= N of the last 6 bars closed above the 9-EMA. 0 = off.
        && (cfg.MinCloseAbove6 <= 0 || gate sum6.State (fun c -> int (round c) >= cfg.MinCloseAbove6))
        // --- day-trend + VWAP floors ---
        // DAY-DIRECTION FLOOR (F17): entry / prev-daily-close − 1 >= MinChg1d. NaN/-inf = off.
        && (Double.IsNegativeInfinity cfg.MinChg1d || Double.IsNaN cfg.MinChg1d
            || (close1d > 0.0 && bar.close / close1d - 1.0 >= cfg.MinChg1d))
        // 3-DAY TREND FLOOR (F28): entry / close-3d-ago − 1 >= MinChg3d. NaN/-inf/no-close3d = off.
        && (Double.IsNegativeInfinity cfg.MinChg3d || Double.IsNaN cfg.MinChg3d || close3d <= 0.0
            || bar.close / close3d - 1.0 >= cfg.MinChg3d)
        // 9-EMA-vs-VWAP FLOOR (F27): ema / vwap − 1 >= MinEmaVsVwap (smoothed trend vs VWAP). NaN/-inf = off.
        && (Double.IsNegativeInfinity cfg.MinEmaVsVwap || Double.IsNaN cfg.MinEmaVsVwap
            || (match ema.State, vwap.State with
                | ValueSome e, ValueSome v -> v > 0.0 && e / v - 1.0 >= cfg.MinEmaVsVwap
                | _ -> false))

    /// The VOLUME pattern — vol_climb = (volEma − volEmaMin)/volEma >= MinVolClimb (the F32 gate). Evaluated
    /// SEPARATELY from the price pattern: a price trigger that FAILS this still disarms (consumes the setup),
    /// it just doesn't open a REAL position — so the reported set == the post-hoc price∧vol filter, LIVE,
    /// without deferring to a later/worse setup. 0 = always passes.
    member private _.VolumePasses : bool =
        cfg.MinVolClimb <= 0.0
        || (match volEma.State, volEmaMin.State with
            | ValueSome v, ValueSome m when v > 0.0 -> (v - m) / v >= cfg.MinVolClimb
            | _ -> false)

    /// Advance the whole system by one minute bar: fold the bar in, advance every open position, then
    /// (if armed and the pattern fires) open ONE new position. The re-arm check runs LAST so we never
    /// arm and enter on the same bar.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar positions.[i]
        if armed && this.PricePatternFires bar then
            // STOP = the CURRENT 20m-min-of-EMA (this-bar-inclusive). The EMA-stop fires when the live
            // 9-EMA later drops below this level. REQUIRE room: the stop must be finite AND strictly below
            // the current 9-EMA (else the position is born stopped-out / the stop is meaningless). No room
            // ⇒ SKIP the trade but STILL disarm (consume the setup until the next low re-arms).
            let stop =
                match emaLow.State with
                | ValueSome m when m > 0.0 -> m
                | _ -> Double.NegativeInfinity
            let hasRoom =
                stop > Double.NegativeInfinity
                && (match ema.State with ValueSome e -> stop < e | ValueNone -> false)
            // SMB-backside split: the PRICE pattern (+room) opens a REAL position ONLY IF volume also passes;
            // a volume FAIL still disarms (consumes the setup) so we never defer to a later, worse setup.
            if hasRoom && this.VolumePasses then
                let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
                positions.Add
                    { EntryMin = bar.etMin
                      EntryPx = bar.close
                      StopLevel = stop
                      StopDistPct = (if bar.close > 0.0 then (bar.close - stop) / bar.close else nan)
                      PriceSlope20 = vv priceOls.Slope
                      LogAtr20 = vv atrLog.State
                      Sum6 = (match sum6.State with ValueSome c -> int (round c) | ValueNone -> 0)
                      VolClimb =
                        (match volEma.State, volEmaMin.State with
                         | ValueSome v, ValueSome m when v > 0.0 -> (v - m) / v
                         | _ -> nan)
                      EmaAtEntry = vv ema.State
                      VwapAtEntry = vv vwap.State
                      State = Holding }
            // taken, vol-skipped, OR no-room: disarm until the next low re-arms.
            armed <- false
        // RE-ARM LAST: the current 9-EMA has dropped below the PRIOR 20m-min-of-EMA (a fresh low was made).
        // Placed at the END so a re-arm on this bar can only enable an entry on a LATER bar.
        match ema.State, sState.emaLow with
        | ValueSome e, ValueSome m when e < m -> armed <- true
        | _ -> ()



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
