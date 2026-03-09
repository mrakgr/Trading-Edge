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

/// Sample a target with variance partitioning (75% parent, 25% child)
let sampleChildTarget (rng: Random) (parentTarget: float) (totalVariance: float) (childDurationFraction: float) : float =
    let parentVariance = 0.75 * totalVariance
    let childVariance = 0.25 * totalVariance * childDurationFraction
    Normal.Sample(rng, parentTarget, sqrt childVariance)


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

/// Generate subepisodes with targets from a parent episode
let generateSubepisodes
    (rng: Random)
    (baseVolBps: float)
    (parentTarget: float)
    (parentVolume: float)
    (parentRate: float)
    (parentDuration: float)
    (childEpisodeSeries: EpisodeSeries<'a>)
    : (EpisodeInstance<'a> * float * float)[] =

    // Use MCMC to sample child episode instances (with durations)
    let childInstances = MCMC.run MCMC.defaultConfig childEpisodeSeries parentDuration rng

    // For each child, calculate variance and sample target
    childInstances |> Array.map (fun instance ->
        // Calculate actual means (parent × child)
        let actualVolume = parentVolume * instance.Episode.VolumeMean
        let actualRate = parentRate * instance.Episode.RateMean

        // Calculate total variance for this child
        let childVariance = calculateVariance baseVolBps actualVolume actualRate instance.Duration

        // Child gets 25% of variance for variation around parent target
        let childTargetSigma = sqrt(0.25 * childVariance)

        // Sample child target using multi-try MCMC around parent target
        let childTarget = multiTryStep rng parentTarget childTargetSigma parentTarget childTargetSigma 10

        (instance, childTarget, childVariance)
    )