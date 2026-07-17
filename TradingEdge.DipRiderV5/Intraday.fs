module TradingEdge.DipRiderV5.Intraday

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

/// A "bars-since-event" countup timer with a latch. State: -1 = waiting (event not yet seen since the last
/// Reset); 0 = the bar the event FIRST fired; >=0 = bars since. `Start` latches -1->0 ONCE (idempotent while
/// already >=0, so a repeated event doesn't re-latch); `Step` increments while >=0 (no-op at -1); `Reset`
/// returns to -1. Read `.Value` (>=0 once armed) or `.Fired` (Value >= 0).
[<Sealed>]
type BreakoutTimer() =
    let mutable v = -1
    /// The current count: -1 = waiting, 0 = fired this bar, +N = N bars since the event.
    member _.Value = v
    /// Has the event fired since the last Reset? (Value >= 0.)
    member _.Fired = v >= 0
    /// Latch the event: -1 -> 0. No effect once already >= 0 (fire once per Reset).
    member _.Start () = if v < 0 then v <- 0
    /// Advance one bar: +1 while counting (>= 0). No effect while waiting (-1).
    member _.Step () = if v >= 0 then v <- v + 1
    /// Return to the waiting state (-1).
    member _.Reset () = v <- -1

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
      Tightness20: float         // (rangeHigh-rangeLow)/atrLin
      Rvol5m: float              // trailing-5m avg vol / permin15m (LIVE-SAFE exhaustion ratio; V4 used the contaminated permin20d); nan if no baseline
      Sum6: int                  // # of the last 6 bars closed >= the 9-EMA
      VolClimb: float            // (volEma - volEmaMin)/volEma
      EmaAtEntry: float          // the 9-EMA at entry
      VwapAtEntry: float         // session VWAP at entry
      // breakout features (recorded for the breakout experiment + post-hoc)
      BarsSinceBreakout: int     // -1 waiting / 0 breakout bar / +N bars since; the session-high countdown
      BarsSince20mBreakout: int  // same, for the 20m-EMA-high breakout
      BarsSince60mBreakout: int  // same, for the 60m-EMA-high breakout
      SessEmaHigh: float         // session-max 9-EMA at entry
      LaggedSessEmaHigh10m: float // session-EMA-high as of 10m ago (post-hoc breakout-continuation feature)
      VolBasisAtEntry: float     // the VOL-STOP BASIS frozen at entry: the volume MA (9-EMA or 20m-avg per
                                 // cfg.VolStopUseAvg20) as of the entry bar. nan if not warm. The scalp exit
                                 // fires when the live volume MA falls below VolStopFrac × this.
      State: IntraPosState }   // immutable — advancing a position returns a NEW record (HighFlyer style)

/// The RE-ARM level: what the live 9-EMA must drop below to re-arm the setup (after a disarm). Three
/// choices, compared as an axis of the F2 experiment.
type ReArmMode =
    | RollingEmaLow          // live 9-EMA < the ROLLING 20m-min-of-EMA (the original; the min slides UP as
                             // bars leave the window, so it re-arms easily — the diagnosed over-firing default).
    | SessionEmaLow          // live 9-EMA < the SESSION-min of the 9-EMA (running min from the session anchor;
                             // only ratchets DOWN as new EMA lows are made — a genuinely non-sliding level).
    | LastStopLevel          // live 9-EMA < the FROZEN stop level of the last CONSUMED setup (frozen on EVERY
                             // disarm: taken, vol-skip, or no-room). Before the first setup fires: stay armed.

/// Intraday engine config — DipRiderV3 (pure trailing-window momentum).
type IntradayConfig =
    {
        ReArm: ReArmMode         // which level the 9-EMA must break below to re-arm (default RollingEmaLow).
        MaxConcurrent: int        // cap on concurrently-OPEN (Holding) positions. 0 = unlimited (the pure arm/
                                 // re-arm book — a re-arm can fire a new entry while a prior one is still open).
                                 // 1 = block BOTH entry and re-arm while a position is Holding (the V3Backside
                                 // slot-lifetime discipline: the slot frees only when the position exits).
        VolAsGate: bool          // false (default) = SKIP mode: a price trigger disarms regardless; a REAL
                                 // position opens only if vol_climb passes (the SMB-backside split). true =
                                 // GATE mode: vol_climb is ANDed into the trigger — the setup only fires (and
                                 // only disarms) when BOTH price AND volume pass.
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
        MinTightness: float      // require tightness = (rangeHigh−rangeLow)/atrLin >= this (a real range, not a
                                 // lethargic name). Default 3.0 (V3Backside). 0 = off.
        MaxRvol5m15m: float      // EXHAUSTION CUT — the LIVE-SAFE V5 rebuild of V4's MaxRvol5m20d. Reject if the
                                 // trailing-5m vol NUMERATOR >= this × permin15m (D's OWN 09:30-09:45 mean 1m
                                 // volume). V4 divided by permin20d = avgvol20/390, and avgvol20 INCLUDES D's own
                                 // full-session volume ⇒ LOOKAHEAD (docs/lookahead_protocol.md R2). permin15m is
                                 // complete at 09:45 = EntryStartMin ⇒ legal. Default 0 = OFF: V4's threshold of
                                 // 100 was fitted against the contaminated denominator and does NOT carry over —
                                 // the scale is completely different. Re-tune from scratch.
        VolStopFrac: float       // ⭐ THE SCALP EXIT (ported from BreakoutTimer F14, user 2026-07-17). While
                                 // HOLDING, exit at the bar close once the live volume MA has fallen below
                                 // VolStopFrac × the volume MA AS OF ENTRY. "The thrust that started this move
                                 // is over; take the scalp." 0.667 = exit at ⅔ of the entry's volume pace.
                                 // 0 = OFF (hold to MOC — the V4 behavior).
                                 //
                                 // ⚠ Uses an AVERAGE (9-EMA, or the 20m AvgMa when VolStopUseAvg20), NOT a raw
                                 // 20-bar SUM as BreakoutTimer did. A raw sum is WARMUP-DEPENDENT: a sparse name
                                 // that reaches 10:00 with <20 bars folded (no trades ⇒ no bar) gets an
                                 // artificially SMALL basis, so the ratio against it is garbage and the exit
                                 // misfires. AvgMa divides by the ACTUAL bar count and EmaMa self-normalizes, so
                                 // both are warmup-safe. (user, 2026-07-17)
                                 //
                                 // LOOKAHEAD-FREE: both sides are D's OWN realized bars — no avgvol20 anywhere.
        VolStopUseAvg20: bool    // vol-stop BASIS. false (default) = the 9-EMA of raw volume (volEma) — the same
                                 // series vol_climb uses, fast-reacting. true = the 20m AvgMa of raw volume —
                                 // smoother/slower, closer to BreakoutTimer's original trailing-20m shape.
        Rvol5mUseMax: bool       // false (default) = numerator is the trailing-5m AVG 1m-vol (V3Backside). true =
                                 // the trailing-5m MAX 1m-vol (the spiky signal the SHORT book uses). Since max>=avg,
                                 // the same threshold cuts MORE with max — sweep the threshold when comparing.
        // ----- breakout mode (BreakoutTimer fused; reset = the rolling-20m-low re-arm) -----
        MaxBarsSinceBreakout: int  // BREAKOUT GATE: require 0 <= barsSinceBreakout < this (the 9-EMA broke to a new
                                   // session high within the last N bars). 0 = OFF (default). BreakoutTimer used 10.
        MaxBarsSince20mBreakout: int // 20m-EMA-BREAKOUT GATE: require 0 <= bars-since-20m-EMA-high < this (the 9-EMA
                                   // broke above its trailing-20m max within the last N bars). 0 = OFF (default).
        MaxBarsSince60mBreakout: int // 60m-EMA-BREAKOUT GATE: same, over the trailing-60m EMA max. 0 = OFF (default).
        BreakoutOr: bool         // false (default) = the enabled breakout gates AND together; true = OR (pass if
                                 // ANY enabled window is within its countdown). N=0 windows are excluded either way.
        // OR-mode PER-WINDOW vol_climb floors (F14 A-book): in OR mode each window's branch ALSO requires
        // vol_climb >= its own floor — the looser the structure, the higher the volume bar it must clear.
        // Only used when BreakoutOr=true; ignored otherwise (the global MinVolClimb still governs AND/non-breakout).
        BreakoutVcSession: float // session-high branch: vol_climb floor (default 0).
        BreakoutVc60m: float     // 60m-high branch: vol_climb floor.
        BreakoutVc20m: float     // 20m-high branch: vol_climb floor.
        DisablePriceSlope: bool  // true = drop the price-slope>0 gate (BreakoutTimer didn't use it). Default false.
        DisableSum6: bool        // true = drop the sum6 gate regardless of MinCloseAbove6 (BreakoutTimer didn't
                                 // use it). Default false.
        // ----- stop-distance floor (F17: larger stops help; require the entry ran >= this fraction above its
        // own 20m-EMA-low, i.e. the frozen stop sits >= MinStopDistPct below entry). 0 = OFF. -----
        MinStopDistPct: float    // require (entry - stop)/entry >= this. Default 0 (OFF); F17 knee ~0.03.
        StopDistAsGate: bool     // true = GATE: AND the floor into the trigger — a too-tight setup does NOT
                                 // fire and does NOT disarm (it waits, re-firing when the distance widens).
                                 // false = SKIP: the setup fires and DISARMS (consumes the arm) but opens NO
                                 // position — passes on the trade entirely. Mirrors the vol gate-vs-skip axis.
      }

type State = 
    {
        bar  : MinuteBar voption
        vwap : float voption
        ema : float voption
        emaLow : float voption
        atrLin : float voption
        atrLog : float voption
        tightness : float voption   // (rangeHigh−rangeLow)/atrLin
        vol5avg : float voption     // trailing-5-bar mean raw volume (exhaustion-cut numerator)
        sum6 : int voption
        sPriceSlope : float voption
        volEma : float voption
        volEmaMin : float voption
        sessEmaLow : float voption   // session-min of the 9-EMA (SessionEmaLow re-arm reference)
        lastStopLevel : float voption // frozen stop of the last consumed setup (LastStopLevel re-arm reference)
        sessEmaHigh : float voption  // session-max of the 9-EMA (the breakout reference high)
        barsSinceBreakout : int      // -1 waiting; 0 = first new-high bar; +1/bar after (latch once per reset)
        laggedSessEmaHigh : float voption  // the session-EMA-high as of 10m ago (record-only, post-hoc)
        barsSeen : int
        armed : bool
    }

/// Per-(ticker, day) intraday engine. Feed it the day's RTH MinuteBar[] in time
/// order via `Process`, then `Flatten` and read `Positions`.
/// `permin15m` = D's OWN mean 1m volume over 09:30-09:45 (vol_0945 / nbar_0945), the LIVE-SAFE per-minute
/// volume baseline. It replaces V4's `permin20d` (= avgvol20/390), which was a LOOKAHEAD: avgvol20 is a
/// CURRENT-ROW-inclusive rolling average, so it contained D's own full-session volume. permin15m is complete
/// at 09:45 and EntryStartMin = 10:00, so it clears the knowability clock (docs/lookahead_protocol.md R3).
/// ⚠ That alignment is LOAD-BEARING: drop EntryStartMin below 09:45 and this becomes a lookahead.
type IntradaySystem(cfg: IntradayConfig, ticker: string, day: DateOnly, close1d: float, close3d: float, permin15m: float) =
    // ----- rolling intraday structures (1m timeframe; all fed ONLY from the 09:30 feature anchor) -----
    let atrLog    = AvgMa(cfg.VolWindow)        // mean LOG true range over the last VolWindow bars (the volatility feature)
    let atrLin    = AvgMa(cfg.VolWindow)        // mean ABSOLUTE true range (linear) — the tightness DENOMINATOR
    let rangeHigh = MaxMa(cfg.VolWindow)        // 20m window high (tightness NUMERATOR)
    let rangeLow  = MinMa(cfg.VolWindow)        // 20m window low  (tightness NUMERATOR)
    let ema       = EmaMa(cfg.EmaPeriod)        // the 9-EMA (closes-above-EMA reference)
    let emaLow    = MinMa(cfg.VolWindow)        // the 20m trailing MIN of the 9-EMA — the ema_climb feature denominator base
    let emaHigh   = MaxMa(cfg.VolWindow)        // the 20m trailing MAX of the 9-EMA — the 20m-EMA-breakout reference
    let emaHigh60 = MaxMa(60)                    // the 60m trailing MAX of the 9-EMA — the 60m-EMA-breakout reference
    let volEma    = EmaMa(cfg.EmaPeriod)        // 9-EMA of raw 1m VOLUME — the volume analogue of the price 9-EMA
    let volEmaMin = MinMa(cfg.VolWindow)        // 20m trailing MIN of the volume-9-EMA — the vol_climb base (mirrors emaMin)
    // 20m trailing AVERAGE of raw 1m volume — the alternative vol-stop basis (cfg.VolStopUseAvg20).
    // AvgMa (NOT SumMa): AvgMa.State divides by the LIVE bar Count, so it is warmup-safe. A raw 20-bar
    // SUM would be artificially small for a sparse name that reaches 10:00 with <20 bars folded.
    let vol20avg  = AvgMa(cfg.VolWindow)
    let sum6    = SumMa(6)
    // trailing-window OLS slopes (fixed VolWindow bars, fed every VALID feature-bar). Log-close = trend slope,
    // log-volume = the volume-trend slope (the F14 lever). Both pushed TOGETHER only when both logs are valid,
    // so they stay on the identical push-index x-axis (directly comparable).
    let priceOls  = OlsSlopeMa(cfg.VolWindow)
    // trailing 5-bar raw VOLUME avg — the recent-tempo numerator for the exhaustion cut (5m-avg vs the 15m
    // opening-avg and the 20d per-minute avg). Fed every VALID feature-bar.
    let vol5avg   = AvgMa(5)
    let vol5max   = MaxMa(5)                    // trailing-5-bar MAX 1m volume (the spiky alt exhaustion numerator)

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
            tightness = ValueNone
            vol5avg = ValueNone
            sum6 = ValueNone
            sPriceSlope = ValueNone
            volEma = ValueNone
            volEmaMin = ValueNone
            sessEmaLow = ValueNone
            lastStopLevel = ValueNone
            sessEmaHigh = ValueNone
            barsSinceBreakout = -1
            laggedSessEmaHigh = ValueNone
            barsSeen = 0
            armed = true
        }

    let mutable positions : IntradayPosition ResizeArray = ResizeArray()
    let mutable armed = true
    let mutable bar = ValueNone
    let mutable barsSeen = 0
    // SESSION-min of the 9-EMA (running min from the session anchor; only ratchets DOWN) — the SessionEmaLow
    // re-arm reference. Updated post-fold in ProcessBar.
    let mutable sessEmaLow : float voption = ValueNone
    // the FROZEN stop level of the last CONSUMED setup (set on EVERY disarm) — the LastStopLevel re-arm
    // reference. ValueNone until the first setup fires (day stays armed until then).
    let mutable lastStopLevel : float voption = ValueNone
    // ----- BREAKOUT features (BreakoutTimer fused into V4; reset = the rolling-20m-low re-arm) -----
    // SESSION-max of the 9-EMA (running max, ratchets UP) — the breakout reference high.
    let mutable sessEmaHigh : float voption = ValueNone
    // BARS-SINCE-INITIAL-BREAKOUT timer: -1 waiting / 0 first new session-EMA-high after reset / +1 each bar.
    // Latches once per reset (Start is idempotent once >=0). Reset by the 20m-low re-arm.
    let breakoutTimer = BreakoutTimer()
    // 20m-9EMA-high breakout timer — mirror of breakoutTimer but the event is a fresh TRAILING-20m EMA high
    // (ema > prior MaxMa(20) of ema), not a session high. Same reset cycle (the 20m-low re-arm).
    let ema20BreakoutTimer = BreakoutTimer()
    // 60m-9EMA-high breakout timer — same, over a 60-bar trailing max.
    let ema60BreakoutTimer = BreakoutTimer()
    // 10m-lagged session-EMA-high (record only, for post-hoc). Delay line of the post-fold sessEmaHigh.
    let laggedSessEmaHigh = LagMa<float>(10)

    member _.Ticker = ticker
    member _.Day = day

    /// Count of currently-open (Holding) positions — the concurrency denominator.
    member _.OpenCount =
        let mutable n = 0
        for p in positions do (match p.State with Holding -> n <- n + 1 | ExitedAt _ -> ())
        n

    /// A free concurrency slot? MaxConcurrent<=0 = unlimited (always free).
    member this.HasSlot = cfg.MaxConcurrent <= 0 || this.OpenCount < cfg.MaxConcurrent

    /// The trailing-5m exhaustion NUMERATOR — the 5-bar MAX (spiky) or AVG 1m volume per cfg.Rvol5mUseMax.
    member _.Vol5Num : float voption = if cfg.Rvol5mUseMax then vol5max.State else vol5avg.State

    /// The VOL-STOP series — the volume MA the scalp exit reads, per cfg.VolStopUseAvg20.
    /// BOTH are AVERAGES, so both are warmup-safe (AvgMa divides by the live bar Count; EmaMa
    /// self-normalizes). Deliberately NOT a raw rolling SUM — that is warmup-dependent, and a sparse
    /// name reaching 10:00 with <20 bars folded would get an artificially small basis.
    member _.VolStopSeries : float voption = if cfg.VolStopUseAvg20 then vol20avg.State else volEma.State

    /// Intraday consolidation tightness = (20m window high − low) / linear-ATR. ValueNone until warm.
    member _.Tightness : float voption =
        match rangeHigh.State, rangeLow.State, atrLin.State with
        | ValueSome hi, ValueSome lo, ValueSome atr when atr <> 0.0 -> ValueSome ((hi - lo) / atr)
        | _ -> ValueNone

    member this.State : State =
        {
            bar = bar
            vwap = vwap.State
            ema = ema.State
            emaLow = emaLow.State
            atrLin = atrLin.State
            atrLog = atrLog.State
            tightness = this.Tightness
            vol5avg = vol5avg.State
            sum6 = ValueOption.map (round >> int) sum6.State
            sPriceSlope = priceOls.Slope
            volEma = volEma.State
            volEmaMin = volEmaMin.State
            sessEmaLow = sessEmaLow
            lastStopLevel = lastStopLevel
            sessEmaHigh = sessEmaHigh
            barsSinceBreakout = breakoutTimer.Value
            laggedSessEmaHigh = laggedSessEmaHigh.Lagged
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
            // 20m window high/low (tightness numerator) — pushed every valid feature-bar (like V3Backside).
            rangeHigh.Push curBar.high
            rangeLow.Push  curBar.low
            // closes-above-9EMA count, vs the STRICTLY-PRIOR EMA (before this bar folds).
            let prevEma = ema.State
            let aboveInd = match prevEma with ValueSome e -> (if curBar.close >= e then 1.0 else 0.0) | ValueNone -> 0.0
            sum6.Push aboveInd
            // trailing-window OLS log-price slope (the trend feature).
            priceOls.Push (log curBar.close)
            // trailing 5-bar raw-volume avg (recent-tempo numerator).
            vol5avg.Push (float curBar.volume)
            vol5max.Push (float curBar.volume)
            // the 9-EMA (fed AFTER the above-EMA indicator reads the prior EMA), then its 20m trailing MIN.
            ema.Push curBar.close
            ema.State |> ValueOption.iter emaLow.Push
            // SESSION-min of the 9-EMA (only ratchets down) — the SessionEmaLow re-arm reference.
            ema.State |> ValueOption.iter (fun e ->
                sessEmaLow <- match sessEmaLow with ValueSome m -> ValueSome (min m e) | ValueNone -> ValueSome e)
            // ----- BREAKOUT features -----
            ema.State |> ValueOption.iter (fun e ->
                // SESSION-EMA-high breakout: new high vs the PRIOR session high (captured before we update it).
                let isNewSessHigh = match sessEmaHigh with ValueSome h -> e > h | ValueNone -> true
                sessEmaHigh <- match sessEmaHigh with ValueSome h -> ValueSome (max h e) | ValueNone -> ValueSome e
                // Step FIRST (increment while counting), THEN Start on a new high (latch -1->0 once per reset).
                breakoutTimer.Step()
                if isNewSessHigh then breakoutTimer.Start()
                // 20m-EMA-high breakout: new high vs the PRIOR trailing-20m EMA max (captured before the push).
                let isNew20mHigh = match emaHigh.State with ValueSome h -> e > h | ValueNone -> true
                emaHigh.Push e
                ema20BreakoutTimer.Step()
                if isNew20mHigh then ema20BreakoutTimer.Start()
                // 60m-EMA-high breakout: same, over the trailing-60m EMA max.
                let isNew60mHigh = match emaHigh60.State with ValueSome h -> e > h | ValueNone -> true
                emaHigh60.Push e
                ema60BreakoutTimer.Step()
                if isNew60mHigh then ema60BreakoutTimer.Start()
                // 10m-lagged session-EMA-high (record-only). Push the post-fold session high.
                match sessEmaHigh with ValueSome h -> laggedSessEmaHigh.Push h | ValueNone -> ())
            // volume analogue: 9-EMA of raw volume, then its 20m trailing MIN (the vol_climb base).
            volEma.Push (float curBar.volume)
            volEma.State |> ValueOption.iter volEmaMin.Push
            vol20avg.Push (float curBar.volume)   // the 20m-avg vol-stop basis (warmup-safe)
        // advance the prior-bar pointer & counter for EVERY bar (an invalid/pre-feature bar still
        // advances wall-clock time so the ATR prior-close is the immediately-prior bar), THEN snapshot
        // the post-fold state — the entry gate reads features that INCLUDE this bar.
        bar      <- ValueSome curBar
        barsSeen <- barsSeen + 1
        sState   <- currentState


    /// Advance one open position by the current bar (immutable update). Exit precedence
    /// (first to fire wins): stop -> vol_stop -> moc. All fill at the bar CLOSE.
    member private this.Advance (bar: MinuteBar) (pos: IntradayPosition) : IntradayPosition =
        match pos.State with
        | ExitedAt _ -> pos
        | Holding ->
            // STOP: the current 9-EMA has closed BELOW the frozen stop level. Fills at the bar close.
            let stopHit = match ema.State with ValueSome e -> e < pos.StopLevel | ValueNone -> false
            // ⭐ VOL-STOP (the SCALP EXIT): the live volume MA has fallen below VolStopFrac × the volume MA
            // frozen at entry — the thrust that drove the move has faded, so bank the scalp. Fills at the
            // bar close. Both sides are the SAME series (9-EMA of volume, or the 20m AvgMa), so the ratio is
            // dimensionless and warmup-safe. LOOKAHEAD-FREE: D's own realized bars only.
            let volStopHit =
                cfg.VolStopFrac > 0.0
                && pos.VolBasisAtEntry > 0.0 && not (Double.IsNaN pos.VolBasisAtEntry)
                && (match this.VolStopSeries with
                    | ValueSome v -> v < cfg.VolStopFrac * pos.VolBasisAtEntry
                    | ValueNone -> false)
            if stopHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "stop") }
            elif volStopHit then
                { pos with State = ExitedAt (bar.etMin, bar.close, "vol_stop") }
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
        // positive 20m OLS log-price slope (an up-trend into the entry). --no-price-slope drops it (BreakoutTimer).
        && (cfg.DisablePriceSlope || gate priceOls.Slope (fun s -> s > 0.0))
        // log-ATR FLOOR (the main lever) + optional CEILING.
        && (cfg.MinAtrPct <= 0.0 || gate atrLog.State (fun a -> a >= cfg.MinAtrPct))
        && (Double.IsInfinity cfg.MaxAtrPct || gate atrLog.State (fun a -> a < cfg.MaxAtrPct))
        // >= N of the last 6 bars closed above the 9-EMA. 0 = off; --no-sum6 drops it (BreakoutTimer).
        && (cfg.DisableSum6 || cfg.MinCloseAbove6 <= 0 || gate sum6.State (fun c -> int (round c) >= cfg.MinCloseAbove6))
        // BREAKOUT GATES (session / 20m / 60m EMA-high). Each: 0 <= timer < N, reset by the 20m-low re-arm.
        // A gate with MaxBars...=0 is OFF (excluded). BreakoutOr=false (default): the ENABLED gates AND together
        // (all must pass). BreakoutOr=true: the enabled gates OR together (pass if ANY enabled window is within
        // its countdown) — with N=0 excluded, so "session || 60m" enables only those two.
        && (let inline g (n: int) (t: BreakoutTimer) = n > 0 && t.Fired && t.Value < n   // this window PASSES
            let inline en (n: int) = n > 0                                               // this window is ENABLED
            let sess = g cfg.MaxBarsSinceBreakout    breakoutTimer
            let b20  = g cfg.MaxBarsSince20mBreakout ema20BreakoutTimer
            let b60  = g cfg.MaxBarsSince60mBreakout ema60BreakoutTimer
            let anyEnabled = en cfg.MaxBarsSinceBreakout || en cfg.MaxBarsSince20mBreakout || en cfg.MaxBarsSince60mBreakout
            if not anyEnabled then true                              // no breakout gate configured — pass
            elif cfg.BreakoutOr then                                 // OR: any enabled window within countdown AND
                                                                    // clearing ITS OWN per-window vol_climb floor
                (sess && this.VolClimbAtLeast cfg.BreakoutVcSession)
                || (b60 && this.VolClimbAtLeast cfg.BreakoutVc60m)
                || (b20 && this.VolClimbAtLeast cfg.BreakoutVc20m)
            else                                                    // AND: every ENABLED window must pass
                (not (en cfg.MaxBarsSinceBreakout)    || sess)
                && (not (en cfg.MaxBarsSince20mBreakout) || b20)
                && (not (en cfg.MaxBarsSince60mBreakout) || b60))
        // TIGHTNESS FLOOR: (rangeHigh−rangeLow)/atrLin >= MinTightness (a real range, not lethargic). 0 = off.
        && (cfg.MinTightness <= 0.0 || gate this.Tightness (fun t -> t >= cfg.MinTightness))
        // EXHAUSTION CUT — ⛔ DELETED IN V5 (LOOKAHEAD). V4 gated on `Vol5Num < MaxRvol5m20d * permin20d`
        // where permin20d = avgvol20/390, and avgvol20 is `ROWS BETWEEN 19 PRECEDING AND CURRENT ROW` — it
        // CONTAINS D's own full-session volume, unknowable at a 10:00 entry. Identical to the brv20d
        // denominator bug that left MaxFlyerV3 unconfirmed. See docs/lookahead_protocol.md R2.
        // The LIVE-SAFE twin is the 09:45-baselined cut below (permin15m = D's own 09:30-09:45 mean 1m vol,
        // complete at 09:45 = EntryStartMin). Same idea, legal. 0 = off (the V5 default: re-tune from scratch,
        // since every V4 threshold was fitted against the contaminated denominator).
        && (cfg.MaxRvol5m15m <= 0.0 || permin15m <= 0.0
            || gate this.Vol5Num (fun v -> v < cfg.MaxRvol5m15m * permin15m))
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

    /// The current vol_climb = (volEma − volEmaMin)/volEma, or nan if not warm. Used for the OR-mode per-window
    /// volume floors (F14). A floor of 0 always passes (vol_climb ∈ [0,1)).
    member private _.VolClimbNow : float =
        match volEma.State, volEmaMin.State with
        | ValueSome v, ValueSome m when v > 0.0 -> (v - m) / v
        | _ -> nan
    member private this.VolClimbAtLeast (floor: float) : bool =
        floor <= 0.0 || (let vc = this.VolClimbNow in not (Double.IsNaN vc) && vc >= floor)

    /// Advance the whole system by one minute bar: fold the bar in, advance every open position, then
    /// (if armed and the pattern fires) open ONE new position. The re-arm check runs LAST so we never
    /// arm and enter on the same bar.
    member this.Process (bar: MinuteBar) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Advance bar positions.[i]
        // A setup fires when the PRICE pattern clears. In SKIP mode (VolAsGate=false) the volume check does
        // NOT block the trigger (it only decides real-vs-skip); in GATE mode it is ANDed in, so a vol-fail
        // neither opens nor disarms.
        // STOP = the CURRENT 20m-min-of-EMA (this-bar-inclusive), and the stop DISTANCE below the entry close.
        // Computed up-front because the stop-distance floor (F17) can gate the trigger (GATE mode).
        let stop =
            match emaLow.State with
            | ValueSome m when m > 0.0 -> m
            | _ -> Double.NegativeInfinity
        let stopDistPct = if bar.close > 0.0 && stop > Double.NegativeInfinity then (bar.close - stop) / bar.close else nan
        // stop-distance floor: does the entry sit >= MinStopDistPct above its 20m-EMA-low? 0 = always passes.
        let stopDistOk = cfg.MinStopDistPct <= 0.0 || (not (Double.IsNaN stopDistPct) && stopDistPct >= cfg.MinStopDistPct)
        let volOk = this.VolumePasses
        // GATE mode for the stop-distance floor: AND it into the trigger, so a too-tight setup does NOT fire
        // (and does NOT disarm — it waits, re-firing when the distance widens). SKIP mode leaves the trigger
        // alone and applies the floor at the fill point (disarm + open no position). Same axis as VolAsGate.
        let triggerFires =
            this.PricePatternFires bar
            && (not cfg.VolAsGate || volOk)
            && (not cfg.StopDistAsGate || stopDistOk)
        let mutable disarmedThisBar = false
        // A free concurrency slot is required to consume a setup. Under mc=1 this blocks a new trigger while a
        // position is still Holding — the V3Backside slot-lifetime discipline (the slot IS the arm gate).
        if armed && triggerFires && this.HasSlot then
            // REQUIRE room: the stop must be finite AND strictly below the current 9-EMA (else the position is
            // born stopped-out / the stop is meaningless). No room ⇒ SKIP the trade but STILL disarm (consume
            // the setup until the re-arm level is breached).
            let hasRoom =
                stop > Double.NegativeInfinity
                && (match ema.State with ValueSome e -> stop < e | ValueNone -> false)
            // Open a REAL position when there is room, volume passes, AND (SKIP mode) the stop-distance floor
            // clears. In GATE mode stopDistOk is already part of triggerFires (so it's true here); in SKIP mode
            // a floor-fail here skips the trade but the disarm below still consumes the setup.
            if hasRoom && volOk && (cfg.StopDistAsGate || stopDistOk) then
                let vv (v: float voption) = match v with ValueSome x -> x | ValueNone -> nan
                positions.Add
                    { EntryMin = bar.etMin
                      EntryPx = bar.close
                      StopLevel = stop
                      StopDistPct = stopDistPct
                      PriceSlope20 = vv priceOls.Slope
                      LogAtr20 = vv atrLog.State
                      Tightness20 = (match this.Tightness with ValueSome t -> t | ValueNone -> nan)
                      Rvol5m = (match this.Vol5Num with ValueSome v when permin15m > 0.0 -> v / permin15m | _ -> nan)
                      Sum6 = (match sum6.State with ValueSome c -> int (round c) | ValueNone -> 0)
                      VolClimb =
                        (match volEma.State, volEmaMin.State with
                         | ValueSome v, ValueSome m when v > 0.0 -> (v - m) / v
                         | _ -> nan)
                      EmaAtEntry = vv ema.State
                      VwapAtEntry = vv vwap.State
                      BarsSinceBreakout = breakoutTimer.Value
                      BarsSince20mBreakout = ema20BreakoutTimer.Value
                      BarsSince60mBreakout = ema60BreakoutTimer.Value
                      SessEmaHigh = (match sessEmaHigh with ValueSome h -> h | ValueNone -> nan)
                      LaggedSessEmaHigh10m = (match laggedSessEmaHigh.Lagged with ValueSome h -> h | ValueNone -> nan)
                      // Freeze the VOL-STOP BASIS: the volume MA as of THIS (the entry) bar, post-fold.
                      // The scalp exit later compares the live series against VolStopFrac × this.
                      VolBasisAtEntry = (match this.VolStopSeries with ValueSome v -> v | ValueNone -> nan)
                      State = Holding }
            // consumed (taken, vol-skipped, or no-room): disarm + freeze the stop level for the LastStopLevel
            // re-arm. Freeze on EVERY disarm, even a no-room one (record the emaLow it WOULD have used).
            armed <- false
            disarmedThisBar <- true
            lastStopLevel <- (if stop > Double.NegativeInfinity then ValueSome stop else emaLow.State)
        // RE-ARM LAST (only on a bar we did NOT just disarm on, so a re-arm can only enable a LATER entry):
        // the current 9-EMA has dropped BELOW the re-arm reference level. The reference is one of:
        //   RollingEmaLow  — the ROLLING 20m-min-of-EMA (prior snapshot; slides up as bars leave the window)
        //   SessionEmaLow  — the SESSION-min of the 9-EMA (ratchets down only)
        //   LastStopLevel  — the frozen stop level of the last consumed setup (ValueNone ⇒ no re-arm yet)
        // Also require a free slot to re-arm: under mc=1 the setup cannot re-arm while a position is still
        // Holding, so the slot's lifetime (entry → exit) throttles the book (matches V3Backside).
        if not disarmedThisBar && this.HasSlot then
            let reArmRef =
                match cfg.ReArm with
                | RollingEmaLow -> sState.emaLow
                | SessionEmaLow -> sState.sessEmaLow
                | LastStopLevel -> sState.lastStopLevel
            match ema.State, reArmRef with
            | ValueSome e, ValueSome m when e < m ->
                armed <- true
                // RESET all breakout timers: a fresh re-arm starts a new breakout-wait cycle (-1 = waiting).
                breakoutTimer.Reset()
                ema20BreakoutTimer.Reset()
                ema60BreakoutTimer.Reset()
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
