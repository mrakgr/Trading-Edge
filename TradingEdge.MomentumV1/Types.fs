module TradingEdge.MomentumV1.Types

open System
open TradingEdge.MomentumV1.RollingMa

type Bar =
    { date: DateOnly
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// Life-cycle of a single trip.
///   - Holding: still in the trade, watching for a stop / expansion trigger.
///   - Exiting seed: a "get me out" trigger fired on the prior bar; a SELL
///     LIMIT now rests, seeded at the trigger bar's N-day high (`seed`) and
///     ratcheting DOWN-only. At N=1 the rolling N-day high is just each bar's
///     own high, so the limit fills on the very NEXT bar at min(seed, high) —
///     `seed` is the only per-position value the fill price needs.
///   - Exited: closed for any reason; never updated again.
type PositionState =
    | Holding
    | Exiting of seed: float
    | Exited of exitDate: DateOnly * exitPrice: float * reason: string

/// One trip. EntryDate/EntryPrice are fixed at open; State carries the rest.
type Position =
    { EntryDate: DateOnly
      EntryPrice: float
      EntryDayLow: float        // Qullamaggie initial-stop floor (entry bar's low)
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
///   - ATR(14)                     : simple mean of true range over the prior 14 bars
///   - long-term close channel     : `hiClose` = max(close) over the prior `hiCloseWindow` bars
///   - tightness range (14)        : max(high) and min(low) over the prior 14 bars
type QullaSystem
    ( stopLowWindow: int,
      trailWindow: int,
      hiCloseWindow: int,
      atrWindow: int,
      tightnessWindow: int,
      volDays: int,
      expansionThr: float,
      entryCfg: EntryConfig ) =

    // ----- rolling structures -----
    let stopLow   = MinMa(stopLowWindow)        // trailing stop: min low
    let trailHigh = MaxMa(trailWindow)          // trailing-limit exit: max high
    let hiClose   = MaxMa(hiCloseWindow)        // long-term close channel (e.g. 252d)
    let atr       = AvgMa(atrWindow)            // ATR = mean true range over the window
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
    let mutable sAtrAbs    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone
    // Prior bar's close, snapshotted before this bar is folded in (for pct_up).
    let mutable sPrevClose : float voption = ValueNone
    let mutable sAvgVol    : float voption = ValueNone
    let mutable sAvgDolVol : float voption = ValueNone
    // Post-push trailing high INCLUDING the current bar. v0's trailing-limit
    // seed (`nDayHigh d`) takes the max high over a window that INCLUDES the
    // trigger bar d, unlike the stop's `low_15_prior` (which excludes it). So
    // the limit target reads this, while the stop reads the pre-push sTrailHigh.
    let mutable sTrailHighIncl : float voption = ValueNone

    member _.BarsSeen = barsSeen

    /// Trailing stop reference: lowest low over the prior `stopLowWindow` bars.
    member _.StopLow = sStopLow
    /// Trailing-limit exit reference: highest high over the prior `trailWindow` bars
    /// (pre-push, EXCLUDES the current bar).
    member _.TrailHigh = sTrailHigh
    /// Trailing-limit SEED: highest high over `trailWindow` bars INCLUDING the
    /// current bar (post-push). This is v0's `nDayHigh d` — the seed for the
    /// mean-reversion limit when a stop fires on this bar.
    member _.TrailHighIncl = sTrailHighIncl
    /// Long-term close channel: highest close over the prior `hiCloseWindow` bars.
    member _.HiClose = sHiClose
    /// ATR(14), absolute (mean true range over the prior `atrWindow` bars).
    member _.AtrAbs = sAtrAbs
    /// ATR as a fraction of the entry bar's close. ValueNone before any bar / on a 0 close.
    // TODO(post-parity): change the ATR% denominator. v0 divides the prior-14 ATR
    // by the CURRENT bar's close, but on a big breakout day that close jumps, which
    // deflates ATR% even though the underlying volatility is unchanged — so a
    // genuinely volatile name can sneak under the atr_pct < 0.08 filter purely
    // because its trigger bar gapped/ran up. Once we match v0 trips, switch the
    // denominator to the PREVIOUS close (or some pre-breakout reference) so ATR%
    // reflects volatility going INTO the bar, not distorted by the breakout itself.
    member _.AtrPct (closePrice: float) =
        match sAtrAbs with
        | ValueSome atr when closePrice <> 0.0 -> ValueSome (atr / closePrice)
        | _ -> ValueNone
    /// 14-day price span (max high - min low over the prior `tightnessWindow` bars).
    member _.RangeAbs =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo -> ValueSome (hi - lo)
        | _ -> ValueNone
    /// Consolidation tightness = range / (window * ATR). ~1 = clean trend,
    /// well below 1 = coiled spring. ValueNone until ATR is available.
    member this.Tightness =
        match this.RangeAbs, sAtrAbs with
        | ValueSome r, ValueSome atr when atr <> 0.0 ->
            ValueSome (r / (float tightnessWindow * atr))
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
        && gate (this.AtrPct c) (fun a -> a < entryCfg.MaxAtrPct)

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
        sAtrAbs    <- atr.State
        sRangeHigh <- rangeHigh.State
        sRangeLow  <- rangeLow.State
        sPrevClose <- prevClose   // prior bar's close, before this bar is folded in

        // 2) fold the current bar in
        match prevClose with
        | ValueSome pc ->
            // true range against the prior close
            bar.high - bar.low
            |> max (abs (bar.high - pc))
            |> max (abs (bar.low - pc))
            |> atr.Push
        | ValueNone ->
            // first bar has no prior close -> TR undefined; skip it from the ATR mean
            ()

        stopLow.Push   bar.low
        trailHigh.Push bar.high
        sTrailHighIncl <- trailHigh.State   // post-push: includes the current bar
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
    /// v0 parity (the locked default: Qulla day-low stop + 0.70 expansion exit,
    /// N=1 trailing-limit, no time stop):
    ///   - Holding: stop level = max(prior-window low, entry-day low); a stop
    ///     fires when bar.low <= that level, an expansion exit fires when
    ///     tightness > expansionThr. The TRIGGER bar does not fill — we move to
    ///     Exiting and rest the limit seeded at this bar's N-day high (incl).
    ///   - Exiting seed: ratchet the limit DOWN to min(seed, bar's N-day high)
    ///     and fill when bar.high >= limit. At N=1 the N-day high is bar.high,
    ///     so this fills THIS bar at min(seed, bar.high).
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
            if stopHit || expansionHit then
                // Trigger fires on this bar; seed the resting limit at the
                // N-day high INCLUDING this bar (v0's nDayHigh d). Don't fill
                // the trigger bar itself.
                match sTrailHighIncl with
                | ValueSome seed -> { pos with State = Exiting seed }
                | ValueNone      -> { pos with State = Exiting bar.high }
            else pos
        | Exiting seed ->
            // Ratchet DOWN-only to the current N-day high. At N=1 that's just
            // bar.high, so limit = min(seed, bar.high) and we always fill here.
            // TODO(post-parity): the down-only ratchet only exists to patch a
            // flaw in comparing the limit to the CURRENT bar's own high. If we
            // instead treated the limit like the stop — compare the fill to the
            // PREVIOUS bar's N-day high (exclude the current bar from its own
            // target, sTrailHigh not sTrailHighIncl) — a stale high could no
            // longer hold the target above current price, and the ratchet would
            // be unnecessary. Test this once we match v0 trips.
            let nDayHigh =
                match sTrailHighIncl with
                | ValueSome h -> h
                | ValueNone   -> bar.high
            let limit = min seed nDayHigh
            if bar.high >= limit then
                { pos with State = Exited (bar.date, limit, "stop_limit") }
            else
                // Couldn't reach the limit; keep resting at the ratcheted level.
                { pos with State = Exiting limit }

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
            positions.Add
                { EntryDate = bar.date
                  EntryPrice = bar.close
                  EntryDayLow = bar.low
                  State = Holding }
