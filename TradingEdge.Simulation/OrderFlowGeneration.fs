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

/// Parameters for price generation (time-normalized, then applied per-trade)
type PriceParams = {
    VolatilityPerSecond: float   // Volatility per second (normalized to mean trade rate and size)
}

/// Support/resistance level parameters for a trend
type SRParams = {
    SupportOffset: float         // Starting support as log-offset from start price (negative = below)
    ResistanceOffset: float      // Starting resistance as log-offset from start price (positive = above)
    SupportDrift: float          // How much support moves per second (positive = dragged up)
    ResistanceDrift: float       // How much resistance moves per second (negative = dragged down)
    Noise: float                 // Gaussian noise std dev added to S/R levels per trade
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
    | StrongUptrend ->   { VolatilityPerSecond = 300e-6 }
    | MidUptrend ->      { VolatilityPerSecond = 240e-6 }
    | WeakUptrend ->     { VolatilityPerSecond = 180e-6 }
    | Consolidation ->   { VolatilityPerSecond = 120e-6 }
    | WeakDowntrend ->   { VolatilityPerSecond = 180e-6 }
    | MidDowntrend ->    { VolatilityPerSecond = 240e-6 }
    | StrongDowntrend -> { VolatilityPerSecond = 300e-6 }

/// Get support/resistance parameters for a trend type
/// For uptrends: support drags upward. For downtrends: resistance drags downward.
let getSRParams (trend: Trend) : SRParams =
    match trend with
    | StrongUptrend ->   { SupportOffset = -0.002; ResistanceOffset = 0.004; SupportDrift = 50e-6; ResistanceDrift = 0.0; Noise = 0.0005 }
    | MidUptrend ->      { SupportOffset = -0.002; ResistanceOffset = 0.003; SupportDrift = 25e-6; ResistanceDrift = 0.0; Noise = 0.0004 }
    | WeakUptrend ->     { SupportOffset = -0.002; ResistanceOffset = 0.002; SupportDrift = 10e-6; ResistanceDrift = 0.0; Noise = 0.0003 }
    | Consolidation ->   { SupportOffset = -0.002; ResistanceOffset = 0.002; SupportDrift = 0.0;   ResistanceDrift = 0.0; Noise = 0.0003 }
    | WeakDowntrend ->   { SupportOffset = -0.002; ResistanceOffset = 0.002; SupportDrift = 0.0;   ResistanceDrift = -10e-6; Noise = 0.0003 }
    | MidDowntrend ->    { SupportOffset = -0.003; ResistanceOffset = 0.002; SupportDrift = 0.0;   ResistanceDrift = -25e-6; Noise = 0.0004 }
    | StrongDowntrend -> { SupportOffset = -0.004; ResistanceOffset = 0.002; SupportDrift = 0.0;   ResistanceDrift = -50e-6; Noise = 0.0005 }

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

/// Sample trade count using LogNormal distribution
let sampleTradeCount (rng: Random) (orderFlowParams: OrderFlowParams) (duration: float) =
    let mu = log(orderFlowParams.MedianTradesPerSecond)
    let sigma = sqrt(2.0 * log(orderFlowParams.MeanTradesPerSecond / orderFlowParams.MedianTradesPerSecond))
    let count = LogNormal(mu, sigma, rng).Sample() * duration
    max 1 (stochasticRound rng count)

/// Convert median/mean parameterization to LogNormal mu/sigma
let activityMuSigma (activityParams: ActivityParams) : float * float =
    let mu = log(activityParams.MedianSize)
    let sigma = sqrt(2.0 * log(activityParams.MeanSize / activityParams.MedianSize))
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

/// Correction factor so E[correction * sqrt(size/meanSize)] = 1 for LogNormal
let getActivityCorrection (sigma: float) : float =
    exp(sigma * sigma / 8.0)

/// Sample from a truncated standard normal on [lo, hi] using inverse CDF
let sampleTruncatedNormal (rng: Random) (lo: float) (hi: float) : float =
    let cdfLo = Normal.CDF(0.0, 1.0, lo)
    let cdfHi = Normal.CDF(0.0, 1.0, hi)
    let u = cdfLo + rng.NextDouble() * (cdfHi - cdfLo)
    Normal.InvCDF(0.0, 1.0, u)

/// Generate prices and sizes using truncated normal S/R model with actual dt
let generatePricesAndSizes 
    (rng: Random) 
    (priceParams: PriceParams) 
    (orderFlowParams: OrderFlowParams)
    (activityParams: ActivityParams)
    (srParams: SRParams)
    (startPrice: float) 
    (timestamps: float[])
    : (float * int)[] * float =
    
    let count = timestamps.Length
    if count = 0 then
        [||], startPrice
    else
        let mu, sigma = activityMuSigma activityParams
        let correction = getActivityCorrection sigma
        let normal = Normal(0.0, 1.0, rng)
        let srNormal = Normal(0.0, 1.0, rng)
        let results = Array.zeroCreate count
        let mutable logPrice = log(startPrice)
        let logStartPrice = logPrice
        
        let expectedSqrtSize = sqrt(activityParams.MeanSize)
        let expectedTradeCount = orderFlowParams.MeanTradesPerSecond * (if count > 1 then timestamps.[count-1] - timestamps.[0] else 1.0)
        let tradeCountScale = sqrt(float count / (max 1.0 expectedTradeCount))
        let vol = priceParams.VolatilityPerSecond
        
        let mutable prevTime = timestamps.[0]
        
        for i in 0 .. count - 1 do
            let dt = if i = 0 then 1.0 / orderFlowParams.MeanTradesPerSecond else timestamps.[i] - prevTime
            let sqrtDt = sqrt(max dt 1e-9)
            prevTime <- timestamps.[i]
            
            let size = sampleSize rng mu sigma
            let sizeNorm = correction * sqrt(float size) / expectedSqrtSize
            let scaledVol = vol * sizeNorm * tradeCountScale
            
            let t = timestamps.[i]
            let support = logStartPrice + srParams.SupportOffset + srParams.SupportDrift * t + srParams.Noise * srNormal.Sample()
            let resistance = logStartPrice + srParams.ResistanceOffset + srParams.ResistanceDrift * t + srParams.Noise * srNormal.Sample()
            
            let zLo = (support - logPrice) / (scaledVol * sqrtDt)
            let zHi = (resistance - logPrice) / (scaledVol * sqrtDt)
            
            let z = 
                if zLo >= zHi then normal.Sample()
                else sampleTruncatedNormal rng zLo zHi
            
            logPrice <- logPrice + (-scaledVol * scaledVol / 2.0) * dt + scaledVol * sqrtDt * z
            results.[i] <- (exp(logPrice), size)
        
        results, exp(logPrice)

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
    let pricesAndSizes, endPrice = generatePricesAndSizes rng priceParams orderFlowParams activityParams srParams startPrice timestamps
    
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
