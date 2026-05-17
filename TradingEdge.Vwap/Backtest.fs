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
