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
      volDays: int ) =

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
    let mutable sAvgVol    : float voption = ValueNone
    let mutable sAvgDolVol : float voption = ValueNone

    member _.BarsSeen = barsSeen

    /// Trailing stop reference: lowest low over the prior `stopLowWindow` bars.
    member _.StopLow = sStopLow
    /// Trailing-limit exit reference: highest high over the prior `trailWindow` bars.
    member _.TrailHigh = sTrailHigh
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
        hiClose.Push   bar.close
        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        let vol = float bar.volume
        avgVol.Push    (bar.date, vol)
        avgDolVol.Push (bar.date, bar.close * vol)

        prevClose <- ValueSome bar.close
        barsSeen  <- barsSeen + 1
