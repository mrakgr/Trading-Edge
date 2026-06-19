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

/// Trade direction. Long = buy, trail the stop along the prior-window LOW, cover
/// (sell) when price breaks below it; P&L = exit − entry. Short = sell, trail the
/// stop along the prior-window HIGH, cover (buy) when price breaks above it; P&L =
/// entry − exit. The whole stop/exit geometry mirrors between the two.
type Side =
    | Long
    | Short

/// Trailing-stop mechanism.
///   - WindowLow: the legacy Qulla rule — trail the prior-window low (`stopLowWindow`),
///     optionally floored at the entry-day low (`useEntryDayStop`). Can tick DOWN if a
///     lower low slides into the window; no hard ratchet.
///   - AtrRatchet k: an up-only ATR%-chandelier off the LATEST close. Each bar the
///     candidate stop = close − k·(ATR%·close) (ATR% = the log-ATR, a per-bar fractional
///     volatility, converted to dollars at the current close). The carried stop only ever
///     RISES: stop = max(prev_stop, candidate). No entry-day-low floor (the ratchet starts
///     from the entry bar's own candidate). Mirrored for Short (close + k·ATR$, ratchets
///     DOWN-only).
///   - FixedPct p: identical up-only ratchet but with a CONSTANT fractional distance instead
///     of k·ATR% — candidate stop = close·(1 − p). Isolates "distance" from "mechanism": same
///     trailing machinery as AtrRatchet, distance set directly. Mirrored for Short (close·(1+p)).
type StopMode =
    | WindowLow
    | AtrRatchet of k: float
    | FixedPct of p: float

/// Life-cycle of a single trip.
///   - PendingLimit barsRested: the entry signal fired on the prior bar, but
///     instead of buying the close we rest a BUY LIMIT at the trailing prior-
///     window low (`EntryTrailLow`, drags DOWN each bar as the window low
///     falls). It fills only when bar.low reaches that level — a real resting
///     order that can miss. `barsRested` counts the bars it has rested; once it
///     reaches `entryTimeCap` without filling, the trade enters at the NEXT
///     bar's open (timeout) tagged "open_after_cap". The signal-bar close is
///     NOT a fill — the whole point is to test pullback entries vs at-close.
///   - Holding: filled and in the trade, watching for a stop / expansion trigger.
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
    | PendingLimit of barsRested: int
    | Holding
    | ExitingLimit of barsRested: int
    | ExitingMarket of reason: string
    | Exited of exitDate: DateOnly * exitPrice: float * reason: string

/// One trip. The *_at_entry metrics are snapshotted at the SIGNAL bar and fixed;
/// EntryDate/EntryPrice are the FILL values (= the signal close in the at-close
/// baseline, or the limit / timed-open price in limit-entry mode), set when the
/// order fills. State carries the rest of the life-cycle.
type Position =
    { SignalDate: DateOnly      // bar that fired the entry signal (metrics are as of here)
      EntryDate: DateOnly       // bar the order actually FILLED (= SignalDate at-close)
      EntryPrice: float         // fill price (signal close at-close; limit/open in limit mode)
      EntryReason: string       // "close" | "limit" | "open_after_cap"
      EntryDayStopRef: float    // Qullamaggie initial-stop reference (FILL bar's low for Long, high for Short)
      StopLowAtEntry: float     // trailing prior-window low/high going INTO the signal bar (the 15-day-stop ref); nan if window cold
      RatchetStop: float        // AtrRatchet mode: the carried up-only stop level (nan until the first Holding bar sets it)
      // metrics snapshotted at the SIGNAL bar (for the trips CSV / parity diff)
      EntryVolume: int64
      RvolAtEntry: float
      AvgDollarVolumeAtEntry: float
      PctUpAtEntry: float
      AtrPctAtEntry: float
      TightnessAtEntry: float
      Pct52wAtEntry: float       // close / hi_252_prior - 1 (how far above the prior 252d closing high)
      Pct52wHighAtEntry: float   // close / hi_252_high - 1 (above the prior 252d INTRADAY high — true resistance)
      Pct52wLowCloseAtEntry: float // close / lo_252_close - 1 (below the prior 252d closing LOW)
      Pct52wLowAtEntry: float    // close / lo_252_low - 1 (below the prior 252d INTRADAY low — true support)
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

/// Which tightness measure the entry filter and the expansion exit read.
///   - Log    : log(maxHigh/minLow) / logATR  — scale-free, the v2 default.
///   - Linear : (maxHigh − minLow) / linearATR — a raw range/ATR ratio. A blow-off
///     bar is a sharp LINEAR range expansion (a 30% candle) but a compressed LOG
///     one, so linear may make the expansion exit fire on the spikes it should catch.
type TightnessMode =
    | Log
    | Linear

/// Per-ticker rolling-indicator state for the momentum-v2 system.
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
      entryLimitMode: bool,
      entryTrailWindow: int,
      entryTimeCap: int,
      useEntryDayStop: bool,
      stopMode: StopMode,
      side: Side,
      tightnessMode: TightnessMode,
      entryCfg: EntryConfig ) =

    // ----- rolling structures -----
    let stopLow   = MinMa(stopLowWindow)        // LONG trailing stop: min low
    let entryLow  = MinMa(entryTrailWindow)     // limit-entry: prior-window low the buy limit rests at
    let entryHigh = MaxMa(entryTrailWindow)     // limit-entry SHORT mirror: prior-window high
    let stopHigh  = MaxMa(stopLowWindow)        // SHORT trailing stop: max high
    let trailHigh = MaxMa(trailWindow)          // trailing-limit exit: max high
    let hiClose   = MaxMa(hiCloseWindow)        // long-term close channel (e.g. 252d), over CLOSES
    let hiHigh    = MaxMa(hiCloseWindow)        // long-term HIGH channel (252d max of intraday highs)
    let loClose   = MinMa(hiCloseWindow)        // long-term LOW close channel (252d min of closes)
    let loLow     = MinMa(hiCloseWindow)        // long-term LOW channel (252d min of intraday lows)
    let atrLog    = AvgMa(atrWindow)            // ATR = mean LOG true range over the window
    let atrLin    = AvgMa(atrWindow)            // ATR = mean ABSOLUTE true range (linear)
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
    let mutable sStopHigh  : float voption = ValueNone
    let mutable sEntryLow  : float voption = ValueNone
    let mutable sEntryHigh : float voption = ValueNone
    let mutable sTrailHigh : float voption = ValueNone
    let mutable sHiClose   : float voption = ValueNone
    let mutable sHiHigh    : float voption = ValueNone
    let mutable sLoClose   : float voption = ValueNone
    let mutable sLoLow     : float voption = ValueNone
    let mutable sAtrLog    : float voption = ValueNone
    let mutable sAtrLin    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone
    // Prior bar's close, snapshotted before this bar is folded in (for pct_up).
    let mutable sPrevClose : float voption = ValueNone
    let mutable sAvgVol    : float voption = ValueNone
    let mutable sAvgDolVol : float voption = ValueNone

    member _.BarsSeen = barsSeen

    /// Trailing stop reference: lowest low over the prior `stopLowWindow` bars.
    member _.StopLow = sStopLow
    /// Limit-ENTRY reference (Long): lowest low over the prior `entryTrailWindow`
    /// bars (pre-push, EXCLUDES the current bar). The resting BUY limit rests here
    /// and drags DOWN as the window low falls.
    member _.EntryLow = sEntryLow
    /// Limit-ENTRY reference (Short mirror): highest high over the prior
    /// `entryTrailWindow` bars. The resting SELL limit rests here.
    member _.EntryHigh = sEntryHigh
    /// Trailing-limit exit reference: highest high over the prior `trailWindow` bars
    /// (pre-push, EXCLUDES the current bar). The resting sell limit rests here.
    member _.TrailHigh = sTrailHigh
    /// Long-term close channel: highest close over the prior `hiCloseWindow` bars.
    member _.HiClose = sHiClose
    /// How far the given close sits ABOVE the prior 252d closing high:
    /// `close / hi_252_prior - 1`. >0 = a new closing high (by that fraction);
    /// <0 = still below the prior high. ValueNone before the channel is warm / on 0.
    member _.Pct52w (closePrice: float) =
        match sHiClose with
        | ValueSome hi when hi <> 0.0 -> ValueSome (closePrice / hi - 1.0)
        | _ -> ValueNone
    /// How far the given close sits above the prior 252d INTRADAY-high (max of highs):
    /// `close / hi_252_high - 1`. This is the true resistance reference — clearing the
    /// max CLOSE (Pct52w) can still leave price under the prior intraday high. ValueNone
    /// before the channel is warm / on 0.
    member _.Pct52wHigh (closePrice: float) =
        match sHiHigh with
        | ValueSome hi when hi <> 0.0 -> ValueSome (closePrice / hi - 1.0)
        | _ -> ValueNone
    /// How far the given close sits relative to the prior 252d closing LOW:
    /// `close / lo_252_close - 1`. <0 = a new closing low (by that fraction);
    /// >0 = still above the prior low. ValueNone before the channel is warm / on 0.
    member _.Pct52wLowClose (closePrice: float) =
        match sLoClose with
        | ValueSome lo when lo <> 0.0 -> ValueSome (closePrice / lo - 1.0)
        | _ -> ValueNone
    /// How far the given close sits relative to the prior 252d INTRADAY low (min of lows):
    /// `close / lo_252_low - 1`. The true support reference — a new closing low (Pct52wLowClose<0)
    /// can still sit above the prior intraday low. ValueNone before the channel is warm / on 0.
    member _.Pct52wLow (closePrice: float) =
        match sLoLow with
        | ValueSome lo when lo <> 0.0 -> ValueSome (closePrice / lo - 1.0)
        | _ -> ValueNone
    /// ATR(14) in LOG space: mean log-true-range over the prior `atrWindow` bars.
    /// Because log-true-range is itself a relative (log-return-magnitude) measure,
    /// this is directly an ATR% stand-in — no division by any close price. A big
    /// breakout day no longer deflates the figure the way dividing prior-14 ATR by
    /// the CURRENT (jumped) close used to: log TR measures the bar's volatility on
    /// its own scale, regardless of where the close landed.
    member _.AtrLog = sAtrLog
    /// ATR(14) in LINEAR space: mean ABSOLUTE true range over the prior `atrWindow`
    /// bars (dollars per bar). Used only for the linear tightness/expansion variant.
    member _.AtrLin = sAtrLin
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
    /// 14-day ABSOLUTE price span = max high - min low over the prior
    /// `tightnessWindow` bars (dollars).
    member _.RangeAbs =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo -> ValueSome (hi - lo)
        | _ -> ValueNone
    /// Consolidation tightness in LOG space = log range / log ATR.
    member this.TightnessLog =
        match this.RangeLog, sAtrLog with
        | ValueSome r, ValueSome atr when atr <> 0.0 -> ValueSome (r / atr)
        | _ -> ValueNone
    /// Consolidation tightness in LINEAR space = abs range / abs ATR.
    member this.TightnessLin =
        match this.RangeAbs, sAtrLin with
        | ValueSome r, ValueSome atr when atr <> 0.0 -> ValueSome (r / atr)
        | _ -> ValueNone
    /// Consolidation tightness in the configured mode. Low = coiled spring (the
    /// window's whole span is only a few average bars wide), high = trending /
    /// expanding. The entry filter and the expansion exit both read this. No
    /// `* window` factor: ATR is already a per-bar average and the threshold is set
    /// by sweep, so the constant span factor is redundant. NOTE: Log and Linear are
    /// on DIFFERENT numeric scales, so MaxTightness / expansionThr must be tuned per
    /// mode (they are not interchangeable).
    member this.Tightness =
        match tightnessMode with
        | Log -> this.TightnessLog
        | Linear -> this.TightnessLin
    /// Position-relative EXPANSION tightness for an open trade, in the configured
    /// mode. Same range/ATR ratio as `Tightness`, but the range LOW is floored at
    /// the trade's `entryPrice`: range = `rangeHigh − max(rangeLow, entryPrice)`.
    ///
    /// Why: the plain 14-day low sits below the breakout base, so a name that has
    /// run far ABOVE our entry over a multi-bar climax still reads a normal tightness
    /// (the climax inflates rangeHigh and ATR together). Flooring at entry makes the
    /// numerator "how far above our entry it has stretched", which DOES grow across a
    /// multi-bar run-up — the signal a blow-off exit actually wants. Used only by the
    /// expansion exit; the entry filter keeps the plain (entry-independent) tightness.
    /// ValueNone until the windows/ATR are warm; if the floored low ≥ high (price has
    /// fallen back below entry across the whole window) the range is 0 → not expanding.
    member _.ExpansionTightness (entryPrice: float) : float voption =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo ->
            let lo' = max lo entryPrice
            match tightnessMode with
            | Linear ->
                match sAtrLin with
                | ValueSome atr when atr <> 0.0 -> ValueSome (max 0.0 (hi - lo') / atr)
                | _ -> ValueNone
            | Log ->
                match sAtrLog with
                | ValueSome atr when atr <> 0.0 && hi > 0.0 && lo' > 0.0 ->
                    ValueSome (max 0.0 (log hi - log lo') / atr)
                | _ -> ValueNone
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
        sStopHigh  <- stopHigh.State
        sEntryLow  <- entryLow.State
        sEntryHigh <- entryHigh.State
        sTrailHigh <- trailHigh.State
        sHiClose   <- hiClose.State
        sHiHigh    <- hiHigh.State
        sLoClose   <- loClose.State
        sLoLow     <- loLow.State
        sAtrLog    <- atrLog.State
        sAtrLin    <- atrLin.State
        sRangeHigh <- rangeHigh.State
        sRangeLow  <- rangeLow.State
        sPrevClose <- prevClose   // prior bar's close, before this bar is folded in

        // 2) fold the current bar in. Both the LOG and LINEAR ATRs are updated each
        //    bar so the tightness mode is a pure read-time switch.
        match prevClose with
        | ValueSome pc when bar.high > 0.0 && bar.low > 0.0 && pc > 0.0 ->
            // LINEAR (absolute) true range against the prior close:
            //   high - low , |high - prevClose| , |low - prevClose|
            (bar.high - bar.low)
            |> max (abs (bar.high - pc))
            |> max (abs (bar.low - pc))
            |> atrLin.Push
            // LOG true range: each leg is a log-price difference, i.e. a log-return
            // magnitude, so the resulting ATR is intrinsically a percentage-of-price
            // measure (no later division by any close needed).
            let lh, ll, lpc = log bar.high, log bar.low, log pc
            (lh - ll)
            |> max (abs (lh - lpc))
            |> max (abs (ll - lpc))
            |> atrLog.Push
        | _ ->
            // first bar (no prior close) or a non-positive price -> TR undefined;
            // skip it from both ATR means
            ()

        stopLow.Push   bar.low
        stopHigh.Push  bar.high
        entryLow.Push  bar.low
        entryHigh.Push bar.high
        trailHigh.Push bar.high
        hiClose.Push   bar.close
        hiHigh.Push    bar.high
        loClose.Push   bar.close
        loLow.Push     bar.low
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
        | PendingLimit barsRested ->
            // The signal fired on a PRIOR bar; rest a BUY LIMIT (Long) at the
            // trailing prior-window low (sEntryLow excludes the current bar and
            // drags DOWN as the window low falls), or a SELL LIMIT (Short) at the
            // prior-window high. Fill only if THIS bar trades to it — a real
            // resting order that can miss and roll forward. This is rest-bar
            // number (barsRested+1) of at most `entryTimeCap`.
            //
            // Fill price models a conservative gap: a buy limit at `lvl` fills at
            // `min(lvl, open)` (a gap-down opening below the limit fills at the
            // open, not the better limit), symmetric for the short sell limit.
            // Entry-day stop reference is captured at the FILL bar (its low/high).
            let fillLong  lvl = min lvl bar.``open``
            let fillShort lvl = max lvl bar.``open``
            let filled =
                match side with
                | Long  -> match sEntryLow  with ValueSome lvl when bar.low  <= lvl -> ValueSome (fillLong lvl)  | _ -> ValueNone
                | Short -> match sEntryHigh with ValueSome lvl when bar.high >= lvl -> ValueSome (fillShort lvl) | _ -> ValueNone
            match filled with
            | ValueSome px ->
                { pos with
                    State = Holding
                    EntryDate = bar.date
                    EntryPrice = px
                    EntryReason = "limit"
                    EntryDayStopRef = (match side with Long -> bar.low | Short -> bar.high) }
            | ValueNone when barsRested + 1 >= entryTimeCap ->
                // Rested the full cap unfilled -> enter at the NEXT bar's open,
                // tagged so the open-after-cap entries can be analyzed separately.
                // Re-use ExitingMarket as the "fill at next open" carrier with a
                // sentinel reason that the next-bar branch recognizes for ENTRY.
                { pos with State = ExitingMarket "__enter_open_after_cap" }
            | ValueNone ->
                { pos with State = PendingLimit (barsRested + 1) }
        | Holding ->
            // Stop floor: the higher of the trailing prior-window low and the
            // Qullamaggie entry-day low. (sStopLow excludes the current bar.)
            // When `useEntryDayStop` is false the entry-day-low floor is dropped
            // and the stop is JUST the trailing prior-window low (and there is no
            // stop at all until that window has warmed, sStopLow = ValueNone).
            // TODO(post-parity): make this a HARD up-only ratchet. v0 only
            // ratchets implicitly — max(rising low_15_prior, entry-day low) —
            // so the stop can tick DOWN if a lower low slides into the trailing
            // window (it just can't go below the entry-day low). A proper Qulla
            // trailing stop never loosens; once we match v0 trips, carry the
            // stop in the Position and set it to max(prev_stop, max(sStopLow,
            // entryDayLow)) so it only ever rises.
            // The new ratcheted stop level after THIS bar, carried forward for the
            // next bar's check (AtrRatchet mode only; nan in WindowLow mode). No
            // lookahead: the level checked on this bar is `pos.RatchetStop` (set from
            // bars ≤ B-1); we only update it AFTER the check, for B+1.
            let mutable nextRatchet = pos.RatchetStop
            let stopHit =
                match stopMode with
                | WindowLow ->
                    match side with
                    | Long ->
                        // trail the prior-window LOW (floored at the entry-day low); stop
                        // when price breaks below it.
                        match sStopLow with
                        | ValueSome lo ->
                            let lvl = if useEntryDayStop then max lo pos.EntryDayStopRef else lo
                            bar.low <= lvl
                        | ValueNone -> useEntryDayStop && bar.low <= pos.EntryDayStopRef
                    | Short ->
                        // mirror: trail the prior-window HIGH (capped at the entry-day high);
                        // cover when price breaks above it.
                        match sStopHigh with
                        | ValueSome hi ->
                            let lvl = if useEntryDayStop then min hi pos.EntryDayStopRef else hi
                            bar.high >= lvl
                        | ValueNone -> useEntryDayStop && bar.high >= pos.EntryDayStopRef
                | AtrRatchet _ | FixedPct _ ->
                    // Up-only ratchet off the LATEST close. AtrRatchet uses a per-bar
                    // ATR%-based fractional distance (k·ATR%, ValueNone if ATR is cold);
                    // FixedPct uses a constant fraction p. Otherwise identical: check this
                    // bar against the carried level, then ratchet from this bar's close.
                    let frac : float voption =
                        match stopMode with
                        | AtrRatchet k -> sAtrLog |> ValueOption.map (fun atr -> k * atr)
                        | FixedPct p   -> ValueSome p
                        | WindowLow    -> ValueNone   // unreachable
                    let hit =
                        match side with
                        | Long  -> not (Double.IsNaN pos.RatchetStop) && bar.low  <= pos.RatchetStop
                        | Short -> not (Double.IsNaN pos.RatchetStop) && bar.high >= pos.RatchetStop
                    match frac with
                    | ValueSome f ->
                        let candidate =
                            match side with
                            | Long  -> bar.close * (1.0 - f)
                            | Short -> bar.close * (1.0 + f)
                        nextRatchet <-
                            if Double.IsNaN pos.RatchetStop then candidate
                            else match side with
                                 | Long  -> max pos.RatchetStop candidate   // ratchet UP only
                                 | Short -> min pos.RatchetStop candidate   // ratchet DOWN only
                    | ValueNone -> ()
                    hit
            // Expansion exit fires on the POSITION-RELATIVE tightness (range low
            // floored at the entry price), so it grows as the name runs above our
            // entry across a multi-bar climax — unlike the plain tightness, which a
            // climax bar can't move (it inflates range and ATR together).
            let expansionHit =
                match this.ExpansionTightness pos.EntryPrice with
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
            else { pos with RatchetStop = nextRatchet }  // still holding — carry the ratcheted stop forward
        | ExitingMarket "__enter_open_after_cap" ->
            // ENTRY timed-out: the buy/sell limit rested the full cap unfilled, so
            // we take the trade at THIS bar's open (the bar after the cap elapsed),
            // tagged "open_after_cap" so these forced-open fills can be analyzed
            // separately from the limit fills.
            { pos with
                State = Holding
                EntryDate = bar.date
                EntryPrice = bar.``open``
                EntryReason = "open_after_cap"
                EntryDayStopRef = (match side with Long -> bar.low | Short -> bar.high) }
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
            // At-close baseline: fill at this bar's close immediately (Holding).
            // Limit-entry mode: queue a PendingLimit order — no fill yet; it rests
            // a buy/sell limit at the trailing prior-window low/high for up to
            // `entryTimeCap` bars (then takes the open). EntryDate/EntryPrice are
            // placeholders here and are overwritten when the order actually fills.
            // The *_at_entry metrics are fixed at THIS (the signal) bar either way.
            positions.Add
                { SignalDate = bar.date
                  EntryDate = bar.date
                  EntryPrice = bar.close
                  EntryReason = "close"
                  EntryDayStopRef = (match side with Long -> bar.low | Short -> bar.high)
                  StopLowAtEntry = orNan (match side with Long -> sStopLow | Short -> sStopHigh)
                  RatchetStop = nan   // AtrRatchet sets this on the first Holding bar
                  EntryVolume = bar.volume
                  RvolAtEntry = orNan (this.Rvol (float bar.volume))
                  AvgDollarVolumeAtEntry = orNan sAvgDolVol
                  PctUpAtEntry = orNan (this.PctUp bar.close)
                  AtrPctAtEntry = orNan this.AtrPct
                  TightnessAtEntry = orNan this.Tightness
                  Pct52wAtEntry = orNan (this.Pct52w bar.close)
                  Pct52wHighAtEntry = orNan (this.Pct52wHigh bar.close)
                  Pct52wLowCloseAtEntry = orNan (this.Pct52wLowClose bar.close)
                  Pct52wLowAtEntry = orNan (this.Pct52wLow bar.close)
                  State = if entryLimitMode then PendingLimit 0 else Holding }

    /// Close any still-open positions at the final bar's close, marked-to-market
    /// (v0's "mtm" exit). Call once after the last bar of the series. Positions
    /// still resting (ExitingLimit / ExitingMarket) are also MTM'd here — their
    /// exit never resolved within the data.
    member _.Finalize (lastBar: Bar) =
        for i in 0 .. positions.Count - 1 do
            match positions.[i].State with
            | Exited _ -> ()
            // A still-pending entry order that never filled (or whose timed-open
            // fill never resolved within the data) is NOT a trip — leave it Pending
            // / sentinel so the caller can drop it (see `run`'s never-filled skip).
            | PendingLimit _ -> ()
            | ExitingMarket "__enter_open_after_cap" -> ()
            | Holding | ExitingLimit _ | ExitingMarket _ ->
                positions.[i] <-
                    { positions.[i] with
                        State = Exited (lastBar.date, lastBar.close, "mtm") }
