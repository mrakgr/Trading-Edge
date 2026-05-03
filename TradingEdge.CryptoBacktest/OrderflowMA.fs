module TradingEdge.CryptoBacktest.OrderflowMA

open TradingEdge.Simulation.BinanceLoader
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
// Fill model: decision at the close of bar i. We then fill at the very next
// trade, at that trade's actual price. This is the most realistic model of
// a market taker order placed the instant the signal fires — better than
// using the next bar's VWAP, which would average over price action that
// happens *after* we'd already have been filled.

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
    let qty = notional / entry
    match side with
    | Long  -> (exit - entry) * qty
    | Short -> (entry - exit) * qty
    | Flat  -> 0.0

// =============================================================================
// Streaming engine
// =============================================================================
//
// Two interleaved feeds:
//   ProcessBar — at every bar close, update rolling sums; if a side change
//                is warranted, set pendingTarget so the *next* trade fills it.
//   ProcessTrade — every raw trade. If a pendingTarget is set, execute the
//                  side change at this trade's price, then clear pending.
//
// The Cell driver wires this up so that for each trade T:
//     builder.Process(t)        — may emit a bar; if it does, our
//                                 ProcessBar callback runs and may set
//                                 pendingTarget.
//     engine.ProcessTrade(t)    — if pendingTarget is set, fill happens at
//                                 t.Price. Note T is the first trade of the
//                                 new bucket — exactly the price you'd pay
//                                 sending a market order at signal time.
//
// At end of stream, Flush() force-closes any open position at the last
// trade's price (or last bar VWAP as a fallback if no trades have been seen
// since the last close, which can't happen in our pipeline but is harmless).

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

    // Pending side change: target side after the most recent bar close. If
    // hasPending, the next trade fills at trade.Price.
    let mutable hasPending = false
    let mutable pendingTarget = Flat

    let mutable lastTradePrice = 0.0
    let mutable lastTradeUs = 0L

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

    /// Update rolling sums with the just-closed bar; compute ratio; if a
    /// side change is warranted, mark hasPending so the next trade fills.
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

        if barsSeen >= n then
            let ratio = if sumSell > 0.0 then sumBuy / sumSell else infinity
            let bullish = ratio > 1.0
            let bearish = ratio < 1.0
            let target =
                if bullish then Long
                elif bearish && cfg.AllowShort then Short
                else Flat
            if target <> side then
                pendingTarget <- target
                hasPending <- true

    /// Every raw trade. If a pending side change is set, execute at this
    /// trade's price.
    member _.ProcessTrade(trade: Trade) =
        lastTradePrice <- trade.Price
        lastTradeUs <- trade.TimestampUs
        if hasPending then
            if pendingTarget <> side then
                if side <> Flat then closePos trade.Price trade.TimestampUs
                if pendingTarget <> Flat then openPos pendingTarget trade.Price trade.TimestampUs
            hasPending <- false

    /// Force-close any open position at the last trade's price. Pending
    /// signal is dropped — it would have been executed against a trade that
    /// never arrived. Idempotent.
    member _.Flush() =
        if side <> Flat && lastTradePrice > 0.0 then
            closePos lastTradePrice lastTradeUs
        hasPending <- false
