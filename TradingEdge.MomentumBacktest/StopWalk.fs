module TradingEdge.MomentumBacktest.StopWalk

open TradingEdge.MomentumBacktest.Types

/// Walk a single entry forward to its exit. Bar indexing (no-lookahead on exit):
///   - entry on bar T, filled at a.[T].adj_close (same-day close — user's choice)
///   - first eligible exit-check bar is T+1 (the entry bar is never a check bar)
///   - on each held bar D (>T) we check the active exits and take whichever fires
///     first (downside protection labelled first on ties; fill is the same):
///       * STOP: a.[D].adj_low <= a.[D].low_15_prior (15-day-low trailing stop).
///       * EXPANSION: a.[D].tightness_14 > expansionThr (volatility expanded).
///       * TIME: D - t >= timeStopBars (no runner within N held bars → bail).
///       * STALL: stallBars consecutive held bars with no new since-entry-high
///         CLOSE (momentum persistence broke). The since-entry-high reference
///         INCLUDES the entry close and updates each held bar from D's close.
///     All signals are read from bar D's close, so the EXIT fills at a.[D+1].open
///     (don't exit into the trigger bar) — consistent with the stop's execution.
///   - if the trigger is on the last bar (no D+1), or none ever fires, the trip
///     is still OPEN → mark-to-market at the final adj_close (reason "mtm").
let private walkOne
    (notional: float)
    (expansionThr: float option)
    (timeStopBars: int option)
    (stallBars: int option)
    (a: SignalRow[])
    (t: int)
    : Trip =
    let n = a.Length
    let entry = a.[t]
    let entryPrice = entry.adj_close
    let qty = notional / entryPrice

    // Stall state: highest close seen so far (incl. entry) and how many held bars
    // have passed since the last new high. Both updated per bar BEFORE the stall
    // test, using only data up to and including bar D (no lookahead).
    let mutable highClose = entry.adj_close
    let mutable barsSinceHigh = 0

    let mutable d = t + 1
    let mutable reason = ""   // "" until a trigger fires
    while d < n && reason = "" do
        let bar = a.[d]
        // update stall tracker from this bar's close
        if bar.adj_close > highClose then
            highClose <- bar.adj_close
            barsSinceHigh <- 0
        else
            barsSinceHigh <- barsSinceHigh + 1

        let stopHit =
            bar.low_15_prior.HasValue && bar.adj_low <= bar.low_15_prior.Value
        let expansionHit =
            match expansionThr with
            | Some thr -> bar.tightness_14.HasValue && bar.tightness_14.Value > thr
            | None -> false
        let timeHit =
            match timeStopBars with
            | Some nbars -> (d - t) >= nbars
            | None -> false
        let stallHit =
            match stallBars with
            | Some k -> barsSinceHigh >= k
            | None -> false

        if stopHit then reason <- "stop"
        elif expansionHit then reason <- "expansion"
        elif timeHit then reason <- "time"
        elif stallHit then reason <- "stall"
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

/// Does an entry bar pass the optional tightness/ATR entry filters? A NaN/missing
/// metric fails a filter that is set (we don't trade what we can't measure).
let private passesEntryFilters (cfg: Config) (r: SignalRow) : bool =
    let okTight =
        match cfg.MaxTightnessAtEntry with
        | Some mx -> r.tightness_14.HasValue && r.tightness_14.Value <= mx
        | None -> true
    let okAtr =
        match cfg.MaxAtrPctAtEntry with
        | Some mx -> r.atr_pct_14.HasValue && r.atr_pct_14.Value <= mx
        | None -> true
    okTight && okAtr

/// All trips for one ticker's date-ordered series. Every is_entry bar that also
/// passes the entry filters opens an independent trip — overlapping/concurrent
/// trips on the same ticker are allowed (uncapped, fixed-notional, no compounding),
/// so we do NOT dedup or suppress new entries while another is open.
let tripsForTicker (cfg: Config) (rows: SignalRow[]) : Trip[] =
    [| for t in 0 .. rows.Length - 1 do
         if rows.[t].is_entry && passesEntryFilters cfg rows.[t] then
             yield walkOne cfg.Notional cfg.ExpansionExitThreshold cfg.TimeStopBars cfg.StallBars rows t |]
