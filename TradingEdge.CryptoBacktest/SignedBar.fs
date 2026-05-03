module TradingEdge.CryptoBacktest.SignedBar

open TradingEdge.Simulation.BinanceLoader

// =============================================================================
// Time-bar with signed dollar-volume
// =============================================================================
//
// One bar = one fixed-width slice of the trade tape (1m, 5m, 15m, 1h, 4h ...).
// We carry buy / sell dollar-volume separately so OrderflowMA can compute
// rolling Σ(buyDV) / Σ(sellDV) ratios without re-touching the trades.
//
// Trade splitting: time bars do NOT split trades — a trade is assigned in
// full to the bar that contains its timestamp. (Volume bars in
// VolumeBar.fs do split because they enforce a fixed volume cap; time bars
// don't.)

type SignedBar = {
    StartUs: int64
    EndUs: int64
    VWAP: float
    /// sqrt(max 0 (Σ(p²·v)/Σv − vwap²)). Volume-weighted std of trade prices
    /// within the bar; useful as a per-bar volatility proxy.
    VolWeightedStdDev: float
    BuyDollarVolume: float    // Σ price·qty where Sign = +1 (taker buy)
    SellDollarVolume: float   // Σ price·qty where Sign = -1 (taker sell)
    TradeCount: int
}

/// Streaming time-bar builder. Feed Trade values via Process; the onNext
/// callback fires once per closed bar. The trailing partial bar is NOT
/// emitted automatically — call Flush at end-of-stream if you need it.
///
/// The bucket boundary is computed from the absolute microsecond timestamp:
/// bucketIdx = ts / bucketUs. This means bars are anchored to unix-epoch
/// boundaries (e.g. a 1h bar always starts on the hour UTC), which keeps
/// bars aligned across symbols and makes them easy to reason about.
type TimeBarBuilder(bucketUs: int64) =
    let mutable hasOpen = false
    let mutable curIdx = 0L
    let mutable startUs = 0L
    let mutable endUs = 0L
    let mutable sumVol = 0.0
    let mutable sumPV = 0.0
    let mutable sumPPV = 0.0
    let mutable buyDV = 0.0
    let mutable sellDV = 0.0
    let mutable tradeCount = 0


    let resetTo (idx: int64) (ts: int64) =
        curIdx <- idx
        startUs <- idx * bucketUs
        endUs <- ts
        sumVol <- 0.0
        sumPV <- 0.0
        sumPPV <- 0.0
        buyDV <- 0.0
        sellDV <- 0.0
        tradeCount <- 0

    member self.Emit (onNext: SignedBar -> unit) =
        let vwap = if sumVol > 0.0 then sumPV / sumVol else 0.0
        let variance =
            if sumVol > 0.0 then max 0.0 (sumPPV / sumVol - vwap * vwap)
            else 0.0
        onNext {
            StartUs = startUs
            EndUs = endUs
            VWAP = vwap
            VolWeightedStdDev = sqrt variance
            BuyDollarVolume = buyDV
            SellDollarVolume = sellDV
            TradeCount = tradeCount
        }

    member _.BucketUs = bucketUs

    member self.Process(onNext: SignedBar -> unit, trade: Trade) =
        let idx = trade.TimestampUs / bucketUs
        if not hasOpen then
            hasOpen <- true
            resetTo idx trade.TimestampUs
        elif idx <> curIdx then
            // Time bars are anchored to wall-clock buckets, so a gap with no
            // trades simply means we skip emitting empty bars — the strategy
            // sees the next populated bar with its true timestamp. We do not
            // synthesize zero-volume placeholders.
            self.Emit onNext
            resetTo idx trade.TimestampUs
        let p = trade.Price
        let v = trade.Quantity
        let pv = p * v
        sumVol <- sumVol + v
        sumPV <- sumPV + pv
        sumPPV <- sumPPV + p * p * v
        if trade.Sign > 0.0 then buyDV <- buyDV + pv
        else sellDV <- sellDV + pv
        tradeCount <- tradeCount + 1
        endUs <- trade.TimestampUs

    /// Emit the currently-open bar (if any). Call this once after the last
    /// trade if you want the trailing partial bar to appear in the output.
    member self.Flush(onNext: SignedBar -> unit) =
        if hasOpen then
            self.Emit onNext
            hasOpen <- false

/// Convenience: build the full bar array from a Trade[] in timestamp order.
/// The trailing partial bar IS included via Flush.
let buildBars (bucketUs: int64) (trades: Trade[]) : SignedBar[] =
    let result = ResizeArray<SignedBar>(capacity = trades.Length / 100 + 16)
    let builder = TimeBarBuilder(bucketUs)
    for trade in trades do
        builder.Process((fun bar -> result.Add bar), trade)
    builder.Flush(fun bar -> result.Add bar)
    result.ToArray()

/// Convert a human-readable timeframe string ("1m", "5m", "15m", "1h", "4h")
/// to the bucket length in microseconds. Unknown values throw.
let bucketUsOfTimeframe (tf: string) : int64 =
    let s = tf.Trim().ToLowerInvariant()
    let n, unit =
        let i = s.Length - 1
        s.Substring(0, i), s.[i]
    let v = System.Int64.Parse n
    match unit with
    | 's' -> v * 1_000_000L
    | 'm' -> v * 60L * 1_000_000L
    | 'h' -> v * 3600L * 1_000_000L
    | 'd' -> v * 86400L * 1_000_000L
    | c   -> failwithf "bucketUsOfTimeframe: unknown unit %c in %s" c tf
