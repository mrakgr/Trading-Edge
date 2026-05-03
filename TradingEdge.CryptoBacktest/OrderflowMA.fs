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
// Fill model: decide at bar i close, fill at bar i's close price. The bar's
// trades have already happened in the past (we observed them to compute the
// ratio), so trading at the close price models a market order placed at the
// instant the bar closes. On hourly+ timeframes the difference between this
// bar's close and the next bar's open is noise; we use bar.Close so we can
// run on pre-aggregated bar parquets without needing the trade tape.

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
    /// on the bear signal.
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
    let qty = notional / entry
    match side with
    | Long  -> (exit - entry) * qty
    | Short -> (entry - exit) * qty
    | Flat  -> 0.0

// =============================================================================
// Streaming engine — bar-only path
// =============================================================================
//
// State machine, on each ProcessBar(bar):
//   1. Slide the rolling window forward by one bar (add this bar, drop the
//      oldest if window is full).
//   2. If the window is full, compute the ratio and target side. If the
//      target differs from the current side, execute the change at bar.Close.
//
// At end of stream, Flush() force-closes any open position at the last
// observed close price.

type Engine(cfg: StrategyConfig) =
    let n = cfg.MaLength
    let buyBuf = Array.zeroCreate<float> n
    let sellBuf = Array.zeroCreate<float> n
    let mutable barsSeen = 0
    let mutable sumBuy = 0.0
    let mutable sumSell = 0.0

    let mutable side = Flat
    let mutable entryPrice = 0.0
    let mutable entryUs = 0L

    let mutable lastClose = 0.0
    let mutable lastUs = 0L

    let trips = ResizeArray<RoundTrip>()

    let openPos (newSide: Side) (fillPrice: float) (fillUs: int64) =
        side <- newSide
        entryPrice <- fillPrice
        entryUs <- fillUs

    let closePos (fillPrice: float) (fillUs: int64) =
        let gross = grossPnL side entryPrice fillPrice cfg.Notional
        let fees = 2.0 * feeOnFill cfg
        trips.Add {
            EntryUs = entryUs
            ExitUs = fillUs
            Side = side
            EntryPrice = entryPrice
            ExitPrice = fillPrice
            NetPnL = gross - fees
            Fees = fees
        }
        side <- Flat
        entryPrice <- 0.0
        entryUs <- 0L

    member _.BarsSeen = barsSeen
    member _.Trips = trips :> seq<RoundTrip>

    member _.ProcessBar(bar: SignedBar) =
        let slot = barsSeen % n
        if barsSeen >= n then
            sumBuy <- sumBuy - buyBuf.[slot]
            sumSell <- sumSell - sellBuf.[slot]
        buyBuf.[slot] <- bar.BuyDollarVolume
        sellBuf.[slot] <- bar.SellDollarVolume
        sumBuy <- sumBuy + bar.BuyDollarVolume
        sumSell <- sumSell + bar.SellDollarVolume
        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        if barsSeen >= n then
            let ratio = if sumSell > 0.0 then sumBuy / sumSell else infinity
            let bullish = ratio > 1.0
            let bearish = ratio < 1.0
            let target =
                if bullish then Long
                elif bearish && cfg.AllowShort then Short
                else Flat
            if target <> side then
                if side <> Flat then closePos bar.Close bar.EndUs
                if target <> Flat then openPos target bar.Close bar.EndUs

    /// Force-close any open position at the last seen bar's close.
    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

/// Convenience runner over a precomputed bar array.
let run (cfg: StrategyConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
