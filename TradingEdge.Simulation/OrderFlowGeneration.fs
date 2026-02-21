module TradingEdge.Simulation.OrderFlowGeneration

open System
open MathNet.Numerics.Distributions
open TradingEdge.Simulation.EpisodeMCMC

/// A single trade
type Trade = {
    Time: float      // Seconds from start of episode
    Price: float     // Execution price
    Size: int        // Number of shares/contracts
    Trend: Trend
}

/// Parameters for order flow generation within a trend (LogNormal model)
type OrderFlowParams = {
    MedianTradesPerSecond: float  // Typical trade rate (50th percentile)
    MeanTradesPerSecond: float    // Average trade rate (>= Median due to right skew)
}

/// Parameters for price generation
type PriceParams = {
    DriftToVolRatio: float       // Drift as a fraction of volatility (e.g. 0.1 = 10% of vol)
    VolatilityBps: float         // Volatility in bps/sec (normalized to mean trade rate and size)
}

/// Support/resistance level parameters for a trend (in basis points)
type SRParams = {
    NoiseBps: float              // Gaussian noise std dev for S/R jitter in bps
}

/// Parameters for trade size generation (LogNormal activity model)
/// Parameterized by median and mean for intuitive interpretation
type ActivityParams = {
    MedianSize: float            // Typical trade size (50th percentile)
    MeanSize: float              // Average trade size (>= MedianSize due to right skew)
}

/// Get order flow parameters for a trend type
let getOrderFlowParams (trend: Trend) : OrderFlowParams =
    match trend with
    | StrongUptrend ->   { MedianTradesPerSecond = 50.0; MeanTradesPerSecond = 60.0 }
    | MidUptrend ->      { MedianTradesPerSecond = 40.0; MeanTradesPerSecond = 48.0 }
    | WeakUptrend ->     { MedianTradesPerSecond = 25.0; MeanTradesPerSecond = 28.0 }
    | Consolidation ->   { MedianTradesPerSecond = 10.0; MeanTradesPerSecond = 11.0 }
    | WeakDowntrend ->   { MedianTradesPerSecond = 25.0; MeanTradesPerSecond = 28.0 }
    | MidDowntrend ->    { MedianTradesPerSecond = 40.0; MeanTradesPerSecond = 48.0 }
    | StrongDowntrend -> { MedianTradesPerSecond = 50.0; MeanTradesPerSecond = 60.0 }

/// Get price parameters for a trend type (volatility per second)
let getPriceParams (trend: Trend) : PriceParams =
    match trend with
    | StrongUptrend ->   { DriftToVolRatio = 0.10;  VolatilityBps = 3.0 }
    | MidUptrend ->      { DriftToVolRatio = 0.0625; VolatilityBps = 2.4 }
    | WeakUptrend ->     { DriftToVolRatio = 0.0389; VolatilityBps = 1.8 }
    | Consolidation ->   { DriftToVolRatio = 0.0;    VolatilityBps = 1.2 }
    | WeakDowntrend ->   { DriftToVolRatio = -0.0389; VolatilityBps = 1.8 }
    | MidDowntrend ->    { DriftToVolRatio = -0.0625; VolatilityBps = 2.4 }
    | StrongDowntrend -> { DriftToVolRatio = -0.10;  VolatilityBps = 3.0 }

/// Get support/resistance noise parameters for a trend type
let getSRParams (trend: Trend) : SRParams =
    match trend with
    | StrongUptrend ->   { NoiseBps = 5.0 }
    | MidUptrend ->      { NoiseBps = 4.0 }
    | WeakUptrend ->     { NoiseBps = 3.0 }
    | Consolidation ->   { NoiseBps = 3.0 }
    | WeakDowntrend ->   { NoiseBps = 3.0 }
    | MidDowntrend ->    { NoiseBps = 4.0 }
    | StrongDowntrend -> { NoiseBps = 5.0 }

/// Get activity parameters for a trend type (LogNormal activity model)
/// Stronger trends have higher mean/median ratio (more large trades)
let getActivityParams (trend: Trend) : ActivityParams =
    match trend with
    | StrongUptrend ->   { MedianSize = 100.0; MeanSize = 200.0 }
    | MidUptrend ->      { MedianSize = 100.0; MeanSize = 150.0 }
    | WeakUptrend ->     { MedianSize = 100.0; MeanSize = 120.0 }
    | Consolidation ->   { MedianSize = 100.0; MeanSize = 110.0 }
    | WeakDowntrend ->   { MedianSize = 100.0; MeanSize = 120.0 }
    | MidDowntrend ->    { MedianSize = 100.0; MeanSize = 150.0 }
    | StrongDowntrend -> { MedianSize = 100.0; MeanSize = 200.0 }

/// Stochastic rounding: rounds up or down probabilistically based on fractional part
let stochasticRound (rng: Random) (x: float) : int =
    let floor = Math.Floor(x)
    let frac = x - floor
    int (if rng.NextDouble() < frac then floor + 1.0 else floor)

/// Compute LogNormal sigma from median/mean ratio
let logNormalSigma (median: float) (mean: float) : float =
    sqrt(2.0 * log(mean / median))

/// Sample trade count using LogNormal distribution
let sampleTradeCount (rng: Random) (orderFlowParams: OrderFlowParams) (duration: float) =
    let mu = log(orderFlowParams.MedianTradesPerSecond)
    let sigma = logNormalSigma orderFlowParams.MedianTradesPerSecond orderFlowParams.MeanTradesPerSecond
    let count = LogNormal(mu, sigma, rng).Sample() * duration
    max 1 (stochasticRound rng count)

/// Convert median/mean parameterization to LogNormal mu/sigma
let activityMuSigma (activityParams: ActivityParams) : float * float =
    let mu = log(activityParams.MedianSize)
    let sigma = logNormalSigma activityParams.MedianSize activityParams.MeanSize
    (mu, sigma)

/// Sample size from LogNormal distribution
let sampleSize (rng: Random) (mu: float) (sigma: float) : int =
    let rec loop() =
        let rawSize = LogNormal(mu, sigma, rng).Sample()
        let size = rawSize |> stochasticRound rng
        if size > 0 then size else loop()
    loop()

/// Generate uniformly distributed timestamps within an interval
let generateTimestamps (rng: Random) (startTime: float) (duration: float) (count: int) : float[] =
    let timestamps = Array.init count (fun _ -> startTime + rng.NextDouble() * duration)
    Array.sortInPlace timestamps
    timestamps

let clip lo hi x = max lo (min hi x)

/// Sample from a truncated standard normal on [lo, hi] using inverse CDF
let sampleTruncatedNormal (rng: Random) (lo: float) (hi: float) : float =
    let lo = clip -38.0 8.2 lo
    let hi = clip -38.0 8.2 hi
    if lo > hi then failwith "Support shouldn't be greater than resistance after clipping."
    let cdfLo = Normal.CDF(0.0, 1.0, lo)
    let cdfHi = Normal.CDF(0.0, 1.0, hi)
    let u = cdfLo + rng.NextDouble() * (cdfHi - cdfLo)
    Normal.InvCDF(0.0, 1.0, u)

/// Metropolis-Hastings step for S/R noise: target is N(0, sigma), proposal is current + sqrt(dt) * sigma * z
let mhStepSRNoise (rng: Random) (sigma: float) (dt: float) (current: float) : float =
    let proposal = current + sigma * sqrt(dt) * Normal.Sample(rng, 0.0, 1.0)
    let logAccept = (current * current - proposal * proposal) / (2.0 * sigma * sigma)
    if log(rng.NextDouble()) < logAccept then proposal else current

/// Generate prices and sizes using truncated normal S/R model with actual dt
let generatePricesAndSizes 
    (rng: Random) 
    (priceParams: PriceParams) 
    (orderFlowParams: OrderFlowParams)
    (activityParams: ActivityParams)
    (srParams: SRParams)
    (startPrice: float) 
    (duration: float)
    (timestamps: float[])
    : (float * int)[] * float =
    
    let count = timestamps.Length
    if count = 0 then
        [||], startPrice
    else
        let mu, sigma = activityMuSigma activityParams
        let results = Array.zeroCreate count
        let mutable logPrice = log startPrice
        
        let logStartPrice = logPrice
        let expectedTradeCount = orderFlowParams.MeanTradesPerSecond * duration
        let bps = 1e-4
        let tradeCountVar = float count / expectedTradeCount
        let tradeCountVol = sqrt tradeCountVar
        let vol = priceParams.VolatilityBps * bps * tradeCountVol
        let driftBps = priceParams.DriftToVolRatio * priceParams.VolatilityBps
        let logEndPrice = logStartPrice + driftBps * bps * tradeCountVar * duration + vol * sqrt duration * Normal(0.0, 1.0, rng).Sample()
        
        let mutable prevTime = timestamps.[0]
        let srNoise = srParams.NoiseBps * bps
        let mutable supportNoise = srNoise * Normal.Sample(rng, 0.0, 1.0)
        let mutable resistanceNoise = srNoise * Normal.Sample(rng, 0.0, 1.0)
        
        for i in 0 .. count - 1 do
            let dt = if i = 0 then 1.0 / orderFlowParams.MeanTradesPerSecond else timestamps.[i] - prevTime
            let dtVol = sqrt(max dt 1e-9)
            prevTime <- timestamps.[i]
            
            let size = sampleSize rng mu sigma
            let sizeVol = sqrt(float size / activityParams.MeanSize)
            let vol = vol * sizeVol * dtVol
            
            let frac = timestamps.[i] / duration
            let supportBase = logStartPrice + (logEndPrice - logStartPrice) * frac
            let resistanceBase = logEndPrice
            supportNoise <- mhStepSRNoise rng srNoise dt supportNoise
            resistanceNoise <- mhStepSRNoise rng srNoise dt resistanceNoise
            let support = min supportBase resistanceBase - abs supportNoise
            let resistance = max supportBase resistanceBase + abs resistanceNoise
            logPrice <- clip support resistance logPrice            
            let zLo = (support - logPrice) / vol
            let zHi = (resistance - logPrice) / vol
            let z = sampleTruncatedNormal rng zLo zHi           
            logPrice <- logPrice + vol * z
            results.[i] <- exp logPrice, size
        
        results, exp logEndPrice

/// Generate trades for a single trend episode
/// Returns trades and the ending price for chaining to next episode
let generateEpisodeTrades (rng: Random) (startPrice: float) (episode: Episode<Trend>) : Trade[] * float =
    let durationSeconds = episode.Duration * 60.0
    let orderFlowParams = getOrderFlowParams episode.Label
    let priceParams = getPriceParams episode.Label
    let activityParams = getActivityParams episode.Label
    let srParams = getSRParams episode.Label
    
    let tradeCount = sampleTradeCount rng orderFlowParams durationSeconds
    let timestamps = generateTimestamps rng 0.0 durationSeconds tradeCount
    let pricesAndSizes, endPrice = generatePricesAndSizes rng priceParams orderFlowParams activityParams srParams startPrice durationSeconds timestamps
    
    let trades = Array.init tradeCount (fun i -> 
        let price, size = pricesAndSizes.[i]
        {
            Time = timestamps.[i]
            Price = price
            Size = size
            Trend = episode.Label
        })
    
    trades, endPrice

/// Print summary statistics for generated trades
let printTradesSummary (trades: Trade[]) : unit =
    if trades.Length = 0 then
        printfn "No trades generated"
    else
        let duration = trades.[trades.Length - 1].Time
        let avgRate = float trades.Length / duration
        let totalVolume = trades |> Array.sumBy (fun t -> t.Size)
        let avgSize = float totalVolume / float trades.Length
        let maxSize = trades |> Array.map (fun t -> t.Size) |> Array.max
        let startPrice = trades.[0].Price
        let endPrice = trades.[trades.Length - 1].Price
        let returnPct = (endPrice - startPrice) / startPrice * 100.0
        
        printfn "Trade Summary:"
        printfn "  Total trades: %d" trades.Length
        printfn "  Duration: %.1f seconds (%.1f minutes)" duration (duration / 60.0)
        printfn "  Average rate: %.2f trades/sec" avgRate
        printfn "  Total volume: %d" totalVolume
        printfn "  Avg size: %.1f, Max size: %d" avgSize maxSize
        printfn "  Start price: %.4f, End price: %.4f" startPrice endPrice
        printfn "  Return: %.2f%%" returnPct
