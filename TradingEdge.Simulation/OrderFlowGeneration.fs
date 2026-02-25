module TradingEdge.Simulation.OrderFlowGeneration

open System
open MathNet.Numerics.Distributions
open TradingEdge.Simulation.EpisodeMCMC

/// A single trade
type Trade = {
    Time: float
    Price: float
    Size: int
    Trend: Trend
    TargetMean: float   // Target distribution mean (price space)
    TargetSigma: float  // Target distribution sigma (price space)
}

/// Order flow parameters per trend
type OrderFlowParams = {
    MedianTradesPerSecond: float
    MeanTradesPerSecond: float
}

/// Target distribution parameters per trend
type TargetParams = {
    MoveSigmaMedian: float  // Median of LogNormal for move magnitude in target σ units
    MoveSigmaMean: float    // Mean of LogNormal for move magnitude
    TargetVolBps: float     // Target distribution σ in bps
}

/// Trade size parameters
type ActivityParams = {
    MedianSize: float
    MeanSize: float
}

/// Session-wide baseline parameters
type SessionBaseline = {
    ProposalVolBps: float   // Proposal random walk σ in bps
    RateProposalVol: float  // Proposal σ for log-rate MCMC walk
    MeanSize: float         // Baseline mean trade size for scaling
}

let defaultBaseline = { ProposalVolBps = 0.375; RateProposalVol = 0.05; MeanSize = 100.0 }

let getOrderFlowParams (trend: Trend) : OrderFlowParams =
    match trend with
    | StrongUptrend   -> { MedianTradesPerSecond = 35.0; MeanTradesPerSecond = 40.0 }
    | MidUptrend      -> { MedianTradesPerSecond = 17.0; MeanTradesPerSecond = 20.0 }
    | WeakUptrend     -> { MedianTradesPerSecond = 8.5;  MeanTradesPerSecond = 10.0 }
    | Consolidation   -> { MedianTradesPerSecond = 4.0;  MeanTradesPerSecond = 5.0 }
    | WeakDowntrend   -> { MedianTradesPerSecond = 8.5;  MeanTradesPerSecond = 10.0 }
    | MidDowntrend    -> { MedianTradesPerSecond = 17.0; MeanTradesPerSecond = 20.0 }
    | StrongDowntrend -> { MedianTradesPerSecond = 35.0; MeanTradesPerSecond = 40.0 }
    | TightHold       -> { MedianTradesPerSecond = 15.0; MeanTradesPerSecond = 20.0 }

let getTargetParams (trend: Trend) : TargetParams =
    match trend with
    | StrongUptrend   -> { MoveSigmaMedian = 4.0; MoveSigmaMean = 5.0; TargetVolBps = 24.0 }
    | MidUptrend      -> { MoveSigmaMedian = 2.5; MoveSigmaMean = 3.0; TargetVolBps = 18.0 }
    | WeakUptrend     -> { MoveSigmaMedian = 1.2; MoveSigmaMean = 1.5; TargetVolBps = 12.0 }
    | Consolidation   -> { MoveSigmaMedian = 0.2; MoveSigmaMean = 0.5; TargetVolBps = 9.0 }
    | WeakDowntrend   -> { MoveSigmaMedian = 1.2; MoveSigmaMean = 1.5; TargetVolBps = 12.0 }
    | MidDowntrend    -> { MoveSigmaMedian = 2.5; MoveSigmaMean = 3.0; TargetVolBps = 18.0 }
    | StrongDowntrend -> { MoveSigmaMedian = 4.0; MoveSigmaMean = 5.0; TargetVolBps = 24.0 }
    | TightHold       -> { MoveSigmaMedian = 0.1; MoveSigmaMean = 0.2; TargetVolBps = 3.0 }

let getActivityParams (trend: Trend) : ActivityParams =
    match trend with
    | StrongUptrend   -> { MedianSize = 100.0; MeanSize = 200.0 }
    | MidUptrend      -> { MedianSize = 100.0; MeanSize = 150.0 }
    | WeakUptrend     -> { MedianSize = 100.0; MeanSize = 120.0 }
    | Consolidation   -> { MedianSize = 100.0; MeanSize = 110.0 }
    | WeakDowntrend   -> { MedianSize = 100.0; MeanSize = 120.0 }
    | MidDowntrend    -> { MedianSize = 100.0; MeanSize = 150.0 }
    | StrongDowntrend -> { MedianSize = 100.0; MeanSize = 200.0 }
    | TightHold       -> { MedianSize = 100.0; MeanSize = 150.0 }

let stochasticRound (rng: Random) (x: float) : int =
    let floor = Math.Floor(x)
    let frac = x - floor
    int (if rng.NextDouble() < frac then floor + 1.0 else floor)

let logNormalSigma (median: float) (mean: float) : float =
    sqrt(2.0 * log(mean / median))

let sampleTradeCount (rng: Random) (orderFlowParams: OrderFlowParams) (duration: float) =
    let mu = log(orderFlowParams.MedianTradesPerSecond)
    let sigma = logNormalSigma orderFlowParams.MedianTradesPerSecond orderFlowParams.MeanTradesPerSecond
    let count = LogNormal(mu, sigma, rng).Sample() * duration
    max 1 (stochasticRound rng count)

let logNormalMuSigma (median: float) (mean: float) : float * float =
    let mu = log median
    let sigma = logNormalSigma median mean
    (mu, sigma)

let sampleSize (rng: Random) (mu: float) (sigma: float) : int =
    let rec loop() =
        let size = LogNormal(mu, sigma, rng).Sample() |> stochasticRound rng
        if size > 0 then size else loop()
    loop()

let generateTimestamps (rng: Random) (startTime: float) (duration: float) (count: int) : float[] =
    let timestamps = Array.init count (fun _ -> startTime + rng.NextDouble() * duration)
    Array.sortInPlace timestamps
    timestamps

/// Multi-try Metropolis step: propose n moves + n negated + current, select by target likelihood
let multiTryStep (rng: Random) (logPrice: float) (proposalVol: float) (targetMean: float) (targetSigma: float) (n: int) : float =
    let candidates = Array.zeroCreate (2 * n + 1)
    let logWeights = Array.zeroCreate (2 * n + 1)
    // Current position
    candidates.[0] <- logPrice
    logWeights.[0] <- Normal.PDFLn(targetMean, targetSigma, logPrice)
    // Proposals and their negations
    for i in 0 .. n - 1 do
        let z = Normal.Sample(rng, 0.0, proposalVol)
        let yPos = logPrice + z
        let yNeg = logPrice - z
        candidates.[2 * i + 1] <- yPos
        candidates.[2 * i + 2] <- yNeg
        logWeights.[2 * i + 1] <- Normal.PDFLn(targetMean, targetSigma, yPos)
        logWeights.[2 * i + 2] <- Normal.PDFLn(targetMean, targetSigma, yNeg)
    // Normalize via log-sum-exp and select using Categorical distribution
    let maxW = Array.max logWeights
    let weights = logWeights |> Array.map (fun w -> exp(w - maxW))
    let idx = Categorical.Sample(rng, weights)
    candidates.[idx]

/// Sample target mean for an episode
let sampleTargetMean (rng: Random) (prevTargetMean: float) (targetParams: TargetParams) (trend: Trend) : float =
    let bps = 1e-4
    let targetSigma = targetParams.TargetVolBps * bps
    let moveSigma = logNormalSigma targetParams.MoveSigmaMedian targetParams.MoveSigmaMean
    let moveMu = log targetParams.MoveSigmaMedian
    let kSigmas = LogNormal(moveMu, moveSigma, rng).Sample()
    let moveSize = kSigmas * targetSigma
    let sign =
        match trend with
        | StrongUptrend | MidUptrend | WeakUptrend -> 1.0
        | StrongDowntrend | MidDowntrend | WeakDowntrend -> -1.0
        | Consolidation | TightHold -> if rng.NextDouble() < 0.5 then 1.0 else -1.0
    prevTargetMean + sign * moveSize

/// Generate trades for a single trend episode with sequential MCMC rate walk
let generateEpisodeTrades (rng: Random) (startPrice: float) (prevTargetMean: float) (startLogRate: float) (baseline: SessionBaseline) (episode: Episode<Trend>) : Trade[] * float * float * float =
    let durationSeconds = episode.Duration * 60.0
    let orderFlowParams = getOrderFlowParams episode.Label
    let activityParams = getActivityParams episode.Label
    let targetParams = getTargetParams episode.Label
    let bps = 1e-4

    let targetMean = sampleTargetMean rng prevTargetMean targetParams episode.Label
    let targetSigma = targetParams.TargetVolBps * bps
    let proposalVol = baseline.ProposalVolBps * bps
    let logRateTarget, logRateTargetSigma = logNormalMuSigma orderFlowParams.MedianTradesPerSecond orderFlowParams.MeanTradesPerSecond
    let logSizeTarget, logSizeSigma = logNormalMuSigma activityParams.MedianSize activityParams.MeanSize
    
    let trades = ResizeArray<Trade>()
    let mutable logPrice = log startPrice
    let mutable logRate = startLogRate
    let mutable time = 0.0

    while time < durationSeconds do
        let dt = Exponential.Sample(rng, exp logRate)
        time <- time + dt
        if time < durationSeconds then
            let sqrtDt = sqrt dt
            // MCMC step on log-rate (scaled by sqrt dt)
            logRate <- multiTryStep rng logRate (baseline.RateProposalVol * sqrtDt) logRateTarget logRateTargetSigma 10
            // MCMC step on price
            logPrice <- multiTryStep rng logPrice proposalVol targetMean targetSigma 10
            let size = sampleSize rng logSizeTarget logSizeSigma
            trades.Add({
                Time = time
                Price = exp logPrice
                Size = size
                Trend = episode.Label
                TargetMean = exp targetMean
                TargetSigma = LogNormal(targetMean, targetSigma).StdDev
            })

    let endPrice = exp logPrice
    trades.ToArray(), endPrice, targetMean, logRate

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
        printfn "  Trades: %d, Duration: %.1fs, Rate: %.2f/s" trades.Length duration avgRate
        printfn "  Volume: %d, Avg: %.1f, Max: %d" totalVolume avgSize maxSize
        printfn "  Price: %.4f -> %.4f (%.2f%%)" startPrice endPrice returnPct
