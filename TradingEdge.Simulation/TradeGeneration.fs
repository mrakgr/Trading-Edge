module TradingEdge.Simulation.TradeGeneration

open System
open MathNet.Numerics.Distributions
open EpisodeMCMC

// =============================================================================
// Variance Calculation
// =============================================================================

/// Calculate variance for a given duration using the auction formula
let calculateVariance (baseVolBps: float) (volumeMean: float) (rateMean: float) (durationSeconds: float) : float =
    let bps = 1e-4
    let baseVol = baseVolBps * bps
    baseVol * baseVol * volumeMean * rateMean * durationSeconds

// =============================================================================
// Target Sampling
// =============================================================================

/// Multi-try step with pluggable log-density function
let inline multiTryStepGeneric (rng: Random) (price: float) (proposalVol: float) (density: float -> float) (n: int) : float =
    let candidates = Array.zeroCreate (2 * n + 1)
    let logWeights = Array.zeroCreate (2 * n + 1)
    candidates.[0] <- price
    logWeights.[0] <- density price
    for i in 0 .. n - 1 do
        let z = Normal.Sample(rng, 0.0, proposalVol)
        let yPos = price + z
        let yNeg = price - z
        candidates.[2 * i + 1] <- yPos
        candidates.[2 * i + 2] <- yNeg
        logWeights.[2 * i + 1] <- density yPos
        logWeights.[2 * i + 2] <- density yNeg
    let maxW = Array.max logWeights
    let weights = logWeights |> Array.map (fun w -> exp(w - maxW))
    let idx = Categorical.Sample(rng, weights)
    candidates.[idx]

/// Multi-try Metropolis step: propose n moves + n negated + current, select by target likelihood
let multiTryStep (rng: Random) (price: float) (proposalVol: float) (targetMean: float) (targetSigma: float) (n: int) : float =
    multiTryStepGeneric rng price proposalVol (fun x -> Normal.PDFLn(targetMean, targetSigma, x)) n

let stochasticRound (rng: Random) (x: float) : int =
    let floor = Math.Floor(x)
    let frac = x - floor
    int (if rng.NextDouble() < frac then floor + 1.0 else floor)

// =============================================================================
// Subepisode Generation
// =============================================================================

let variancePartitionParent = 0.75
let variancePartitionChild = 0.25

type SubepisodeResult<'a> = {
    Instance: EpisodeInstance<'a>
    Target: float
    Variance: float
}

/// Generate subepisodes with targets from a parent episode
let generateSubepisodes
    (rng: Random)
    (baseVolBps: float)
    (parentTarget: float)
    (parentVolume: float)
    (parentRate: float)
    (parentDuration: float)
    (childEpisodeSeries: EpisodeSeries<'a>)
    : SubepisodeResult<'a>[] =

    // Use MCMC to sample child episode instances (with durations)
    let childInstances = MCMC.run MCMC.defaultConfig childEpisodeSeries parentDuration rng

    // Calculate variance for each child
    let childVariances =
        childInstances |> Array.map (fun instance ->
            let actualVolume = parentVolume * instance.Episode.VolumeMean
            let actualRate = parentRate * instance.Episode.RateMean
            calculateVariance baseVolBps actualVolume actualRate instance.Duration
        )

    // Parent gets 75% of total variance (sum of all child variances)
    let totalVariance = Array.sum childVariances
    let parentTargetSigma = sqrt(variancePartitionParent * totalVariance)

    // Sample target for each child as a random walk
    let results, _ = Array.mapFold (fun currentTarget (instance, childVariance) ->
        let childTargetVariance = variancePartitionChild * childVariance
        let newTarget = multiTryStep rng currentTarget (sqrt childVariance) parentTarget parentTargetSigma 10
        let result = { Instance = instance; Target = newTarget; Variance = childTargetVariance }
        (result, newTarget)
    ) parentTarget (Array.zip childInstances childVariances)

    results