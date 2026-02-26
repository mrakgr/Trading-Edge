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
    RateProposalVol: float
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

/// HMM hold parameters for TightHold episodes
type HoldParams = {
    LaplaceScaleFraction: float  // Laplace scale as fraction of TargetVolBps
    HoldProposalFraction: float  // Hold proposal vol as fraction of baseline ProposalVolBps
    HoldDurationSec: float       // Average hold duration in seconds
    LooseDurationSec: float      // Average loose/fakeout duration in seconds
}

let defaultHoldParams = {
    LaplaceScaleFraction = 0.1
    HoldProposalFraction = 0.1
    HoldDurationSec = 15.0
    LooseDurationSec = 0.5
}

/// Session-wide baseline parameters
type SessionBaseline = {
    ProposalVolBps: float   // Proposal random walk σ in bps
    MeanSize: float         // Baseline mean trade size for scaling
}

let defaultBaseline = { ProposalVolBps = 0.375; MeanSize = 100.0 }

let getOrderFlowParams (trend: Trend) : OrderFlowParams =
    match trend with
    | StrongUptrend   -> { MedianTradesPerSecond = 35.0; MeanTradesPerSecond = 40.0; RateProposalVol = 0.08 }
    | MidUptrend      -> { MedianTradesPerSecond = 17.0; MeanTradesPerSecond = 20.0; RateProposalVol = 0.06 }
    | WeakUptrend     -> { MedianTradesPerSecond = 8.5;  MeanTradesPerSecond = 10.0; RateProposalVol = 0.05 }
    | Consolidation   -> { MedianTradesPerSecond = 4.0;  MeanTradesPerSecond = 5.0;  RateProposalVol = 0.03 }
    | WeakDowntrend   -> { MedianTradesPerSecond = 8.5;  MeanTradesPerSecond = 10.0; RateProposalVol = 0.05 }
    | MidDowntrend    -> { MedianTradesPerSecond = 17.0; MeanTradesPerSecond = 20.0; RateProposalVol = 0.06 }
    | StrongDowntrend -> { MedianTradesPerSecond = 35.0; MeanTradesPerSecond = 40.0; RateProposalVol = 0.08 }
    | TightHold       -> { MedianTradesPerSecond = 60.0; MeanTradesPerSecond = 80.0; RateProposalVol = 0.15 }

let getTargetParams (trend: Trend) : TargetParams =
    match trend with
    | StrongUptrend   -> { MoveSigmaMedian = 4.0; MoveSigmaMean = 5.0; TargetVolBps = 24.0 }
    | MidUptrend      -> { MoveSigmaMedian = 2.5; MoveSigmaMean = 3.0; TargetVolBps = 18.0 }
    | WeakUptrend     -> { MoveSigmaMedian = 1.2; MoveSigmaMean = 1.5; TargetVolBps = 12.0 }
    | Consolidation   -> { MoveSigmaMedian = 0.2; MoveSigmaMean = 0.5; TargetVolBps = 9.0 }
    | WeakDowntrend   -> { MoveSigmaMedian = 1.2; MoveSigmaMean = 1.5; TargetVolBps = 12.0 }
    | MidDowntrend    -> { MoveSigmaMedian = 2.5; MoveSigmaMean = 3.0; TargetVolBps = 18.0 }
    | StrongDowntrend -> { MoveSigmaMedian = 4.0; MoveSigmaMean = 5.0; TargetVolBps = 24.0 }
    | TightHold       -> { MoveSigmaMedian = 0.1; MoveSigmaMean = 0.2; TargetVolBps = 9.0 }

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

let private baselineSizeMu, private baselineSizeSigma =
    let p = getActivityParams Consolidation
    logNormalMuSigma p.MedianSize p.MeanSize

let sampleSize (rng: Random) (mu: float) (sigma: float) : int =
    let median = exp mu
    let rec sampleBelow () =
        let v = LogNormal(baselineSizeMu, baselineSizeSigma, rng).Sample()
        if v < median then v else sampleBelow ()
    let rec loop () =
        let raw = LogNormal(mu, sigma, rng).Sample()
        let size = if raw < median then sampleBelow () else raw
        let rounded = stochasticRound rng size
        if rounded > 0 then rounded else loop ()
    loop ()

let generateTimestamps (rng: Random) (startTime: float) (duration: float) (count: int) : float[] =
    let timestamps = Array.init count (fun _ -> startTime + rng.NextDouble() * duration)
    Array.sortInPlace timestamps
    timestamps

/// Multi-try step with pluggable log-density function
let multiTryStepGeneric (rng: Random) (logPrice: float) (proposalVol: float) (logDensity: float -> float) (n: int) : float =
    let candidates = Array.zeroCreate (2 * n + 1)
    let logWeights = Array.zeroCreate (2 * n + 1)
    candidates.[0] <- logPrice
    logWeights.[0] <- logDensity logPrice
    for i in 0 .. n - 1 do
        let z = Normal.Sample(rng, 0.0, proposalVol)
        let yPos = logPrice + z
        let yNeg = logPrice - z
        candidates.[2 * i + 1] <- yPos
        candidates.[2 * i + 2] <- yNeg
        logWeights.[2 * i + 1] <- logDensity yPos
        logWeights.[2 * i + 2] <- logDensity yNeg
    let maxW = Array.max logWeights
    let weights = logWeights |> Array.map (fun w -> exp(w - maxW))
    let idx = Categorical.Sample(rng, weights)
    candidates.[idx]

/// Multi-try Metropolis step: propose n moves + n negated + current, select by target likelihood
let multiTryStep (rng: Random) (logPrice: float) (proposalVol: float) (targetMean: float) (targetSigma: float) (n: int) : float =
    multiTryStepGeneric rng logPrice proposalVol (fun x -> Normal.PDFLn(targetMean, targetSigma, x)) n

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

    let priceTransition = 
        match episode.Label with
        | TightHold ->
            let holdParams = defaultHoldParams
            let mutable holding = true
            let holdLevel = targetMean
            let laplaceScale = targetSigma * holdParams.LaplaceScaleFraction
            let holdProposalVol = proposalVol * holdParams.HoldProposalFraction
            let meanRate = orderFlowParams.MeanTradesPerSecond
            let pHoldToLoose = 1.0 / (holdParams.HoldDurationSec * meanRate)
            let pLooseToHold = 1.0 / (holdParams.LooseDurationSec * meanRate)
            fun logPrice ->
                // HMM transition
                if holding then
                    if rng.NextDouble() < pHoldToLoose then holding <- false
                else
                    if rng.NextDouble() < pLooseToHold then holding <- true
                // Price step depends on HMM state
                let pVol = if holding then holdProposalVol else proposalVol
                let logDensity =
                    if holding then fun x -> Laplace.PDFLn(holdLevel, laplaceScale, x)
                    else fun x -> Normal.PDFLn(holdLevel, targetSigma, x)
                multiTryStepGeneric rng logPrice pVol logDensity 10
        | StrongDowntrend | StrongUptrend | MidUptrend | MidDowntrend | WeakUptrend | WeakDowntrend | Consolidation ->
            fun logPrice ->
                multiTryStep rng logPrice proposalVol targetMean targetSigma 10

    let trades = ResizeArray<Trade>()
    let mutable logPrice = log startPrice
    let mutable logRate = startLogRate
    let mutable time = 0.0

    while time < durationSeconds do
        let dt = Exponential.Sample(rng, exp logRate)
        time <- time + dt
        if time < durationSeconds then
            let sqrtDt = sqrt dt
            logRate <- multiTryStep rng logRate (orderFlowParams.RateProposalVol * sqrtDt) logRateTarget logRateTargetSigma 10
            logPrice <- priceTransition logPrice
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
