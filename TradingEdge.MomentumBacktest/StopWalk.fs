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
    (atrExitThr: float option)
    (timeStopBars: int option)
    (stallBars: int option)
    (breakevenAfter: int option)
    (noPriceStop: bool)
    (initialStopDayLow: bool)
    (trailLimitHighWindow: int option)
    (trailLimitTimeCap: int)
    (a: SignalRow[])
    (t: int)
    : Trip =
    let n = a.Length
    let entry = a.[t]
    let entryPrice = entry.adj_close
    let entryDayLow = entry.adj_low   // Qullamaggie initial-stop floor
    let qty = notional / entryPrice

    // Stall state: highest close seen so far (incl. entry) and how many held bars
    // have passed since the last new high. Both updated per bar BEFORE the stall
    // test, using only data up to and including bar D (no lookahead).
    let mutable highClose = entry.adj_close
    let mutable barsSinceHigh = 0
    // Breakeven-after-N state: once armed, the stop floor includes the entry price
    // so the trade can never give back to a loss. Arming happens at exactly bar
    // (t+N) and only if the trade is in profit there; if it is NOT in profit at
    // (t+N) the trade is exited (laggard time-stop), reason "time_be".
    let mutable beArmed = false

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

        // Breakeven-after-N: at exactly bar t+N decide arm-or-exit (using only
        // this bar's close — no lookahead). Once armed it stays armed.
        let mutable beLaggardExit = false
        match breakevenAfter with
        | Some nbars when not beArmed && (d - t) >= nbars ->
            if bar.adj_close > entryPrice then beArmed <- true
            else beLaggardExit <- true   // not profitable by day N → exit
        | _ -> ()

        // Effective stop level = the highest of the active floors:
        //   - the trailing 15-day-low (the base stop),
        //   - the entry-day low (Qullamaggie initial stop) when initialStopDayLow,
        //   - the entry price once the breakeven floor is armed.
        // Disabled entirely when noPriceStop (Variant 1: time/expansion exits only).
        let trail = if bar.low_15_prior.HasValue then Some bar.low_15_prior.Value else None
        let floors =
            [ trail
              (if initialStopDayLow then Some entryDayLow else None)
              (if beArmed then Some entryPrice else None) ]
            |> List.choose id
        let stopLevel = if noPriceStop || List.isEmpty floors then None else Some (List.max floors)
        let stopHit =
            match stopLevel with
            | Some lvl -> bar.adj_low <= lvl
            | None -> false
        // Label "breakeven" when the entry-price floor is the binding one (above
        // both the trailing low and the entry-day low); else ordinary "stop".
        let trailOrDayLow =
            [ trail; (if initialStopDayLow then Some entryDayLow else None) ] |> List.choose id
        let stopReason =
            if beArmed && (List.isEmpty trailOrDayLow || List.max trailOrDayLow < entryPrice)
            then "breakeven" else "stop"

        let expansionHit =
            match expansionThr with
            | Some thr -> bar.tightness_14.HasValue && bar.tightness_14.Value > thr
            | None -> false
        let atrHit =
            match atrExitThr with
            | Some thr -> bar.atr_pct_14.HasValue && bar.atr_pct_14.Value > thr
            | None -> false
        let timeHit =
            match timeStopBars with
            | Some nbars -> (d - t) >= nbars
            | None -> false
        let stallHit =
            match stallBars with
            | Some k -> barsSinceHigh >= k
            | None -> false

        if stopHit then reason <- stopReason
        elif beLaggardExit then reason <- "time_be"
        elif expansionHit then reason <- "expansion"
        elif atrHit then reason <- "atr_exit"
        elif timeHit then reason <- "time"
        elif stallHit then reason <- "stall"
        else d <- d + 1

    // Mean-reversion trailing-limit resolution. When the trigger was a "get me out"
    // exit (price stop / breakeven, OR the time stop — the latter is V1's primary
    // exit since V1 has no price stop) and trailing-limit mode is on: rather than
    // selling at d+1 open, rest a SELL LIMIT at the N-day high (as of the stop bar d),
    // ratchet it DOWN-only each subsequent bar, and fill at the limit on the first
    // bar whose high reaches it. If unfilled within trailLimitTimeCap held bars,
    // exit at market (the bar-after-cap open). No lookahead: a resting limit at P
    // fills when a later bar trades through P (high >= P); the ratchet uses only
    // highs up to and including each evaluated bar.
    let isStopTrigger =
        reason = "stop" || reason = "breakeven" || reason = "time" || reason = "time_be"
    let trailResolution =
        match trailLimitHighWindow with
        | Some win when isStopTrigger && d < n ->
            // Initial limit = max high over [d-win+1 .. d] (N-day high at the stop).
            let nDayHigh hi =
                let lo = max (t + 1) (hi - win + 1)   // never look before the entry+1 window start
                let mutable m = a.[hi].adj_high
                for k in lo .. hi do if a.[k].adj_high > m then m <- a.[k].adj_high
                m
            let mutable limit = nDayHigh d
            let mutable e = d + 1
            let cap = d + trailLimitTimeCap          // last bar the limit may rest on
            let mutable result = None
            while e < n && e <= cap && result.IsNone do
                // Ratchet the limit DOWN to the current rolling N-day high.
                let rollHigh = nDayHigh e
                if rollHigh < limit then limit <- rollHigh
                if a.[e].adj_high >= limit then
                    // Filled into the bounce at the resting limit price.
                    result <- Some (e, limit, false, "stop_limit")
                else
                    e <- e + 1
            match result with
            | Some r -> Some r
            | None ->
                // Limit never filled within the cap: exit at market next open.
                if e < n then Some (e, a.[e].adj_open, false, "stop_limit_timeout")
                elif d < n then Some (n - 1, a.[n - 1].adj_close, true, "mtm")
                else None
        | _ -> None

    let exitIdx, exitPrice, isOpen, exitReason =
        match trailResolution with
        | Some r -> r
        | None ->
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
        EntryLevels = entry.levels
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
             yield walkOne cfg.Notional cfg.ExpansionExitThreshold cfg.AtrExitThreshold cfg.TimeStopBars cfg.StallBars cfg.BreakevenAfter cfg.NoPriceStop cfg.InitialStopDayLow cfg.TrailLimitHighWindow cfg.TrailLimitTimeCap rows t |]
