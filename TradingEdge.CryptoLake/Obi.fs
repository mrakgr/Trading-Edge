module TradingEdge.CryptoLake.Obi

open System

// =============================================================================
// Order-book imbalance (OBI), volume-weighted with exponential distance decay
// =============================================================================
//
// Sign convention (matches the Python prototype):
//
//   OBI_top_N = (Σᵢ wᵢ·bid_size_i − Σᵢ wᵢ·ask_size_i)
//             / (Σᵢ wᵢ·bid_size_i + Σᵢ wᵢ·ask_size_i)
//   wᵢ = exp(-λ · i)
//
// Output ∈ [-1, +1]. +1 = all weighted size on the bid (resting buy interest)
// → SUPPORT below. -1 = all weighted size on the ask → RESISTANCE above.
//
// The exponential tilt penalises levels far from the inside, on the
// assumption that deep liquidity rarely fills. We use the level *index* (0..N-1)
// as the distance proxy rather than the actual |price - mid| / tick_size:
// on BTC perps the top-N levels sit densely 1 tick apart, so index distance
// is exact under that assumption and avoids a per-row distance recompute.

let exponentialWeights (depth: int) (lambdaDecay: float) : float[] =
    Array.init depth (fun i -> exp (-lambdaDecay * float i))

/// Compute OBI for a single snapshot row (bidSizes/askSizes already extracted).
/// `weights` length must equal `depth`. NaN sizes are treated as zero.
let inline computeOne (bidSizes: float[]) (askSizes: float[]) (weights: float[]) : float =
    let mutable qb = 0.0
    let mutable qa = 0.0
    let n = weights.Length
    for i = 0 to n - 1 do
        let bs = bidSizes.[i]
        let asz = askSizes.[i]
        let bs = if Double.IsNaN bs then 0.0 else bs
        let asz = if Double.IsNaN asz then 0.0 else asz
        qb <- qb + weights.[i] * bs
        qa <- qa + weights.[i] * asz
    let denom = qb + qa
    if denom > 0.0 then (qb - qa) / denom else 0.0
