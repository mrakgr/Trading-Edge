module TradingEdge.Vwap.Backtest

open System
open TradingEdge.Vwap.BarLoader

type Side = Long | Short

/// One round-trip trade. Always entered and exited at a bar's CLOSE price.
/// `EntryBucket`/`ExitBucket` are within-day 0..959 indices; `EntryDate` and
/// `ExitDate` are normally the same day because we flatten at the 16:00 close,
/// but kept separate for joinability.
type Trade = {
    EntryDate: DateOnly
    EntryBucket: int
    EntryPrice: float
    EntryVwap: float
    EntryReason: string  // "vwap_cross_long" | "vwap_cross_short" | "session_open"

    ExitDate: DateOnly
    ExitBucket: int
    ExitPrice: float
    ExitVwap: float
    ExitReason: string   // "vwap_cross_flip" | "session_close"

    Side: Side
    /// Shares-normalized P&L. Long: exit - entry. Short: entry - exit.
    /// A trade with entry=$500, exit=$501, long → +$1 per share.
    PnL: float
    /// Return per share normalized by entry price (decimal).
    Return: float
    BarsHeld: int
}

/// Run the always-in VWAP flip strategy for one day's bars (already
/// filtered to RTH, sorted by bucket).
///
/// Rules:
///   - At RTH open (first bar, bucket 330), enter long or short based on
///     sign of (close - vwap) at that bar. (Tie → long by convention; first
///     bar's VWAP equals its own price so we never tie unless price == vwap
///     exactly to the cent.)
///   - On each subsequent bar's CLOSE: if the side implied by (close - vwap)
///     differs from the current position, close the current trade at this
///     bar's close and open a new one in the new direction at the SAME
///     bar's close price (the "flip" — entry and exit share a fill price).
///   - At the last RTH bar (bucket 719, 15:59 ET close), force-exit the
///     position at that bar's close.
let runDay (dayBars: Bar[]) : Trade[] =
    if dayBars.Length = 0 then [||]
    else
        let trades = ResizeArray<Trade>()

        // Compute cumulative session VWAP per bar as we walk. Using the bar's
        // own VWAP (price × volume already weighted within the bar) as the
        // typical-price contribution. This matches Σ(dollar_volume) / Σ(volume).
        let mutable cumDollar = 0.0
        let mutable cumVol = 0L
        let mutable cumVwap = 0.0   // session VWAP through the current bar
        let mutable curSide = Long
        let mutable entryBar : Bar = dayBars.[0]
        let mutable entryVwap = 0.0
        let mutable entryReason = "session_open"
        let mutable entryBarIdx = 0

        let openTrade (i: int) (bar: Bar) (side: Side) (vwapNow: float) (reason: string) =
            curSide <- side
            entryBar <- bar
            entryVwap <- vwapNow
            entryReason <- reason
            entryBarIdx <- i

        let closeTrade (i: int) (bar: Bar) (vwapNow: float) (reason: string) =
            let pnl =
                match curSide with
                | Long -> bar.Close - entryBar.Close
                | Short -> entryBar.Close - bar.Close
            let ret = pnl / entryBar.Close
            let barsHeld = i - entryBarIdx + 1
            trades.Add {
                EntryDate = entryBar.Date
                EntryBucket = entryBar.Bucket
                EntryPrice = entryBar.Close
                EntryVwap = entryVwap
                EntryReason = entryReason

                ExitDate = bar.Date
                ExitBucket = bar.Bucket
                ExitPrice = bar.Close
                ExitVwap = vwapNow
                ExitReason = reason

                Side = curSide
                PnL = pnl
                Return = ret
                BarsHeld = barsHeld
            }

        // Bar 0 = RTH open: compute VWAP for this bar alone, take the side
        // implied by (close - vwap), enter at this bar's close.
        let b0 = dayBars.[0]
        cumDollar <- cumDollar + b0.DollarVolume
        cumVol <- cumVol + b0.Volume
        cumVwap <- if cumVol > 0L then cumDollar / float cumVol else b0.Close
        let side0 = if b0.Close >= cumVwap then Long else Short
        openTrade 0 b0 side0 cumVwap "session_open"

        for i in 1 .. dayBars.Length - 1 do
            let bar = dayBars.[i]
            cumDollar <- cumDollar + bar.DollarVolume
            cumVol <- cumVol + bar.Volume
            cumVwap <- if cumVol > 0L then cumDollar / float cumVol else bar.Close

            let isLastBar = (i = dayBars.Length - 1)
            let impliedSide = if bar.Close >= cumVwap then Long else Short

            if isLastBar then
                // Force flat at session close.
                closeTrade i bar cumVwap "session_close"
            elif impliedSide <> curSide then
                // Flip: close at this bar's close, immediately open new side
                // at the SAME price.
                closeTrade i bar cumVwap "vwap_cross_flip"
                let reason =
                    if impliedSide = Long then "vwap_cross_long" else "vwap_cross_short"
                openTrade i bar impliedSide cumVwap reason
            // else: hold

        trades.ToArray()

/// Run across many days. Each day is treated independently (session VWAP
/// resets, position closes at EOD); no overnight holds.
let runMany (bars: Bar[]) : Trade[] =
    if bars.Length = 0 then [||]
    else
        let trades = ResizeArray<Trade>()
        // Partition by date. Bars are already sorted by (date, bucket).
        let mutable i = 0
        while i < bars.Length do
            let d = bars.[i].Date
            let mutable j = i
            while j < bars.Length && bars.[j].Date = d do
                j <- j + 1
            let dayBars = bars.[i .. j - 1]
            trades.AddRange(runDay dayBars)
            i <- j
        trades.ToArray()


// ───────────────────── filtered (gated by breadth) ─────────────────────
//
// At each bar close the engine asks: "what side does the breadth indicator
// permit right now?". The desired position is:
//   - Long  if VWAP-implied side is Long  AND longGate(imbalance)
//   - Short if VWAP-implied side is Short AND shortGate(imbalance)
//   - Flat  otherwise (gate refuses the implied side, or imbalance is
//           missing for this bar)
// When desiredSide differs from the current position, we close the open
// trade (if any) and immediately open a new one (if non-flat), at THIS
// bar's close. Last RTH bar forces flat.

/// A breadth-gate predicate. True = "this imbalance value is permitted for
/// taking that side." The two predicates are independent so longs and shorts
/// can have different bands.
type BreadthGate = float -> bool

/// imbalanceOf: lookup (date, bucket) → imbalance, or None if missing.
let runDayFiltered
    (dayBars: Bar[])
    (imbalanceOf: DateOnly -> int -> float option)
    (longGate: BreadthGate)
    (shortGate: BreadthGate)
    : Trade[] =
    if dayBars.Length = 0 then [||]
    else
        let trades = ResizeArray<Trade>()
        let mutable cumDollar = 0.0
        let mutable cumVol = 0L
        let mutable cumVwap = 0.0

        // Position state: None = flat, Some Long, Some Short.
        let mutable posSide : Side option = None
        let mutable entryBar : Bar = dayBars.[0]
        let mutable entryVwap = 0.0
        let mutable entryReason = ""
        let mutable entryBarIdx = 0

        let openTrade (i: int) (bar: Bar) (side: Side) (vwapNow: float) (reason: string) =
            posSide <- Some side
            entryBar <- bar
            entryVwap <- vwapNow
            entryReason <- reason
            entryBarIdx <- i

        let closeTrade (i: int) (bar: Bar) (vwapNow: float) (reason: string) =
            match posSide with
            | None -> ()
            | Some side ->
                let pnl =
                    match side with
                    | Long -> bar.Close - entryBar.Close
                    | Short -> entryBar.Close - bar.Close
                let ret = pnl / entryBar.Close
                let barsHeld = i - entryBarIdx + 1
                trades.Add {
                    EntryDate = entryBar.Date
                    EntryBucket = entryBar.Bucket
                    EntryPrice = entryBar.Close
                    EntryVwap = entryVwap
                    EntryReason = entryReason

                    ExitDate = bar.Date
                    ExitBucket = bar.Bucket
                    ExitPrice = bar.Close
                    ExitVwap = vwapNow
                    ExitReason = reason

                    Side = side
                    PnL = pnl
                    Return = ret
                    BarsHeld = barsHeld
                }
                posSide <- None

        for i in 0 .. dayBars.Length - 1 do
            let bar = dayBars.[i]
            cumDollar <- cumDollar + bar.DollarVolume
            cumVol <- cumVol + bar.Volume
            cumVwap <- if cumVol > 0L then cumDollar / float cumVol else bar.Close

            let impliedSide = if bar.Close >= cumVwap then Long else Short
            let imbalance = imbalanceOf bar.Date bar.Bucket
            let isLastBar = (i = dayBars.Length - 1)

            // Compute the side the gate currently permits, given the implied side.
            let permittedSide =
                match imbalance with
                | None -> None
                | Some imb ->
                    match impliedSide with
                    | Long  -> if longGate imb  then Some Long  else None
                    | Short -> if shortGate imb then Some Short else None

            // Force-flat at the last bar; otherwise move to permittedSide.
            let desiredSide =
                if isLastBar then None
                else permittedSide

            if desiredSide <> posSide then
                // Close existing position (if any).
                if posSide.IsSome then
                    let reason =
                        if isLastBar then "session_close"
                        elif desiredSide.IsNone then "gate_closed"
                        else "vwap_cross_flip"
                    closeTrade i bar cumVwap reason
                // Open new position (if desired side is non-flat and we aren't EOD).
                match desiredSide with
                | Some s when not isLastBar ->
                    let reason =
                        match s with
                        | Long  -> "vwap_cross_long_gated"
                        | Short -> "vwap_cross_short_gated"
                    openTrade i bar s cumVwap reason
                | _ -> ()

        trades.ToArray()

let runManyFiltered
    (bars: Bar[])
    (imbalanceOf: DateOnly -> int -> float option)
    (longGate: BreadthGate)
    (shortGate: BreadthGate)
    : Trade[] =
    if bars.Length = 0 then [||]
    else
        let trades = ResizeArray<Trade>()
        let mutable i = 0
        while i < bars.Length do
            let d = bars.[i].Date
            let mutable j = i
            while j < bars.Length && bars.[j].Date = d do
                j <- j + 1
            let dayBars = bars.[i .. j - 1]
            trades.AddRange(runDayFiltered dayBars imbalanceOf longGate shortGate)
            i <- j
        trades.ToArray()


// ───────────── filter-on-entry-only: gate only checked at flip ─────────────
//
// Identical to runDay (always-in flip) except: at each VWAP cross, the
// engine checks the breadth gate. If the gate refuses the new side, the
// trade is NOT taken — the engine stays in the current position (or
// flat, if it was already flat from a previous skip). The gate is NOT
// re-checked while a position is open; once entered, we hold to the next
// cross. This faithfully implements "filter entries" semantics and is
// the proper backtest companion to the naive decile-bucketing analysis.

let runDayEntryFiltered
    (dayBars: Bar[])
    (imbalanceOf: DateOnly -> int -> float option)
    (longGate: BreadthGate)
    (shortGate: BreadthGate)
    : Trade[] =
    if dayBars.Length = 0 then [||]
    else
        let trades = ResizeArray<Trade>()
        let mutable cumDollar = 0.0
        let mutable cumVol = 0L
        let mutable cumVwap = 0.0

        // Position state can be flat (no position) or holding a side.
        let mutable posSide : Side option = None
        let mutable entryBar : Bar = dayBars.[0]
        let mutable entryVwap = 0.0
        let mutable entryReason = ""
        let mutable entryBarIdx = 0
        // Last side the VWAP cross *implied*. We only act when this changes
        // (matches the unfiltered runDay's flip semantics).
        let mutable lastImpliedSide : Side option = None

        let gatePermits (side: Side) (imbOpt: float option) : bool =
            match imbOpt with
            | None -> false
            | Some imb ->
                match side with
                | Long  -> longGate imb
                | Short -> shortGate imb

        let openTrade (i: int) (bar: Bar) (side: Side) (vwapNow: float) (reason: string) =
            posSide <- Some side
            entryBar <- bar
            entryVwap <- vwapNow
            entryReason <- reason
            entryBarIdx <- i

        let closeTrade (i: int) (bar: Bar) (vwapNow: float) (reason: string) =
            match posSide with
            | None -> ()
            | Some side ->
                let pnl =
                    match side with
                    | Long -> bar.Close - entryBar.Close
                    | Short -> entryBar.Close - bar.Close
                let ret = pnl / entryBar.Close
                let barsHeld = i - entryBarIdx + 1
                trades.Add {
                    EntryDate = entryBar.Date
                    EntryBucket = entryBar.Bucket
                    EntryPrice = entryBar.Close
                    EntryVwap = entryVwap
                    EntryReason = entryReason
                    ExitDate = bar.Date
                    ExitBucket = bar.Bucket
                    ExitPrice = bar.Close
                    ExitVwap = vwapNow
                    ExitReason = reason
                    Side = side
                    PnL = pnl
                    Return = ret
                    BarsHeld = barsHeld
                }
                posSide <- None

        for i in 0 .. dayBars.Length - 1 do
            let bar = dayBars.[i]
            cumDollar <- cumDollar + bar.DollarVolume
            cumVol <- cumVol + bar.Volume
            cumVwap <- if cumVol > 0L then cumDollar / float cumVol else bar.Close

            let impliedSide = if bar.Close >= cumVwap then Long else Short
            let isLastBar = (i = dayBars.Length - 1)
            let imbalance = imbalanceOf bar.Date bar.Bucket

            // Detect a "cross event" — implied side just changed (or this is bar 0).
            let crossed = (lastImpliedSide <> Some impliedSide)

            if isLastBar then
                // Force flat regardless of gate.
                if posSide.IsSome then closeTrade i bar cumVwap "session_close"
            elif crossed then
                // VWAP cross. Close any open position (it's now the wrong side).
                if posSide.IsSome then closeTrade i bar cumVwap "vwap_cross_flip"
                // Open new position only if breadth gate permits this side.
                if gatePermits impliedSide imbalance then
                    let reason =
                        match impliedSide with
                        | Long  -> "vwap_cross_long_gated"
                        | Short -> "vwap_cross_short_gated"
                    openTrade i bar impliedSide cumVwap reason
            // else: not a cross, no entry — just hold whatever we have.

            lastImpliedSide <- Some impliedSide

        trades.ToArray()

let runManyEntryFiltered
    (bars: Bar[])
    (imbalanceOf: DateOnly -> int -> float option)
    (longGate: BreadthGate)
    (shortGate: BreadthGate)
    : Trade[] =
    if bars.Length = 0 then [||]
    else
        let trades = ResizeArray<Trade>()
        let mutable i = 0
        while i < bars.Length do
            let d = bars.[i].Date
            let mutable j = i
            while j < bars.Length && bars.[j].Date = d do
                j <- j + 1
            let dayBars = bars.[i .. j - 1]
            trades.AddRange(runDayEntryFiltered dayBars imbalanceOf longGate shortGate)
            i <- j
        trades.ToArray()


// ───────────────────────── summary metrics ─────────────────────────

type Metrics = {
    Trades: int
    LongTrades: int
    ShortTrades: int
    Wins: int
    LongWins: int
    ShortWins: int
    WinRate: float
    ProfitFactor: float
    NetPnL: float
    LongNetPnL: float
    ShortNetPnL: float
    GrossWins: float
    GrossLosses: float
    MaxDrawdown: float
    AvgBarsHeld: float
}

let private profitFactor (pnls: float[]) : float =
    let gw = pnls |> Array.sumBy (fun p -> if p > 0.0 then p else 0.0)
    let gl = pnls |> Array.sumBy (fun p -> if p < 0.0 then -p else 0.0)
    if gl = 0.0 then
        if gw = 0.0 then 0.0 else infinity
    else gw / gl

let private maxDrawdown (pnls: float[]) : float =
    if pnls.Length = 0 then 0.0
    else
        let mutable cum = 0.0
        let mutable peak = 0.0
        let mutable mdd = 0.0
        for p in pnls do
            cum <- cum + p
            if cum > peak then peak <- cum
            let dd = cum - peak
            if dd < mdd then mdd <- dd
        mdd

let computeMetrics (trades: Trade[]) : Metrics =
    let pnls = trades |> Array.map (fun t -> t.PnL)
    let wins = pnls |> Array.filter (fun p -> p > 0.0)
    let longTrades = trades |> Array.filter (fun t -> t.Side = Long)
    let shortTrades = trades |> Array.filter (fun t -> t.Side = Short)
    let longPnls = longTrades |> Array.map (fun t -> t.PnL)
    let shortPnls = shortTrades |> Array.map (fun t -> t.PnL)
    {
        Trades = trades.Length
        LongTrades = longTrades.Length
        ShortTrades = shortTrades.Length
        Wins = wins.Length
        LongWins = longPnls |> Array.filter (fun p -> p > 0.0) |> Array.length
        ShortWins = shortPnls |> Array.filter (fun p -> p > 0.0) |> Array.length
        WinRate = if pnls.Length > 0 then float wins.Length / float pnls.Length else 0.0
        ProfitFactor = profitFactor pnls
        NetPnL = Array.sum pnls
        LongNetPnL = Array.sum longPnls
        ShortNetPnL = Array.sum shortPnls
        GrossWins = wins |> Array.sum
        GrossLosses = pnls |> Array.filter (fun p -> p < 0.0) |> Array.sumBy (fun p -> -p)
        MaxDrawdown = maxDrawdown pnls
        AvgBarsHeld =
            if trades.Length = 0 then 0.0
            else trades |> Array.averageBy (fun t -> float t.BarsHeld)
    }
