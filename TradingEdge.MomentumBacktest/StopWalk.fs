module TradingEdge.MomentumBacktest.StopWalk

open TradingEdge.MomentumBacktest.Types

/// Walk a single entry forward to its exit. Bar indexing (no-lookahead on exit):
///   - entry on bar T, filled at a.[T].adj_close (same-day close — user's choice)
///   - first eligible exit-check bar is T+1 (the entry bar is never a check bar)
///   - on each held bar D (>T) we check TWO exits and take whichever fires first:
///       * STOP: a.[D].adj_low <= a.[D].low_15_prior (15-day-low trailing stop;
///         low_15_prior is the min of the prior N lows, EXCLUDING D itself).
///       * EXPANSION: a.[D].tightness_14 > threshold — the held-day rolling
///         tightness range/(14*ATR) over D's prior 14 bars has risen above the
///         threshold, i.e. the name's volatility has EXPANDED / it is becoming
///         overextended. Same metric used to gate entries (<0.40), now as an exit.
///     Both signals are read from bar D's close, so the EXIT fills at a.[D+1].open
///     (don't exit into the trigger bar) — consistent with the stop's execution.
///   - if the trigger is on the last bar (no D+1), or neither ever fires, the
///     trip is still OPEN → mark-to-market at the final adj_close (reason "mtm").
let private walkOne (notional: float) (expansionThr: float option) (a: SignalRow[]) (t: int) : Trip =
    let n = a.Length
    let entry = a.[t]
    let entryPrice = entry.adj_close
    let qty = notional / entryPrice

    // Find the first exit-trigger bar strictly after the entry bar; record which
    // exit fired (stop vs expansion). If both fire on the same bar, the stop is
    // reported (downside protection takes precedence in the label; the fill is
    // the same next-open either way).
    let mutable d = t + 1
    let mutable reason = ""   // "" until a trigger fires
    while d < n && reason = "" do
        let bar = a.[d]
        let stopHit =
            bar.low_15_prior.HasValue && bar.adj_low <= bar.low_15_prior.Value
        let expansionHit =
            match expansionThr with
            | Some thr -> bar.tightness_14.HasValue && bar.tightness_14.Value > thr
            | None -> false
        if stopHit then reason <- "stop"
        elif expansionHit then reason <- "expansion"
        else d <- d + 1

    let exitIdx, exitPrice, isOpen, exitReason =
        if reason <> "" && d + 1 < n then
            // Normal exit: next bar's open.
            d + 1, a.[d + 1].adj_open, false, reason
        else
            // Triggered on the last bar (no next open) OR never triggered:
            // open trip, MTM at the final available close.
            n - 1, a.[n - 1].adj_close, true, "mtm"

    {
        Symbol = entry.ticker
        EntryDate = entry.date
        ExitDate = a.[exitIdx].date
        EntryPrice = entryPrice
        ExitPrice = exitPrice
        Qty = qty
        NetPnL = qty * (exitPrice - entryPrice)
        BarsHeld = exitIdx - t
        EntryAdjVolume = entry.adj_volume
        RvolAtEntry = (if entry.rvol.HasValue then entry.rvol.Value else nan)
        AvgDollarVolume4wAtEntry =
            (if entry.avg_dollar_volume_4w.HasValue then entry.avg_dollar_volume_4w.Value else nan)
        PctUpAtEntry = (if entry.pct_up.HasValue then entry.pct_up.Value else nan)
        AtrPct14AtEntry = (if entry.atr_pct_14.HasValue then entry.atr_pct_14.Value else nan)
        RangePct14AtEntry = (if entry.range_pct_14.HasValue then entry.range_pct_14.Value else nan)
        Tightness14AtEntry = (if entry.tightness_14.HasValue then entry.tightness_14.Value else nan)
        ExitReason = exitReason
        Open = isOpen
    }

/// All trips for one ticker's date-ordered series. Every is_entry bar opens an
/// independent trip — overlapping/concurrent trips on the same ticker are allowed
/// (uncapped, fixed-notional, no compounding), so we do NOT dedup or suppress
/// new entries while another is open.
let tripsForTicker (notional: float) (expansionThr: float option) (rows: SignalRow[]) : Trip[] =
    [| for t in 0 .. rows.Length - 1 do
         if rows.[t].is_entry then
             yield walkOne notional expansionThr rows t |]
