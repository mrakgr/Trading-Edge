module TradingEdge.TideFlyer.Types

open System
open TradingEdge.RollingMa

// =============================================================================
// TideFlyer DAILY swing mean-reversion engine (copied from HighFlyerV2).
//
// Forked from TradingEdge.MaxFlyer.Types (the debloated selection skeleton) and
// brought back to PARITY with the original TradingEdge.HighFlyer daily system —
// but expressed in MaxFlyer's clean style.
//
// Kept from the original HighFlyer:
//   - the full entry factor stack: move band (up[lo,hi)) + rvol band + the
//     quality filters (52w-proximity, price, tightness, ATR%, intraday-ret floor,
//     past-runner max-log-ATR floor, ADV floor, warmup) — read at the SIGNAL bar,
//     filling at the bar CLOSE.
//   - the 252d LOW channels + the Pct52wLow* read members (CSV columns).
//   - a daily Position life-cycle: fill@close -> hold -> time-stop@next-open / MTM.
//
// Removed vs the original HighFlyer (the bloat):
//   - StopMode (ALL variants, incl. NoStop — NoStop == a time-stop with an
//     infinite cap, so there is NO price-stop machinery at all here).
//   - Side (long-only), exhaustion/disaster/expansion exits, limit-entry,
//     profit-target, and every rolling structure / position field tied to them.
//
// The only EXIT is the time-stop (exit at the next open after MaxHoldBars holding
// bars); anything still open at the last bar is MTM'd. `stopLow`/`stopHigh` and
// `EntryDayStopRef`/`StopLowAtEntry` survive ONLY as CSV snapshot columns (the
// golden output carries them); no logic consumes them.
// =============================================================================

/// One daily bar.
type Bar =
    { date: DateOnly
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64 }

/// In-engine entry filter thresholds. The original HighFlyer `is_entry`:
/// breakout move band + rvol band + history + ADV + 52w-proximity + the
/// production price / tightness / ATR% / intraday-ret / past-runner filters.
/// breadth_lag1 is NOT here — market-wide, applied post-hoc.
type EntryConfig =
    { UpThreshold: float        // min same-day return, close/prevClose-1 (production 0.10)
      MaxUpThreshold: float     // MAX same-day return (cap the 30%+ blow-off; production 0.30)
      RvolMin: float            // min rvol, volume/avgVol4w (production 5.0)
      RvolMax: float            // max rvol (production +inf)
      MinPriorDays: int         // require barsSeen > this (warmup; v0 21)
      MinAvgDollarVolume: float // min avg dollar volume 4w (v0 100_000)
      Min52wPct: float          // close >= this * (hiClose or hiHigh) (production 0.95)
      Use52wHigh: bool          // gate on prior-252d intraday HIGH instead of closing high (default false)
      MinPrice: float           // close >= this (production 1.0)
      MaxTightness: float       // tightness < this (LINEAR scale). TideFlyer production 9.0 (Run 14: a loose
                                // sanity cap trimming the >9 trending-knife tail; tightness is a NON-lever otherwise).
      MinAtrPct: float          // atr_pct(close) >= this — log-ATR FLOOR (TideFlyer production 0.08; Run 14).
                                // INVERTS HighFlyer: a washout-MR book wants VIOLENT dislocations (high ATR% =
                                // sharp reversible dislocation), not calm coiled springs. 0/-inf disables.
      MaxAtrPct: float          // atr_pct(close) < this — log-ATR CEILING (TideFlyer production 0.25; Run 14:
                                // cut the >0.25 falling-knife, PF 1.52/44.8% win = genuine death not a dip).
      MinIntradayRet: float     // close/open-1 >= this — reject deep intraday FADES (production -0.07; -inf disables)
      MinMaxAtrLog: float       // "max log ATR" (126-bar max of 14-bar log-ATR) >= this —
                                // past-runner volatility-history FLOOR (production 0.04; 0/-inf disables)
      // ----- TideFlyer 7d-channel entry (the core new signal) -----
      LowWindow: int            // rolling CLOSE-channel window (default 7). PRIOR-window convention:
                                // the min/max is over the prior `LowWindow` closes, EXCLUDING today.
      Mirror: bool              // false (default) = LONG-MR: buy the new 7d LOW (close <= prior-7 min close).
                                // true = MIRROR/momentum: buy the new 7d HIGH (close >= prior-7 max close).
      RequireChannel: bool      // true (default) = gate on the 7d channel (the TideFlyer signal). false =
                                // OFF (study the raw pre-channel population, e.g. to sweep the other gates).
      // ----- volume-fraction band: entry_vol / prior-7 vol-max (Run 4) -----
      VolFracMin: float         // require entry_vol / vol_max_7d >= this (default 0.5 — cut the quiet slow-bleed
                                // tail). 0 disables the floor.
      VolFracMax: float         // require entry_vol / vol_max_7d <= this (default 1.5 — cut the panic-spike
                                // falling knife). +inf disables the ceiling. A dip on ORDINARY volume reverts best.
      Max3dReturn: float         // require close/close-3d-1 <= this (default -0.15 — a real 3-day WASHOUT;
                                // Run 5: 3d deeper=better, no knife). +inf disables. A CEILING only.
      MaxPrior2dReturn: float    // require the TRUE prior-2-day return close[t−1]/close[t−3]−1 <= this —
                                // the PRIOR-2-DAY fall going INTO today, i.e. the name was ALREADY sliding
                                // before today's flush (Run 9; principled form Run 12, replacing the (3d−1d)
                                // diff-of-ratios proxy that overstated the fall ~1pp). Default -0.10. Monotone
                                // deeper=better; the >+5%-then-flush cell is a bull-trap LOSS. +inf disables. CEILING.
      Max60dReturn: float }       // require close/close-60d-1 <= this — a deep 60-day WASHOUT (Run 11).
                                // Default -0.40. TideFlyer is a WASHOUT book, NOT a pullback book: the
                                // prior-2d "already sliding" gate selects freefall names, and within that
                                // deeper-is-better on EVERY horizon (the uptrend-pullback cohort doesn't
                                // survive the gate). 60d<-40% = 16.7k trips @ PF ~1.96 (best trips/PF cell).
                                // +inf disables. A CEILING only.

/// Life-cycle of a single trip. Minimal: the original HighFlyer's PendingLimit /
/// ExitingLimit / ExitingMarket / Side machinery is gone. A trip fills at the
/// signal-bar close (Holding immediately), exits at the next open when the
/// time-stop fires (ExitingNextOpen), or is MTM'd at the last bar.
type PositionState =
    | Holding
    | ExitingNextOpen of reason: string   // time-stop fired; fill at the next bar's open
    | Exited of exitDate: DateOnly * exitPrice: float * reason: string

/// One trip. The *_at_entry metrics are snapshotted at the SIGNAL bar (= the fill
/// bar, since we fill at close) and fixed. EntryDayStopRef / StopLowAtEntry are
/// carried for CSV parity with the original output; no stop logic reads them.
type Position =
    { SignalDate: DateOnly
      EntryDate: DateOnly       // = SignalDate (fill at the signal-bar close)
      EntryPrice: float         // = signal-bar close
      EntryReason: string       // always "close"
      EntryDayStopRef: float    // FILL bar's low (CSV column; unused by logic)
      StopLowAtEntry: float     // trailing prior-window low going INTO the signal bar (CSV column; unused); nan if cold
      HoldBars: int             // # of Holding bars since fill (for the time-stop)
      // metrics snapshotted at the SIGNAL bar (CSV / parity)
      EntryVolume: int64
      VolMaxAtEntry: float       // prior-7 volume MAX going into the signal bar (vol-fraction denominator); nan if cold
      RvolAtEntry: float
      AvgDollarVolumeAtEntry: float
      PctUpAtEntry: float
      AtrPctAtEntry: float
      TightnessAtEntry: float
      Pct52wAtEntry: float
      Pct52wHighAtEntry: float
      Pct52wLowCloseAtEntry: float
      Pct52wLowAtEntry: float
      State: PositionState }

/// Per-ticker rolling-indicator state + the daily trip life-cycle.
///
/// All windowed structures follow the "prior bars, exclude current" convention:
/// read `.State` (or the `s*` snapshots) BEFORE pushing the current bar, so every
/// value measures the name's state going INTO the bar — no lookahead.
type TideFlyer
    ( stopLowWindow: int,
      hiCloseWindow: int,
      atrWindow: int,
      tightnessWindow: int,
      volDays: int,
      maxHoldBars: int,
      targetExit: bool,
      entryCfg: EntryConfig ) =

    // ----- rolling structures -----
    let stopLow   = MinMa(stopLowWindow)        // CSV snapshot only (prior-window low at entry)
    let tideLo    = MinMa(entryCfg.LowWindow)   // TideFlyer 7d-CLOSE low channel (buy the new 7d low)
    let tideHi    = MaxMa(entryCfg.LowWindow)   // TideFlyer 7d-CLOSE high channel (buy the new 7d high / exit target)
    let close3d   = LagMa<float>(3)             // close ring → 3d return via lagPctChange (Run 5: deep 3d washout)
    let close60d  = LagMa<float>(60)            // close ring → 60d return (Run 11: deep 60d washout, the washout gate)
    let hiClose   = MaxMa(hiCloseWindow)        // long-term close channel (252d)
    let hiHigh    = MaxMa(hiCloseWindow)        // long-term HIGH channel (252d max of intraday highs)
    let loClose   = MinMa(hiCloseWindow)        // long-term LOW close channel (252d min of closes)
    let loLow     = MinMa(hiCloseWindow)        // long-term LOW channel (252d min of intraday lows)
    let atrLog    = AvgMa(atrWindow)            // ATR = mean LOG true range
    let atrLin    = AvgMa(atrWindow)            // ATR = mean ABSOLUTE true range (linear, for tightness)
    let maxAtrLog = MaxMa(126)                  // past-runner: 126-bar max of the 14-bar log-ATR
    let rangeHigh = MaxMa(tightnessWindow)      // tightness: max high
    let rangeLow  = MinMa(tightnessWindow)      // tightness: min low
    let avgVol    = AvgMa(volDays)              // AVG(volume) over the prior `volDays` BARS
    let avgDolVol = AvgMa(volDays)              // AVG(close*volume) over the prior `volDays` BARS
    let volMax    = MaxMa(entryCfg.LowWindow)   // MAX(volume) over the prior LowWindow (7) BARS — recorded on
                                                // the trip so a "volume fraction" (entry vol / 7d vol max) and
                                                // rvol are post-hoc levers (mirrors LowFlyer's vol_vs_high idea)

    // Open + closed trips for this ticker, in entry order.
    let positions = ResizeArray<Position>()

    let mutable barsSeen  = 0
    // The last full bar folded in. Doubles as the prior close (lastBar.close) for
    // the true-range fold and the pre-push snapshot, the anchor for the >45d
    // listing-gap check, and the bar we MTM-close open trips at on a gap. ValueNone
    // before the first bar (so a fresh ticker never triggers a spurious gap reset).
    let mutable lastBar   : Bar voption = ValueNone

    // ----- snapshots captured at the last ProcessBar, BEFORE the push -----
    let mutable sStopLow   : float voption = ValueNone
    let mutable sTideLo    : float voption = ValueNone   // prior-7 min close (the new-7d-low reference)
    let mutable sTideHi    : float voption = ValueNone   // prior-7 max close (the new-7d-high reference / exit target)
    let mutable sHiClose   : float voption = ValueNone
    let mutable sHiHigh    : float voption = ValueNone
    let mutable sLoClose   : float voption = ValueNone
    let mutable sLoLow     : float voption = ValueNone
    let mutable sAtrLog    : float voption = ValueNone
    let mutable sMaxAtrLog : float voption = ValueNone
    let mutable sAtrLin    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone
    let mutable sPrevBar   : Bar voption = ValueNone
    let mutable sAvgVol    : float voption = ValueNone
    let mutable sAvgDolVol : float voption = ValueNone
    let mutable sVolMax    : float voption = ValueNone   // prior-7 volume max (the vol-fraction denominator)

    member _.BarsSeen = barsSeen

    /// Trailing prior-window low (CSV snapshot reference).
    member _.StopLow = sStopLow
    /// TideFlyer 7d channel (prior-window min/max CLOSE), pre-push snapshots.
    member _.TideLo = sTideLo
    member _.TideHi = sTideHi
    /// 3d return = today's close / close-3-bars-ago − 1 (read AFTER ProcessBar's push;
    /// ValueNone until warm). Uses the daily bar's close — in the parity path that IS
    /// the entry candle's close; the 3d washout is a daily-context gate either way.
    member _.Chg3d = lagPctChange close3d
    /// 60d return = today's close / close-60-bars-ago − 1 (read AFTER ProcessBar's push; the
    /// long-term-trend / washout context). ValueNone until warm.
    member _.Chg60d = lagPctChange close60d
    /// TRUE prior-2-day return = close[t−1] / close[t−3] − 1 (a real sub-period return; Run 12).
    /// NOT the (3d − 1d) diff-of-ratios proxy — that overstated the fall by ~1pp (the compounding
    /// cross-term). Read AFTER ProcessBar's push: close3d (lag 3) then holds {t−3,t−2,t−1,t} so
    /// `.Lagged` = close[t−3]; sPrevBar = the pre-push lastBar snapshot = bar t−1. ValueNone until
    /// ≥4 bars pushed (needs close[t−3]) and a prior bar exists.
    member _.Prior2dReturn : float voption =
        match sPrevBar, close3d.Lagged with
        | ValueSome pb, ValueSome c3 when c3 > 0.0 -> ValueSome (pb.close / c3 - 1.0)
        | _ -> ValueNone
    /// Long-term close channel: highest close over the prior `hiCloseWindow` bars.
    member _.HiClose = sHiClose
    member _.HiHigh = sHiHigh
    /// close / hi_252_closing - 1 (how far above the prior 252d closing high).
    member _.Pct52w (closePrice: float) =
        match sHiClose with
        | ValueSome hi when hi <> 0.0 -> ValueSome (closePrice / hi - 1.0)
        | _ -> ValueNone
    /// close / hi_252_intraday_high - 1 (true resistance).
    member _.Pct52wHigh (closePrice: float) =
        match sHiHigh with
        | ValueSome hi when hi <> 0.0 -> ValueSome (closePrice / hi - 1.0)
        | _ -> ValueNone
    /// close / lo_252_closing - 1 (relative to the prior 252d closing low).
    member _.Pct52wLowClose (closePrice: float) =
        match sLoClose with
        | ValueSome lo when lo <> 0.0 -> ValueSome (closePrice / lo - 1.0)
        | _ -> ValueNone
    /// close / lo_252_intraday_low - 1 (true support).
    member _.Pct52wLow (closePrice: float) =
        match sLoLow with
        | ValueSome lo when lo <> 0.0 -> ValueSome (closePrice / lo - 1.0)
        | _ -> ValueNone
    /// ATR(14) in LOG space — a direct ATR% stand-in (no close division).
    member _.AtrLog = sAtrLog
    member _.AtrLin = sAtrLin
    /// ATR% stand-in = the log ATR directly.
    member _.AtrPct = sAtrLog
    /// 126-bar max of the 14-bar log-ATR (past-runner floor), pre-push.
    member _.MaxAtrLog = sMaxAtrLog
    /// 14-day ABSOLUTE price span (dollars).
    member _.RangeAbs =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo -> ValueSome (hi - lo)
        | _ -> ValueNone
    /// Consolidation tightness = abs range / abs ATR (LINEAR). Low = coiled
    /// spring, high = trending. Linear is the HighFlyer-confirmed default.
    member this.Tightness =
        match this.RangeAbs, sAtrLin with
        | ValueSome r, ValueSome atr when atr <> 0.0 -> ValueSome (r / atr)
        | _ -> ValueNone
    member _.AvgVolume = sAvgVol
    member _.AvgDollarVolume = sAvgDolVol
    /// prior-`LowWindow` volume MAX (pre-push) — the vol-fraction denominator.
    member _.VolMax = sVolMax
    /// rvol = volume / 4-week avg volume.
    member _.Rvol (volume: float) =
        match sAvgVol with
        | ValueSome av when av <> 0.0 -> ValueSome (volume / av)
        | _ -> ValueNone
    /// Same-day return = close / prev close - 1.
    member _.PctUp (closePrice: float) =
        match sPrevBar with
        | ValueSome pb when pb.close <> 0.0 -> ValueSome (closePrice / pb.close - 1.0)
        | _ -> ValueNone

    /// Does this entry candle trigger an entry? Reads pre-push snapshots, so it
    /// must be evaluated AFTER ProcessBar(dailyBar). Mirrors the original
    /// HighFlyer `ShouldEnter`. A missing (ValueNone) metric for any active gate
    /// fails the entry (we don't trade what we can't measure).
    ///
    /// `bar` is the ENTRY candle whose own OHLCV drive the decision + fill — the
    /// full daily bar in the parity path, or the 10:00 ET partial candle in the
    /// early-entry experiment. EVERY candle-value gate (move, rvol, 52w-proximity
    /// close, price floor, intraday-ret close/open) reads it. The prior-day
    /// snapshots (rvol baseline, 52w channels, tightness, ATR%, ADV, past-runner)
    /// are unaffected by the cutoff — they measure state going INTO the day.
    member this.ShouldEnter (bar: Bar) : bool =
        let c = bar.close
        let inline gate (v: float voption) (test: float -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // TideFlyer CORE signal: a new 7-day CLOSE extreme vs the PRIOR `LowWindow` closes.
        //   long-MR (default): close <= prior-7 min close (a new 7d LOW — the dip we fade)
        //   mirror:            close >= prior-7 max close (a new 7d HIGH — the momentum control)
        // ValueNone (cold) fails. Off when RequireChannel=false (raw-population study).
        (not entryCfg.RequireChannel
         || gate (if entryCfg.Mirror then sTideHi else sTideLo)
                 (fun ch -> if entryCfg.Mirror then c >= ch else c <= ch))
        // 1d-return BAND: close/prevClose-1 in [UpThreshold, MaxUpThreshold). For long-MR the
        // production band is [-40%, -5%) — a real down-day into the low, above the falling knife.
        && gate (this.PctUp c) (fun pu -> pu >= entryCfg.UpThreshold && pu < entryCfg.MaxUpThreshold)
        // VOLUME-FRACTION band (Run 4): entry_vol / prior-7 vol-max in [VolFracMin, VolFracMax].
        // Production [0.5, 1.5] — a dip on ORDINARY volume; cut the quiet slow-bleed (<0.5) and the
        // panic-spike falling knife (>=~2.5). ValueNone vol-max (cold) fails when a bound is active.
        && (let active = entryCfg.VolFracMin > 0.0 || not (Double.IsInfinity entryCfg.VolFracMax)
            not active
            || gate sVolMax (fun vm ->
                   vm > 0.0 &&
                   let vf = float bar.volume / vm
                   vf >= entryCfg.VolFracMin && vf <= entryCfg.VolFracMax))
        // 3d-return CEILING (Run 5): require close/close-3d-1 <= Max3dReturn — a real multi-day
        // washout (production -0.15). 3d has NO falling knife (deeper = better), so only a ceiling.
        // +inf disables. ValueNone (cold) fails when active.
        && (Double.IsInfinity entryCfg.Max3dReturn
            || gate this.Chg3d (fun r -> r <= entryCfg.Max3dReturn))
        // PRIOR-2-DAY-fall CEILING (Run 9, principled form Run 12): require the TRUE prior-2d return
        // close[t−1]/close[t−3]−1 <= MaxPrior2dReturn — the prior 2 days ALREADY fell this much before
        // today's flush (already sliding, not a bolt-from-the-blue crack; the >+5%-then-flush cell is a
        // bull-trap loss). production -0.10. +inf disables. ValueNone (cold) fails when active.
        && (Double.IsInfinity entryCfg.MaxPrior2dReturn
            || gate this.Prior2dReturn (fun r -> r <= entryCfg.MaxPrior2dReturn))
        // 60d-return CEILING (Run 11): require close/close-60d-1 <= Max60dReturn — a deep 60-day WASHOUT
        // (production -0.40). TideFlyer is a WASHOUT book: deeper-is-better on every horizon, so a ceiling
        // only. +inf disables. ValueNone (cold, <60 bars of history) fails when active.
        && (Double.IsInfinity entryCfg.Max60dReturn
            || gate this.Chg60d (fun r -> r <= entryCfg.Max60dReturn))
        // rvol band
        && gate (this.Rvol (float bar.volume)) (fun rv ->
               rv >= entryCfg.RvolMin && rv <= entryCfg.RvolMax)
        // enough prior history
        && barsSeen > entryCfg.MinPriorDays
        // liquidity floor on avg dollar volume
        && gate sAvgDolVol (fun adv -> adv >= entryCfg.MinAvgDollarVolume)
        // 52-week-high proximity band — closing-high (default) or intraday-high channel
        && gate (if entryCfg.Use52wHigh then sHiHigh else sHiClose)
                (fun hi -> c >= entryCfg.Min52wPct * hi)
        // price floor
        && c >= entryCfg.MinPrice
        // consolidation tightness
        && gate this.Tightness (fun t -> t < entryCfg.MaxTightness)
        // ATR% cap (log-space)
        && gate this.AtrPct (fun a -> a < entryCfg.MaxAtrPct)
        // ATR% FLOOR (Run 14): require log-ATR >= MinAtrPct. INVERTS HighFlyer's cap — a washout-MR book
        // wants VIOLENT dislocations (high ATR% snaps back hardest); the quiet slow-bleed tail (<0.08) limps.
        // production 0.08. 0/-inf disables. ValueNone (cold) fails when active.
        && (entryCfg.MinAtrPct <= 0.0
            || gate this.AtrPct (fun a -> a >= entryCfg.MinAtrPct))
        // intraday-return floor: reject deep fades (gap-up then sell-off). Guard open>0.
        && (bar.``open`` > 0.0 && bar.close / bar.``open`` - 1.0 >= entryCfg.MinIntradayRet)
        // past-runner volatility-history FLOOR. ValueNone fails.
        && gate this.MaxAtrLog (fun ma -> ma >= entryCfg.MinMaxAtrLog)

    /// Clear all rolling indicator state so the next bar starts a COLD episode —
    /// identical to a freshly-constructed HighFlyer, EXCEPT the `positions` ledger
    /// is preserved (the pre-gap episode's now-closed trips stay for harvest). Every
    /// windowed structure is reset (atrLin included — else tightness leaks across the
    /// gap), plus lastBar (no cross-gap prior-close / true range) and barsSeen (warmup
    /// re-arms). Callers that need the pre-gap bar must read it BEFORE calling this.
    member private _.ResetIndicators () =
        stopLow.Reset ();   tideLo.Reset ();    tideHi.Reset ();  close3d.Reset (); close60d.Reset ()
        hiClose.Reset ();   hiHigh.Reset ()
        loClose.Reset ();   loLow.Reset ()
        atrLog.Reset ();    atrLin.Reset ();    maxAtrLog.Reset ()
        rangeHigh.Reset (); rangeLow.Reset ()
        avgVol.Reset ();    avgDolVol.Reset ();  volMax.Reset ()
        lastBar   <- ValueNone
        barsSeen  <- 0

    /// Update every rolling structure with the most recent bar. Snapshots are
    /// taken BEFORE the push (prior-bars / no-lookahead), then the bar is folded
    /// in for the next call.
    member this.ProcessBar (bar: Bar) =
        // 0) >45d listing-gap sever (mirrors live_scan.py's `date - LAG(date) > 45`).
        // A recycled ticker (old co. then a NEW co. under the same symbol after a
        // multi-year gap) must not let rolling windows span the gap. On a gap we
        // (a) MTM-close any still-open trips at the LAST pre-gap bar (end-of-episode,
        // the same rule Finalize applies at end-of-series), then (b) reset every
        // indicator so this bar starts cold. positions is NOT cleared — the just-
        // closed trips stay and are harvested at the next Backtest.flush(). Strictly
        // `> 45`, so exactly 45 calendar days does not trigger.
        match lastBar with
        | ValueSome prev when (bar.date.DayNumber - prev.date.DayNumber) > 45 ->
            this.Finalize prev          // MTM open trips at the pre-gap bar's close
            this.ResetIndicators ()
        | _ -> ()

        // 1) snapshot the pre-bar state.
        sAvgVol    <- avgVol.State
        sAvgDolVol <- avgDolVol.State
        sVolMax    <- volMax.State
        sStopLow   <- stopLow.State
        sTideLo    <- tideLo.State
        sTideHi    <- tideHi.State
        sHiClose   <- hiClose.State
        sHiHigh    <- hiHigh.State
        sLoClose   <- loClose.State
        sLoLow     <- loLow.State
        sAtrLog    <- atrLog.State
        sMaxAtrLog <- maxAtrLog.State
        sAtrLin    <- atrLin.State
        sRangeHigh <- rangeHigh.State
        sRangeLow  <- rangeLow.State
        sPrevBar   <- lastBar

        // 2) fold the current bar in. Both LOG and LINEAR ATRs updated each bar.
        match sPrevBar with
        | ValueSome pb when bar.high > 0.0 && bar.low > 0.0 && pb.close > 0.0 ->
            let pc = pb.close
            (max bar.high pc - min bar.low pc)
            |> atrLin.Push
            log (max bar.high pc / min bar.low pc)
            |> atrLog.Push
            match atrLog.State with
            | ValueSome a -> maxAtrLog.Push a
            | ValueNone -> ()
        | _ -> ()

        stopLow.Push   bar.low
        tideLo.Push    bar.close
        tideHi.Push    bar.close
        close3d.Push   bar.close
        close60d.Push  bar.close
        hiClose.Push   bar.close
        hiHigh.Push    bar.high
        loClose.Push   bar.close
        loLow.Push     bar.low
        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        let vol = float bar.volume
        avgVol.Push    vol
        avgDolVol.Push (bar.close * vol)
        volMax.Push    vol

        lastBar   <- ValueSome bar
        barsSeen  <- barsSeen + 1

    /// All trips for this ticker (open + closed), in entry order.
    member _.Positions = positions

    /// Advance one open position by the current bar. Must run AFTER ProcessBar.
    /// Two exit paths:
    ///   (a) TARGET (targetExit=true): the round-trip — long-MR sells when today's
    ///       close reaches a new 7d HIGH (c >= prior-7 max close); mirror sells at
    ///       the 7d LOW. Checked FIRST; fills at the NEXT bar's open. The time-stop
    ///       remains the fallback if the target never hits.
    ///   (b) TIME-STOP: once Holding for `maxHoldBars` bars (0 = off), exit next open.
    member _.Update (bar: Bar) (pos: Position) : Position =
        match pos.State with
        | Exited _ -> pos
        | ExitingNextOpen reason ->
            // sell at THIS bar's open (the bar after the exit fired).
            { pos with State = Exited (bar.date, bar.``open``, reason) }
        | Holding ->
            let heldBars = pos.HoldBars + 1
            // (a) target: reached the opposite 7d extreme? (long-MR -> 7d high; mirror -> 7d low)
            //     sTideHi/sTideLo are the pre-push prior-window snapshots for THIS bar.
            let targetHit =
                targetExit &&
                (if entryCfg.Mirror then
                     match sTideLo with ValueSome lo -> bar.close <= lo | ValueNone -> false
                 else
                     match sTideHi with ValueSome hi -> bar.close >= hi | ValueNone -> false)
            if targetHit then
                { pos with State = ExitingNextOpen "target"; HoldBars = heldBars }
            // (b) time-stop fallback: fires once HoldBars+1 reaches the cap.
            elif maxHoldBars > 0 && heldBars >= maxHoldBars then
                { pos with State = ExitingNextOpen "time_stop"; HoldBars = heldBars }
            else { pos with HoldBars = heldBars }

    /// Advance the whole system by one day: fold the DAILY bar into the
    /// indicators, update every open position (exits run on the daily series),
    /// then open a new trip when the entry predicate fires.
    ///
    /// `dailyBar` always drives the indicators + exits. `entryBar` is the candle
    /// whose own OHLCV drive the entry decision + fill: the daily bar in the parity
    /// path, or the 10:00 ET partial candle in the early-entry experiment. When the
    /// partial candle is missing for a tradeable day (no RTH bar before the cutoff —
    /// halted/illiquid), pass `entryBar = ValueNone` and no entry is taken that day.
    ///
    /// An entry candle is never its own first exit-check bar — the new position is
    /// appended AFTER existing positions advance, and its first time-stop tick is
    /// the NEXT daily bar.
    member this.Process (dailyBar: Bar, ?entryBar: Bar voption, ?breadthOk: bool) =
        this.ProcessBar dailyBar
        for i in 0 .. positions.Count - 1 do
            positions.[i] <- this.Update dailyBar positions.[i]
        let breadthOk = defaultArg breadthOk true
        // Default entry candle = the full daily bar (parity). The experiment passes
        // the partial candle; ValueNone = no tradeable entry candle this day.
        let entry =
            match defaultArg entryBar (ValueSome dailyBar) with
            | ValueSome b -> b
            | ValueNone -> { dailyBar with volume = 0L }   // sentinel; ShouldEnter below is gated off
        let hasEntryBar =
            match defaultArg entryBar (ValueSome dailyBar) with ValueSome _ -> true | ValueNone -> false
        if breadthOk && hasEntryBar && this.ShouldEnter entry then
            // ShouldEnter verified each gated metric is ValueSome, so these reads
            // are safe (nan only if a metric isn't gated — none of these are). All
            // candle-value reads use `entry`; the prior-day snapshots are shared.
            let orNan = function ValueSome v -> v | ValueNone -> nan
            // Tag the fill basis: "close" = full daily bar, "partial" = partial candle.
            let reason = if System.Object.ReferenceEquals(entry, dailyBar) then "close" else "partial"
            positions.Add
                { SignalDate = dailyBar.date
                  EntryDate = dailyBar.date
                  EntryPrice = entry.close
                  EntryReason = reason
                  EntryDayStopRef = entry.low
                  StopLowAtEntry = orNan sStopLow
                  HoldBars = 0
                  EntryVolume = entry.volume
                  VolMaxAtEntry = orNan sVolMax
                  RvolAtEntry = orNan (this.Rvol (float entry.volume))
                  AvgDollarVolumeAtEntry = orNan sAvgDolVol
                  PctUpAtEntry = orNan (this.PctUp entry.close)
                  AtrPctAtEntry = orNan this.AtrPct
                  TightnessAtEntry = orNan this.Tightness
                  Pct52wAtEntry = orNan (this.Pct52w entry.close)
                  Pct52wHighAtEntry = orNan (this.Pct52wHigh entry.close)
                  Pct52wLowCloseAtEntry = orNan (this.Pct52wLowClose entry.close)
                  Pct52wLowAtEntry = orNan (this.Pct52wLow entry.close)
                  State = Holding }

    /// Close any still-open positions at the final bar's close, marked-to-market
    /// (v0's "mtm" exit). Call once after the last bar of the series. Positions
    /// resting in ExitingNextOpen (time-stop fired on the last bar, no next bar to
    /// fill at) are also MTM'd here.
    member _.Finalize (lastBar: Bar) =
        for i in 0 .. positions.Count - 1 do
            match positions.[i].State with
            | Exited _ -> ()
            | Holding | ExitingNextOpen _ ->
                positions.[i] <-
                    { positions.[i] with
                        State = Exited (lastBar.date, lastBar.close, "mtm") }
