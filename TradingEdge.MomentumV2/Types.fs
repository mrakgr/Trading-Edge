module TradingEdge.MomentumV2.Types

open System
open TradingEdge.MomentumV2.RollingMa

type Bar =
    { date: DateOnly
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// Life-cycle of a single trip.
///   - Holding: still in the trade, watching for a stop / expansion trigger.
///   - ExitingLimit barsRested: a STOP fired; a SELL LIMIT now rests at the
///     prior bar's N-day high (`TrailHigh`, excludes the current bar). It fills
///     only when bar.high reaches that level — a real resting order that can
///     miss. `barsRested` counts how many bars it has rested; once it reaches
///     `exitTimeCap` without filling, the trade exits at the next bar's open
///     (timeout). No down-only ratchet, no current-bar fill — that was the
///     unrealistic top-tick credit.
///   - ExitingMarket reason: sell at the next bar's OPEN (expansion exits, the
///     cap=0 baseline stop exit, and limit timeouts all route here).
///   - Exited: closed for any reason; never updated again.
type PositionState =
    | Holding
    | ExitingLimit of barsRested: int
    | ExitingMarket of reason: string
    | Exited of exitDate: DateOnly * exitPrice: float * reason: string

/// One trip. EntryDate/EntryPrice and the *_at_entry metrics are fixed at open;
/// State carries the rest of the life-cycle.
type Position =
    { EntryDate: DateOnly
      EntryPrice: float
      EntryDayLow: float        // Qullamaggie initial-stop floor (entry bar's low)
      // metrics snapshotted at the entry bar (for the trips CSV / parity diff)
      EntryVolume: int64
      RvolAtEntry: float
      AvgDollarVolumeAtEntry: float
      PctUpAtEntry: float
      AtrPctAtEntry: float
      TightnessAtEntry: float
      State: PositionState }

/// In-engine entry filter thresholds. Mirrors v0's `is_entry` (breakout / rvol
/// band / history / ADV / 52w-proximity) PLUS the production post-hoc filters
/// the user wants applied in-engine here (price floor / tightness / ATR%).
/// breadth_lag1 is NOT here — it is market-wide and applied post-hoc.
type EntryConfig =
    { UpThreshold: float        // min same-day return, close/prevClose-1 (v0 0.05)
      RvolMin: float            // min rvol, volume/avgVol4w (production 6.0)
      RvolMax: float            // max rvol (production 20.0)
      MinPriorDays: int         // require barsSeen > this (v0 21)
      MinAvgDollarVolume: float // min avg dollar volume 4w (v0 100_000)
      Min52wPct: float          // close >= this * hiClose (production 0.95)
      MinPrice: float           // close >= this (production 5.0)
      MaxTightness: float       // tightness < this (production 0.30)
      MaxAtrPct: float }        // atr_pct(close) < this (production 0.08)

/// Per-ticker rolling-indicator state for the momentum-v1 system.
///
/// All windowed structures follow the v0 "prior bars, exclude current"
/// convention: read `.State` (or the snapshot fields below) BEFORE pushing
/// the current bar, so every value measures the name's state going INTO the
/// bar — no lookahead.
///
/// Indicators carried:
///   - trailing stop reference     : `stopLow` = min(low) over the prior `stopLowWindow` bars
///   - trailing exit reference     : `trailHigh` = max(high) over the prior `trailWindow` bars
///   - ATR(14)                     : simple mean of LOG true range over the prior 14
///                                   bars — intrinsically an ATR% (no close division)
///   - long-term close channel     : `hiClose` = max(close) over the prior `hiCloseWindow` bars
///   - tightness range (14)        : max(high) and min(low) over the prior 14 bars
///                                   (log span / logATR)
type QullaSystem
    ( stopLowWindow: int,
      trailWindow: int,
      hiCloseWindow: int,
      atrWindow: int,
      tightnessWindow: int,
      volDays: int,
      expansionThr: float,
      exitTimeCap: int,
      entryCfg: EntryConfig ) =

    // ----- rolling structures -----
    let stopLow   = MinMa(stopLowWindow)        // trailing stop: min low
    let trailHigh = MaxMa(trailWindow)          // trailing-limit exit: max high
    let hiClose   = MaxMa(hiCloseWindow)        // long-term close channel (e.g. 252d)
    let atr       = AvgMa(atrWindow)            // ATR = mean LOG true range over the window
    let rangeHigh = MaxMa(tightnessWindow)      // tightness: max high
    let rangeLow  = MinMa(tightnessWindow)      // tightness: min low
    // 4-week (28-calendar-day) trailing means, v0 `stock_volume_4w` semantics.
    let avgVol    = CalendarMeanMa(volDays)     // AVG(volume)
    let avgDolVol = CalendarMeanMa(volDays)     // AVG(close * volume)

    // Open + closed trips for this ticker, in entry order.
    let positions = ResizeArray<Position>()

    let mutable prevClose : float voption = ValueNone
    let mutable barsSeen  = 0

    // ----- snapshots captured at the LAST ProcessBar, BEFORE the bar was
    //       pushed, so they describe the state going into that bar.
    //       ValueNone until the underlying window has seen ≥1 prior bar. -----
    let mutable sStopLow   : float voption = ValueNone
    let mutable sTrailHigh : float voption = ValueNone
    let mutable sHiClose   : float voption = ValueNone
    let mutable sAtrLog    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone
    // Prior bar's close, snapshotted before this bar is folded in (for pct_up).
    let mutable sPrevClose : float voption = ValueNone
    let mutable sAvgVol    : float voption = ValueNone
    let mutable sAvgDolVol : float voption = ValueNone

    member _.BarsSeen = barsSeen

    /// Trailing stop reference: lowest low over the prior `stopLowWindow` bars.
    member _.StopLow = sStopLow
    /// Trailing-limit exit reference: highest high over the prior `trailWindow` bars
    /// (pre-push, EXCLUDES the current bar). The resting sell limit rests here.
    member _.TrailHigh = sTrailHigh
    /// Long-term close channel: highest close over the prior `hiCloseWindow` bars.
    member _.HiClose = sHiClose
    /// ATR(14) in LOG space: mean log-true-range over the prior `atrWindow` bars.
    /// Because log-true-range is itself a relative (log-return-magnitude) measure,
    /// this is directly an ATR% stand-in — no division by any close price. A big
    /// breakout day no longer deflates the figure the way dividing prior-14 ATR by
    /// the CURRENT (jumped) close used to: log TR measures the bar's volatility on
    /// its own scale, regardless of where the close landed.
    member _.AtrLog = sAtrLog
    /// ATR% stand-in = the log ATR directly (a ~percentage of price per bar).
    /// No close-price argument: the log-space TR already normalizes by price level.
    member _.AtrPct = sAtrLog
    /// 14-day LOG price span = log(max high) - log(min low) over the prior
    /// `tightnessWindow` bars. ValueNone until both edges are available / on a
    /// non-positive low.
    member _.RangeLog =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo when hi > 0.0 && lo > 0.0 ->
            ValueSome (log hi - log lo)
        | _ -> ValueNone
    /// Consolidation tightness = log range / log ATR. Low = coiled spring (the
    /// window's whole span is only a few average bars wide), high = trending /
    /// expanding. ValueNone until ATR is available. Fully in log space, so it's
    /// scale-free. No `* window` factor: ATR is already a per-bar average, and the
    /// threshold is set by sweep, so the constant span factor is redundant.
    member this.Tightness =
        match this.RangeLog, sAtrLog with
        | ValueSome r, ValueSome atr when atr <> 0.0 ->
            ValueSome (r / atr)
        | _ -> ValueNone
    /// 4-week trailing average share volume (v0 `avg_volume_4w`).
    member _.AvgVolume = sAvgVol
    /// 4-week trailing average dollar volume (v0 `avg_dollar_volume_4w`).
    member _.AvgDollarVolume = sAvgDolVol
    /// Relative volume = current bar's volume / 4-week avg volume (v0 `rvol`).
    /// ValueNone with no baseline / a 0 baseline.
    member _.Rvol (volume: float) =
        match sAvgVol with
        | ValueSome av when av <> 0.0 -> ValueSome (volume / av)
        | _ -> ValueNone
    /// Same-day return = close / prev close - 1 (v0 `pct_up`). ValueNone on the
    /// first bar (no prior close) or a 0 prior close.
    member _.PctUp (closePrice: float) =
        match sPrevClose with
        | ValueSome pc when pc <> 0.0 -> ValueSome (closePrice / pc - 1.0)
        | _ -> ValueNone

    /// Does the CURRENT bar trigger an entry? Reads pre-push snapshots, so it
    /// must be evaluated AFTER ProcessBar(bar). Mirrors v0's in-engine
    /// `is_entry` (breakout / rvol / history / ADV / 52w-proximity) plus the
    /// production price / tightness / ATR% filters moved in-engine here.
    /// A missing (ValueNone) metric for any active gate fails the entry — we
    /// don't trade what we can't measure (matches v0's COALESCE(..., FALSE)).
    member this.ShouldEnter (bar: Bar) : bool =
        let c = bar.close
        let inline gate (v: float voption) (test: float -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // breakout: same-day return >= threshold
        gate (this.PctUp c) (fun pu -> pu >= entryCfg.UpThreshold)
        // rvol band (production [RvolMin, RvolMax])
        && gate (this.Rvol (float bar.volume)) (fun rv ->
               rv >= entryCfg.RvolMin && rv <= entryCfg.RvolMax)
        // enough prior history (v0 prior_idx > MinPriorDays)
        && barsSeen > entryCfg.MinPriorDays
        // liquidity floor on avg dollar volume
        && gate sAvgDolVol (fun adv -> adv >= entryCfg.MinAvgDollarVolume)
        // 52-week-high proximity band on the close channel
        && gate sHiClose (fun hi -> c >= entryCfg.Min52wPct * hi)
        // price floor (production)
        && c >= entryCfg.MinPrice
        // consolidation tightness (production)
        && gate this.Tightness (fun t -> t < entryCfg.MaxTightness)
        // ATR% cap (production)
        && gate this.AtrPct (fun a -> a < entryCfg.MaxAtrPct)

    /// Update every rolling structure with the most recent bar.
    /// Snapshots are taken BEFORE the push (prior-bars / no-lookahead), then
    /// the bar is folded in for the next call.
    member _.ProcessBar (bar: Bar) =
        // 1) snapshot the pre-bar state (what the system "knew" entering this bar)
        //    The calendar means must be evicted as-of THIS bar's date first, so
        //    the window covers strictly-prior days within `volDays` of it.
        avgVol.Evict    bar.date
        avgDolVol.Evict bar.date
        sAvgVol    <- avgVol.State
        sAvgDolVol <- avgDolVol.State
        sStopLow   <- stopLow.State
        sTrailHigh <- trailHigh.State
        sHiClose   <- hiClose.State
        sAtrLog    <- atr.State
        sRangeHigh <- rangeHigh.State
        sRangeLow  <- rangeLow.State
        sPrevClose <- prevClose   // prior bar's close, before this bar is folded in

        // 2) fold the current bar in
        match prevClose with
        | ValueSome pc when bar.high > 0.0 && bar.low > 0.0 && pc > 0.0 ->
            // LOG true range against the prior close. Each leg is a log-price
            // difference, i.e. a log return magnitude, so the resulting ATR is
            // intrinsically a percentage-of-price measure (no later division by
            // any close needed). Equivalent legs to the absolute TR, on logs:
            //   log(high) - log(low)
            //   |log(high) - log(prevClose)|
            //   |log(low)  - log(prevClose)|
            let lh, ll, lpc = log bar.high, log bar.low, log pc
            (lh - ll)
            |> max (abs (lh - lpc))
            |> max (abs (ll - lpc))
            |> atr.Push
        | _ ->
            // first bar (no prior close) or a non-positive price -> log TR
            // undefined; skip it from the ATR mean
            ()

        stopLow.Push   bar.low
        trailHigh.Push bar.high
        hiClose.Push   bar.close
        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        let vol = float bar.volume
        avgVol.Push    (bar.date, vol)
        avgDolVol.Push (bar.date, bar.close * vol)

        prevClose <- ValueSome bar.close
        barsSeen  <- barsSeen + 1

    /// All trips for this ticker (open + closed), in entry order.
    member _.Positions = positions

    /// Advance one open position by the current bar. Reads the system's
    /// snapshots, so it must run AFTER ProcessBar(bar) for `bar`. Returns the
    /// (possibly state-changed) position; an Exited position is returned as-is.
    ///
    ///   - Holding: stop level = max(prior-window low, entry-day low); a stop
    ///     fires when bar.low <= that level, an expansion exit fires when
    ///     tightness > expansionThr. The TRIGGER bar does not fill — a stop
    ///     moves to ExitingLimit, an expansion to ExitingMarket.
    ///   - ExitingLimit: a SELL LIMIT rests at the PRIOR bar's N-day high
    ///     (sTrailHigh, excludes the current bar — symmetric with the stop's
    ///     low_15_prior). It fills only when bar.high reaches that level, so it
    ///     can genuinely miss and roll forward — no guaranteed top-tick fill.
    ///   - ExitingMarket: sells at the next bar's open.
    member this.Update (bar: Bar) (pos: Position) : Position =
        match pos.State with
        | Exited _ -> pos
        | Holding ->
            // Stop floor: the higher of the trailing prior-window low and the
            // Qullamaggie entry-day low. (sStopLow excludes the current bar.)
            // TODO(post-parity): make this a HARD up-only ratchet. v0 only
            // ratchets implicitly — max(rising low_15_prior, entry-day low) —
            // so the stop can tick DOWN if a lower low slides into the trailing
            // window (it just can't go below the entry-day low). A proper Qulla
            // trailing stop never loosens; once we match v0 trips, carry the
            // stop in the Position and set it to max(prev_stop, max(sStopLow,
            // entryDayLow)) so it only ever rises.
            let stopLevel =
                match sStopLow with
                | ValueSome lo -> max lo pos.EntryDayLow
                | ValueNone -> pos.EntryDayLow
            let stopHit = bar.low <= stopLevel
            let expansionHit =
                match this.Tightness with
                | ValueSome t -> t > expansionThr
                | ValueNone -> false
            // Priority: stop is checked BEFORE expansion. Expansion always sells
            // at the next bar's open. A stop, with exitTimeCap=0, also sells at
            // the next open (the baseline — N ignored); with cap>=1 it rests a
            // sell limit for up to `exitTimeCap` bars. Neither fills the trigger
            // bar itself.
            if stopHit then
                if exitTimeCap <= 0 then { pos with State = ExitingMarket "stop" }
                else { pos with State = ExitingLimit 0 }
            elif expansionHit then
                { pos with State = ExitingMarket "expansion" }
            else pos
        | ExitingMarket reason ->
            // Sell at THIS bar's open (this is the bar after the trigger / after
            // the limit timed out).
            { pos with State = Exited (bar.date, bar.``open``, reason) }
        | ExitingLimit barsRested ->
            // Rest a SELL LIMIT at the PRIOR bar's N-day high (sTrailHigh excludes
            // the current bar). Fill only if this bar trades up to it. This is
            // rest-bar number (barsRested+1) of at most `exitTimeCap`.
            match sTrailHigh with
            | ValueSome limit when bar.high >= limit ->
                { pos with State = Exited (bar.date, limit, "stop_limit") }
            | _ when barsRested + 1 >= exitTimeCap ->
                // Rested the full cap without filling -> exit at the next open.
                { pos with State = ExitingMarket "stop_limit_timeout" }
            | _ ->
                // Still within the cap and out of reach — keep resting.
                { pos with State = ExitingLimit (barsRested + 1) }

    /// Advance the whole system by one bar: fold the bar into the indicators,
    /// update every open position, and open a new trip (filled at this bar's
    /// close) when the in-engine entry predicate fires. `breadthOk` is the one
    /// market-wide gate the engine can't compute itself (breadth_lag1 > 0.5);
    /// the caller ANDs it in. Default true = no breadth gate.
    ///
    /// Entry/exit ordering matches v0: an entry bar is never its own first
    /// exit-check bar, because the new position is appended AFTER the existing
    /// positions are updated for this bar.
    member this.Process (bar: Bar, ?breadthOk: bool) =
        this.ProcessBar bar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Update bar positions.[i]
        let breadthOk = defaultArg breadthOk true
        if breadthOk && this.ShouldEnter bar then
            // ShouldEnter has already verified each gate's metric is ValueSome,
            // so these reads are safe (NaN only if a metric isn't gated, which
            // it always is here).
            let orNan = function ValueSome v -> v | ValueNone -> nan
            positions.Add
                { EntryDate = bar.date
                  EntryPrice = bar.close
                  EntryDayLow = bar.low
                  EntryVolume = bar.volume
                  RvolAtEntry = orNan (this.Rvol (float bar.volume))
                  AvgDollarVolumeAtEntry = orNan sAvgDolVol
                  PctUpAtEntry = orNan (this.PctUp bar.close)
                  AtrPctAtEntry = orNan this.AtrPct
                  TightnessAtEntry = orNan this.Tightness
                  State = Holding }

    /// Close any still-open positions at the final bar's close, marked-to-market
    /// (v0's "mtm" exit). Call once after the last bar of the series. Positions
    /// still resting (ExitingLimit / ExitingMarket) are also MTM'd here — their
    /// exit never resolved within the data.
    member _.Finalize (lastBar: Bar) =
        for i in 0 .. positions.Count - 1 do
            match positions.[i].State with
            | Exited _ -> ()
            | Holding | ExitingLimit _ | ExitingMarket _ ->
                positions.[i] <-
                    { positions.[i] with
                        State = Exited (lastBar.date, lastBar.close, "mtm") }
