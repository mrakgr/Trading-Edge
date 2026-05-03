module TradingEdge.CryptoBacktest.OrderflowMA

open TradingEdge.CryptoBacktest.SignedBar

// =============================================================================
// Orderflow-MA strategy: rolling-sum ratio of buy / sell taker dollar volume
// =============================================================================
//
// Two independent rolling sums of length N over the bar stream:
//     sumBuy_N  = Σ_{i in last N bars} BuyDollarVolume_i
//     sumSell_N = Σ_{i in last N bars} SellDollarVolume_i
//     ratio_t   = sumBuy_N / sumSell_N
//
// We use two separate sums (rather than averaging the per-bar ratio across
// N bars) because per-bar ratios on thin bars are unstable — a bar with
// $10 of buy volume and $1 of sell volume gives ratio = 10, drowning out
// 100× larger bars. Summing first preserves volume-weighting by construction.
//
// Signal:
//   ratio > 1  →  long
//   ratio < 1  →  flat (default) or short (configurable)
//
// Entry/exit happens at the close of the bar that crossed the threshold; the
// fill price is the next bar's VWAP. We size to a fixed dollar notional
// regardless of price so cross-symbol P&L numbers are comparable.

[<Struct>]
type Side = Flat | Long | Short

type StrategyConfig = {
    /// Rolling-sum window length, in bars.
    MaLength: int
    /// Notional per trade, in quote currency (USDT). Default $1000.
    Notional: float
    /// Per-fill taker fee fraction. 4 bps default = 0.0004.
    TakerFee: float
    /// If true, take a short position when ratio < 1; otherwise stay flat
    /// on the bear signal. The pure long/flat variant is the headline test;
    /// long/short is a stress test of the asymmetry.
    AllowShort: bool
}

let defaultConfig (maLength: int) : StrategyConfig =
    { MaLength = maLength
      Notional = 1000.0
      TakerFee = 0.0004
      AllowShort = false }

type RoundTrip = {
    EntryUs: int64
    ExitUs: int64
    Side: Side
    EntryPrice: float
    ExitPrice: float
    /// Net P&L after entry + exit fees, in quote currency.
    NetPnL: float
    /// Total fees paid on this round-trip (entry + exit), in quote currency.
    Fees: float
}

let private feeOnFill (cfg: StrategyConfig) : float =
    cfg.Notional * cfg.TakerFee

let private grossPnL (side: Side) (entry: float) (exit: float) (notional: float) : float =
    // Quantity inferred from notional / entry price; long earns (exit/entry - 1) * notional.
    let qty = notional / entry
    match side with
    | Long  -> (exit - entry) * qty
    | Short -> (entry - exit) * qty
    | Flat  -> 0.0

/// Run the strategy over a precomputed bar stream and return the closed
/// round-trips. Open positions at end-of-data are forced-closed at the last
/// bar's VWAP so the P&L curve isn't biased by un-realized exposure.
///
/// Signal lag: the rolling sum window includes the just-closed bar i, so the
/// signal direction at time-of-decision uses bars [i - N + 1 .. i]. The fill
/// happens at bar i+1's VWAP — i.e. we never see the bar we trade on, which
/// is the standard "no peeking" requirement for a backtest.
let run (cfg: StrategyConfig) (bars: SignedBar[]) : RoundTrip[] =
    let n = bars.Length
    let results = ResizeArray<RoundTrip>()
    if n < cfg.MaLength + 2 then results.ToArray()
    else

    let mutable sumBuy = 0.0
    let mutable sumSell = 0.0
    // Prime the rolling window with the first MaLength bars.
    for i in 0 .. cfg.MaLength - 1 do
        sumBuy <- sumBuy + bars.[i].BuyDollarVolume
        sumSell <- sumSell + bars.[i].SellDollarVolume

    let mutable side = Flat
    let mutable entryPrice = 0.0
    let mutable entryUs = 0L

    let openPosition (newSide: Side) (fillBarIdx: int) =
        let bar = bars.[fillBarIdx]
        side <- newSide
        entryPrice <- bar.VWAP
        entryUs <- bar.StartUs

    let closePosition (fillBarIdx: int) =
        let bar = bars.[fillBarIdx]
        let exitPrice = bar.VWAP
        let gross = grossPnL side entryPrice exitPrice cfg.Notional
        let fees = 2.0 * feeOnFill cfg
        results.Add {
            EntryUs = entryUs
            ExitUs = bar.StartUs
            Side = side
            EntryPrice = entryPrice
            ExitPrice = exitPrice
            NetPnL = gross - fees
            Fees = fees
        }
        side <- Flat
        entryPrice <- 0.0
        entryUs <- 0L

    // Decision happens at the close of bar i (i = MaLength - 1 is the first
    // bar with a full window); fill happens at bar i+1's VWAP. So we walk
    // i from MaLength - 1 to n - 2.
    let mutable i = cfg.MaLength - 1
    while i < n - 1 do
        let ratio = if sumSell > 0.0 then sumBuy / sumSell else infinity
        let bullish = ratio > 1.0
        let bearish = ratio < 1.0
        let target =
            if bullish then Long
            elif bearish && cfg.AllowShort then Short
            else Flat

        if target <> side then
            if side <> Flat then closePosition (i + 1)
            if target <> Flat then openPosition target (i + 1)

        // Slide the rolling window forward by one bar: drop bar i - MaLength + 1, add bar i + 1.
        let dropIdx = i - cfg.MaLength + 1
        sumBuy <- sumBuy - bars.[dropIdx].BuyDollarVolume + bars.[i + 1].BuyDollarVolume
        sumSell <- sumSell - bars.[dropIdx].SellDollarVolume + bars.[i + 1].SellDollarVolume
        i <- i + 1

    // Force-close any open position at the last bar's VWAP. This avoids
    // letting an open trade silently inflate or deflate reported P&L.
    if side <> Flat then
        let bar = bars.[n - 1]
        let exitPrice = bar.VWAP
        let gross = grossPnL side entryPrice exitPrice cfg.Notional
        let fees = 2.0 * feeOnFill cfg
        results.Add {
            EntryUs = entryUs
            ExitUs = bar.StartUs
            Side = side
            EntryPrice = entryPrice
            ExitPrice = exitPrice
            NetPnL = gross - fees
            Fees = fees
        }

    results.ToArray()
