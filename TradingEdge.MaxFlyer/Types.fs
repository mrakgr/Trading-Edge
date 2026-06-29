module TradingEdge.MaxFlyer.Types

open System
open TradingEdge.MaxFlyer.RollingMa

// =============================================================================
// MaxFlyer DAILY layer — SELECTION ONLY (Gate 1).
//
// Copied from TradingEdge.HighFlyer.Types and debloated to the selection
// surface. This layer NEVER opens a trip: it folds D-1 daily bars and answers
// one question per bar — `PassesDailyFilter` — "is this a quality consolidating
// name going into day D?" The actual trading happens in the nested
// IntradaySystem (Intraday.fs) on day D's 1m stream.
//
// Removed vs HighFlyer: StopMode (all variants), Exhaustion/Disaster exits,
// the expansion exit, limit-entry, profit-target, Side (long-only here), and
// the daily Position/PositionState + Update/Process/Finalize trip machinery.
//
// KEPT verbatim: the rolling channels, the snapshot discipline, every read
// member used by selection, and `ProcessBar` (snapshot-before-push, no
// lookahead). The daily move/rvol gate is INTENTIONALLY dropped from Gate 1 —
// in MaxFlyer "the move" is the intraday/gap move on day D, decided by Gate 2
// (premarket) and Gate 3 (intraday), not by D-1's daily candle.
// =============================================================================

/// One daily bar, enriched with two day-D columns from the same DB row.
/// `rawClose` (= daily_prices.close) lets us recover the split-adjust ratio for
/// the intraday parquet; `premktVol` is the day-D premarket signal Gate 2 reads
/// (0, not absent, on illiquid days — no premarket trades is a real value).
/// The gap uses the daily ADJUSTED `open`, which equals the premarket table's
/// rth_open × adjRatio exactly (verified) — so no separate rthOpen field is
/// needed; `open` already IS the RTH open on the adjusted scale `prevAdjClose`
/// lives on.
type Bar =
    { date: DateOnly
      ``open``: float
      high: float
      low: float
      close: float
      volume: int64
      rawClose: float           // daily_prices.close (raw, pre-adjust) — for adjRatio
      premktVol: int64 }        // day-D premarket volume (04:00-09:29 ET); 0 if none

/// Daily Gate-1 selection thresholds. All are "is this a quality consolidating
/// name as of the prior close" — tightness / ATR% / 52w-proximity / price /
/// liquidity / volatility-history floor. NO same-day move/rvol gate (that's the
/// intraday/gap move, handled by Gates 2 & 3).
type DailyFilterConfig =
    { MinPriorDays: int         // require barsSeen > this (warmup; v0 21)
      MinAvgDollarVolume: float // min avg dollar volume 4w (v0 100_000)
      Min52wPct: float          // close >= this * (hiClose or hiHigh) (production 0.95)
      Use52wHigh: bool          // gate on prior-252d intraday HIGH instead of closing high (default false)
      MinPrice: float           // close >= this (production 1.0)
      MaxTightness: float       // tightness < this (production 4.5)
      MaxAtrPct: float          // atr_pct(close) < this — log-ATR (production 0.10)
      MinMaxAtrLog: float       // "max log ATR" (126-bar max of 14-bar log-ATR) >= this — past-runner
                                // volatility-history FLOOR; cuts dead-quiet-base names (production 0.04).
      // ----- Gate 2 (day-D premarket) — gap band + premarket-volume floors -----
      MinGapPct: float          // gapPct = rthOpen / prevAdjClose - 1 (WIDE by default; the intraday move)
      MaxGapPct: float
      MinPremktVol: int64       // min day-D premarket volume (absolute shares)
      MinPremktVolPctOfAvg: float } // min day-D premarket volume as a FRACTION of the 4w avg DAILY volume
                                    // (premktVol / avgVol >= this). Default 0.20 = premarket already traded
                                    // a fifth of a normal full day. Applied IN ADDITION to MinPremktVol.

/// A fully-qualified (ticker, day-D) candidate emitted by MaxFlyer.Process when
/// Gate 1 passed on D-1 AND Gate 2 passes on D. Carries the D-1 daily snapshots,
/// the day-D adjRatio (= adj_close_D / raw_close_D, to put the RAW intraday
/// parquet on the daily adjusted scale), and the already-decided gap%/premktVol.
type Candidate =
    { Ticker: string
      Date: DateOnly            // day D (the trading day)
      SignalDate: DateOnly      // D-1 (where Gate 1 fired)
      PrevAdjClose: float       // (D-1).adj_close
      AdjRatio: float           // (D).adj_close / (D).raw_close
      GapPct: float
      PremktVol: int64
      RthOpen: float            // day-D adjusted RTH open (= bar.open on day D)
      // Gate-1 snapshots (as of D-1)
      DailyAtrPct: float
      DailyTightness: float
      MaxAtrLog: float
      Pct52w: float
      Pct52wHigh: float
      AvgDolVol: float }

/// Per-ticker rolling-indicator state for the MaxFlyer DAILY selection layer.
/// (Named `MaxFlyer` — this is selection-only; there is no Qullamaggie-style
/// daily-low stop here, so the old `QullaSystem` name no longer fit.)
///
/// All windowed structures follow the "prior bars, exclude current" convention:
/// read `.State` (or the snapshot fields below) BEFORE pushing the current bar,
/// so every value measures the name's state going INTO the bar — no lookahead.
type MaxFlyer
    ( ticker: string,
      hiCloseWindow: int,
      atrWindow: int,
      tightnessWindow: int,
      volDays: int,
      cfg: DailyFilterConfig ) =

    // ----- rolling structures (selection-only subset of HighFlyer) -----
    let hiClose   = MaxMa(hiCloseWindow)        // long-term close channel (e.g. 252d), over CLOSES
    let hiHigh    = MaxMa(hiCloseWindow)        // long-term HIGH channel (252d max of intraday highs)
    let atrLog    = AvgMa(atrWindow)            // ATR = mean LOG true range over the window
    let atrLin    = AvgMa(atrWindow)            // ATR = mean ABSOLUTE true range (linear)
    // "max log ATR" = the MAX of the 14-bar log-ATR over the trailing 6 months (126
    // trading days). Past-runner / volatility-history floor. Fed atrLog.State each bar
    // (post-push), snapshotted pre-push.
    let maxAtrLog = MaxMa(126)
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
    let mutable sHiClose   : float voption = ValueNone
    let mutable sHiHigh    : float voption = ValueNone
    let mutable sAtrLog    : float voption = ValueNone
    let mutable sMaxAtrLog : float voption = ValueNone
    let mutable sAtrLin    : float voption = ValueNone
    let mutable sRangeHigh : float voption = ValueNone
    let mutable sRangeLow  : float voption = ValueNone
    let mutable sPrevClose : float voption = ValueNone
    let mutable sAvgVol    : float voption = ValueNone
    let mutable sAvgDolVol : float voption = ValueNone

    member _.BarsSeen = barsSeen
    /// This system's ticker. Public (not the private ctor arg) so the `inline` Process
    /// can read it at the call site.
    member _.Ticker = ticker
    /// Prior bar's close (pre-push snapshot) = the prior adj close, the gap denominator.
    /// Public (not a private `let`) so the `inline` Process can read it at the call site.
    member _.PrevCloseSnapshot = sPrevClose
    /// The daily filter / Gate-2 thresholds. Public for the same `inline` reason.
    member _.Cfg = cfg

    /// Long-term close channel: highest close over the prior `hiCloseWindow` bars.
    member _.HiClose = sHiClose
    /// Prior 252d closing high (raw snapshot) — for the trips CSV.
    member _.HiHigh = sHiHigh
    /// How far the given close sits ABOVE the prior 252d closing high:
    /// `close / hi_252_prior - 1`. ValueNone before the channel is warm / on 0.
    member _.Pct52w (closePrice: float) =
        match sHiClose with
        | ValueSome hi when hi <> 0.0 -> ValueSome (closePrice / hi - 1.0)
        | _ -> ValueNone
    /// How far the given close sits above the prior 252d INTRADAY-high (max of highs):
    /// `close / hi_252_high - 1`. The true resistance reference. ValueNone before warm / on 0.
    member _.Pct52wHigh (closePrice: float) =
        match sHiHigh with
        | ValueSome hi when hi <> 0.0 -> ValueSome (closePrice / hi - 1.0)
        | _ -> ValueNone
    /// ATR(14) in LOG space: mean log-true-range over the prior `atrWindow` bars.
    /// Directly an ATR% stand-in (log-true-range is a relative measure; no close division).
    member _.AtrLog = sAtrLog
    /// ATR(14) in LINEAR space: mean ABSOLUTE true range (dollars/bar). Linear tightness only.
    member _.AtrLin = sAtrLin
    /// ATR% stand-in = the log ATR directly (a ~percentage of price per bar).
    member _.AtrPct = sAtrLog
    /// "max log ATR" = MAX of the 14-bar log-ATR over the trailing 126 bars (~6mo),
    /// snapshotted pre-push (no lookahead). Past-runner / volatility-history floor.
    member _.MaxAtrLog = sMaxAtrLog
    /// 14-day ABSOLUTE price span = max high - min low (dollars).
    member _.RangeAbs =
        match sRangeHigh, sRangeLow with
        | ValueSome hi, ValueSome lo -> ValueSome (hi - lo)
        | _ -> ValueNone
    /// Consolidation tightness = abs range / abs ATR (LINEAR). Low = coiled spring,
    /// high = trending/expanding. Linear separates the loose-base losing tail far more
    /// cleanly than log (the HighFlyer-confirmed default; log mode was removed).
    member this.Tightness =
        match this.RangeAbs, sAtrLin with
        | ValueSome r, ValueSome atr when atr <> 0.0 -> ValueSome (r / atr)
        | _ -> ValueNone
    /// 4-week trailing average share volume (v0 `avg_volume_4w`).
    member _.AvgVolume = sAvgVol
    /// 4-week trailing average dollar volume (v0 `avg_dollar_volume_4w`).
    member _.AvgDollarVolume = sAvgDolVol
    /// Relative volume = volume / 4-week avg volume (v0 `rvol`). ValueNone with no baseline.
    member _.Rvol (volume: float) =
        match sAvgVol with
        | ValueSome av when av <> 0.0 -> ValueSome (volume / av)
        | _ -> ValueNone
    /// Same-day return = close / prev close - 1 (v0 `pct_up`). ValueNone on the
    /// first bar / a 0 prior close.
    member _.PctUp (closePrice: float) =
        match sPrevClose with
        | ValueSome pc when pc <> 0.0 -> ValueSome (closePrice / pc - 1.0)
        | _ -> ValueNone

    /// Gate 1 — does this name pass the daily quality filter going INTO the next
    /// session? Reads pre-push snapshots, so it must be evaluated AFTER
    /// ProcessBar(bar). A missing (ValueNone) metric for any active gate fails
    /// (we don't trade what we can't measure). NO same-day move/rvol gate — that
    /// is the intraday/gap move (Gates 2 & 3).
    member this.PassesDailyFilter (bar: Bar) : bool =
        // MaxFlyer enters at the day-D OPEN (then intraday), so the daily quality
        // filter is measured against the OPEN — the decision-time price — NOT the
        // close (which it will never trade at). This differs from HighFlyer, whose
        // entry filled at the close, so close was correct there.
        let c = bar.``open``
        let inline gate (v: float voption) (test: float -> bool) =
            match v with ValueSome x -> test x | ValueNone -> false
        // enough prior history (warmup)
        barsSeen > cfg.MinPriorDays
        // liquidity floor on avg dollar volume
        && gate sAvgDolVol (fun adv -> adv >= cfg.MinAvgDollarVolume)
        // 52-week-high proximity band — on the closing-high channel (default) or the
        // intraday-high channel (Use52wHigh = true; stricter "above true resistance")
        && gate (if cfg.Use52wHigh then sHiHigh else sHiClose)
                (fun hi -> c >= cfg.Min52wPct * hi)
        // price floor
        && c >= cfg.MinPrice
        // consolidation tightness
        && gate this.Tightness (fun t -> t < cfg.MaxTightness)
        // ATR% cap (log-space)
        && gate this.AtrPct (fun a -> a < cfg.MaxAtrPct)
        // past-runner volatility-history FLOOR: max log ATR >= MinMaxAtrLog.
        // ValueNone (insufficient history) fails the gate.
        && gate this.MaxAtrLog (fun ma -> ma >= cfg.MinMaxAtrLog)

    /// Update every rolling structure with the most recent bar. Snapshots are
    /// taken BEFORE the push (prior-bars / no-lookahead), then the bar is folded
    /// in for the next call. (Verbatim from HighFlyer, minus the removed channels.)
    member _.ProcessBar (bar: Bar) =
        // 1) snapshot the pre-bar state (what the system "knew" entering this bar).
        //    Calendar means evicted as-of THIS bar's date first.
        avgVol.Evict    bar.date
        avgDolVol.Evict bar.date
        sAvgVol    <- avgVol.State
        sAvgDolVol <- avgDolVol.State
        sHiClose   <- hiClose.State
        sHiHigh    <- hiHigh.State
        sAtrLog    <- atrLog.State
        sMaxAtrLog <- maxAtrLog.State
        sAtrLin    <- atrLin.State
        sRangeHigh <- rangeHigh.State
        sRangeLow  <- rangeLow.State
        sPrevClose <- prevClose

        // 2) fold the current bar in. Both LOG and LINEAR ATRs updated each bar
        //    so the tightness mode is a pure read-time switch.
        match prevClose with
        | ValueSome pc when bar.high > 0.0 && bar.low > 0.0 && pc > 0.0 ->
            (max bar.high pc - min bar.low pc)
            |> atrLin.Push
            log (max bar.high pc / min bar.low pc)
            |> atrLog.Push
            match atrLog.State with
            | ValueSome a -> maxAtrLog.Push a
            | ValueNone -> ()
        | _ -> ()

        hiClose.Push   bar.close
        hiHigh.Push    bar.high
        rangeHigh.Push bar.high
        rangeLow.Push  bar.low
        let vol = float bar.volume
        avgVol.Push    (bar.date, vol)
        avgDolVol.Push (bar.date, bar.close * vol)

        prevClose <- ValueSome bar.close
        barsSeen  <- barsSeen + 1

    /// Fold one daily bar (day D) for this ticker, emitting a `Candidate` via
    /// `onNext` when day D passes BOTH Gate 1 (daily quality, from D's pre-push
    /// snapshots = strictly-prior = through yesterday's close) AND Gate 2 (D's
    /// premarket gap + volume). The whole daily pipeline lives here; the caller
    /// just feeds bars in `(ticker, date)` order — no carried mutables, no lookback.
    ///
    /// Order (no-lookahead): (1) ProcessBar folds D in — but only AFTER snapshotting
    /// the strictly-prior state, so the `s*` snapshots + read-members now describe
    /// "going into D" (i.e. through D-1). (2) Gate 1 reads those snapshots, so it is
    /// effectively as-of D-1's close — D's own close never enters the filter. (3)
    /// Gate 2 reads D's premarket columns + the prior adj close (`sPrevClose`). Both
    /// gates decide on information knowable at D's open, so a candidate emitted for D
    /// is tradeable from D's open with no peeking at D's outcome.
    member inline this.Process(onNext: Candidate -> unit, bar: Bar) =
        // (1) advance the engine through day D (snapshot strictly-prior -> fold D).
        this.ProcessBar bar

        // (2)+(3) both gates on day D. Gate 1 reads pre-push snapshots (through D-1);
        // Gate 2 reads D's premarket. PrevCloseSnapshot is D-1's adj close (gap denominator).
        let cfg = this.Cfg
        match this.PrevCloseSnapshot with
        | ValueSome prevAdjClose when
                this.PassesDailyFilter bar
                && prevAdjClose <> 0.0 && bar.rawClose <> 0.0 ->
            // bar.open is the day-D ADJUSTED RTH open (== premarket rth_open * adjRatio,
            // verified), on the same adjusted scale as prevAdjClose.
            // Gate 1 passed => every gated snapshot is ValueSome; the .Value reads are safe.
            let gapPct = bar.``open`` / prevAdjClose - 1.0
            // Premarket volume as a fraction of the 4w avg DAILY volume (avgVol). avgVol is
            // ValueSome here (Gate 1's ADV floor passed implies the volume window is warm).
            let premktVolPctOfAvg =
                match this.AvgVolume with
                | ValueSome av when av > 0.0 -> float bar.premktVol / av
                | _ -> 0.0
            if gapPct >= cfg.MinGapPct && gapPct <= cfg.MaxGapPct
               && bar.premktVol >= cfg.MinPremktVol
               && premktVolPctOfAvg >= cfg.MinPremktVolPctOfAvg then
                onNext
                    { Ticker = this.Ticker
                      Date = bar.date
                      SignalDate = bar.date
                      PrevAdjClose = prevAdjClose
                      AdjRatio = bar.close / bar.rawClose      // (D).adj_close / (D).raw_close
                      GapPct = gapPct
                      PremktVol = bar.premktVol
                      RthOpen = bar.``open``
                      DailyAtrPct = this.AtrPct.Value
                      DailyTightness = this.Tightness.Value
                      MaxAtrLog = this.MaxAtrLog.Value
                      // Relative to the OPEN (the decision-time price), not the close —
                      // D's close isn't knowable at D's open.
                      Pct52w = (this.Pct52w bar.``open``).Value
                      Pct52wHigh = (this.Pct52wHigh bar.``open``).Value
                      AvgDolVol = this.AvgDollarVolume.Value }
        | _ -> ()
