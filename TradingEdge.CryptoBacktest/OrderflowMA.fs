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
// Fill model: decision at the close of bar i (using bars [i-N+1 .. i]),
// position taken at bar (i+1)'s VWAP. We never see the bar we trade on.

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
// Drive bars in via Process. The previous bar is held back so that when the
// next bar arrives we can act on the previous bar's signal at the new bar's
// VWAP — that's the "decide at close, fill at next open" rule. The new bar
// is what the position is opened/closed at, and only after that do we advance
// the rolling window.
//
// State machine, on each Process(bar):
//   1. If we have a pendingSignal from the previous bar, execute it at
//      bar.VWAP (open / close / flip the position).
//   2. Add bar's contribution to the rolling sums; drop the oldest if window
//      is full.
//   3. If window is full, compute ratio and store the resulting target side
//      as the new pendingSignal. (Won't be acted on until next Process.)
//
// Final position is closed via Flush at the symbol's last bar.

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

    // Pending signal: target side computed from the last closed bar, to be
    // executed at the next bar's VWAP.
    let mutable hasPending = false
    let mutable pendingTarget = Flat

    let mutable lastBarVwap = 0.0
    let mutable lastBarStart = 0L

    let trips = ResizeArray<RoundTrip>()

    let openPos (newSide: Side) (fillVwap: float) (fillUs: int64) =
        side <- newSide
        entryPrice <- fillVwap
        entryUs <- fillUs

    let closePos (fillVwap: float) (fillUs: int64) =
        let gross = grossPnL side entryPrice fillVwap cfg.Notional
        let fees = 2.0 * feeOnFill cfg
        trips.Add {
            EntryUs = entryUs
            ExitUs = fillUs
            Side = side
            EntryPrice = entryPrice
            ExitPrice = fillVwap
            NetPnL = gross - fees
            Fees = fees
        }
        side <- Flat
        entryPrice <- 0.0
        entryUs <- 0L

    member _.BarsSeen = barsSeen
    member _.Trips = trips :> seq<RoundTrip>

    member _.Process(bar: SignedBar) =
        // 1. Execute pending signal (if any) at this bar's VWAP.
        if hasPending then
            if pendingTarget <> side then
                if side <> Flat then closePos bar.VWAP bar.StartUs
                if pendingTarget <> Flat then openPos pendingTarget bar.VWAP bar.StartUs
            hasPending <- false

        // 2. Slide the rolling window forward by one bar. Once the buffer is
        //    full (barsSeen >= n), the oldest entry gets evicted from the sum.
        let slot = barsSeen % n
        if barsSeen >= n then
            sumBuy <- sumBuy - buyBuf.[slot]
            sumSell <- sumSell - sellBuf.[slot]
        buyBuf.[slot] <- bar.BuyDollarVolume
        sellBuf.[slot] <- bar.SellDollarVolume
        sumBuy <- sumBuy + bar.BuyDollarVolume
        sumSell <- sumSell + bar.SellDollarVolume
        barsSeen <- barsSeen + 1

        // 3. Once window is full, compute ratio and set next bar's target.
        if barsSeen >= n then
            let ratio = if sumSell > 0.0 then sumBuy / sumSell else infinity
            let bullish = ratio > 1.0
            let bearish = ratio < 1.0
            pendingTarget <-
                if bullish then Long
                elif bearish && cfg.AllowShort then Short
                else Flat
            hasPending <- true

        lastBarVwap <- bar.VWAP
        lastBarStart <- bar.StartUs

    /// Force-close any open position at the last bar's VWAP. The pending
    /// signal is dropped — it would have been executed against a bar that
    /// never arrived. Idempotent.
    member _.Flush() =
        if side <> Flat && lastBarVwap > 0.0 then
            closePos lastBarVwap lastBarStart
        hasPending <- false

/// Convenience wrapper: drive a precomputed bar array through the streaming
/// engine and return the closed round-trips. Behaves identically to the
/// streaming path used in production.
let run (cfg: StrategyConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.Process bar
    eng.Flush()
    eng.Trips |> Seq.toArray
