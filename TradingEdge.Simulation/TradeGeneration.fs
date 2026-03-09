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

// Controls the variance distribution between the parent and child.
let variancePartitionParent = 0.75

type SubepisodeResult<'a> = {
    Instance: EpisodeInstance<'a>
    Target: float
    Variance: float
}

/// Generate subepisodes with targets from a parent episode
let generateSubepisodes
    (rng: Random)
    (parentVariance: float)
    (startPrice: float)
    (parentTarget: float)
    (parentVolume: float)
    (parentRate: float)
    (parentDuration: float)
    (childEpisodeSeries: EpisodeSeries<'a>)
    : SubepisodeResult<'a>[] * float =

    // Use MCMC to sample child episode instances (with durations)
    let childInstances = MCMC.run MCMC.defaultConfig childEpisodeSeries parentDuration rng

    // Calculate total volume across all children
    let totalVolume =
        childInstances |> Array.sumBy (fun instance ->
            let actualVolume = parentVolume * instance.Episode.VolumeMean
            let actualRate = parentRate * instance.Episode.RateMean
            actualVolume * actualRate * instance.Duration
        )

    // Distribute parent variance proportionally based on volume
    let childVariances =
        childInstances |> Array.map (fun instance ->
            let actualVolume = parentVolume * instance.Episode.VolumeMean
            let actualRate = parentRate * instance.Episode.RateMean
            let childVolume = actualVolume * actualRate * instance.Duration
            parentVariance * (childVolume / totalVolume)
        )

    // Parent uses 75% of variance for target sigma
    let parentTargetSigma = sqrt(variancePartitionParent * parentVariance)

    // Sample target for each child as a random walk starting from startPrice
    let mutable currentTarget = startPrice
    let results =
        Array.map2 (fun instance childVariance ->
            let newTarget = multiTryStep rng currentTarget (sqrt childVariance) parentTarget parentTargetSigma 10
            currentTarget <- newTarget
            { Instance = instance; Target = newTarget; Variance = (1. - variancePartitionParent) * childVariance }
        ) childInstances childVariances
    (results, currentTarget)

// =============================================================================
// Testing
// =============================================================================

let testNestedGeneration () =
    let rng = Random(42)

    // Top level: Day parameters
    let startPrice = 100.0
    let dayTarget = 150.0
    let daySigma = 10.0
    let dayVariance = daySigma * daySigma  // 100
    let dayVolume = 1.0
    let dayRate = 1.0
    let dayDuration = 390.0  // minutes

    // Session level episodes
    let sessionEpisodes =
        FixedOrder [|
            {
                Label = SessionLevel.Morning
                DurationParam = Distribution.LogNormal (45.0, 60.0)
                VolumeMean = sqrt 3.0
                RateMean = sqrt 3.0
            }
            {
                Label = SessionLevel.Mid
                DurationParam = Distribution.LogNormal (240.0, 270.0)
                VolumeMean = 1.0
                RateMean = 1.0
            }
            {
                Label = SessionLevel.Close
                DurationParam = Distribution.LogNormal (45.0, 60.0)
                VolumeMean = sqrt 3.0
                RateMean = sqrt 3.0
            }
        |]

    // Generate sessions
    printfn "=== Generating Sessions ==="
    let sessionResults, finalSessionTarget =
        generateSubepisodes rng dayVariance startPrice dayTarget dayVolume dayRate dayDuration sessionEpisodes

    printfn "Start price: %.1f" startPrice
    printfn "Day target: %.6f" dayTarget
    printfn "Day sigma: %.6f" daySigma
    printfn "Final session target: %.6f" finalSessionTarget
    printfn ""

    // For each session, generate subepisodes
    printfn "=== Generating Subepisodes ==="
    let allSubepisodes = ResizeArray<SubepisodeResult<TrendLevel.Trend>>()

    for sessionResult in sessionResults do
        let session = sessionResult.Instance.Episode
        let sessionDuration = sessionResult.Instance.Duration
        let sessionTarget = sessionResult.Target
        let sessionVariance = sessionResult.Variance

        printfn "Session: %A, Duration: %.2f min, Target: %.6f"
            session.Label sessionDuration sessionTarget

        // Generate subepisodes for this session (keep in minutes, don't convert to seconds)
        let subepisodeResults, finalSubepisodeTarget =
            generateSubepisodes
                rng
                sessionResult.Variance
                startPrice
                sessionTarget
                session.VolumeMean
                session.RateMean
                sessionDuration  // Keep in minutes
                TrendLevel.episodes

        printfn "  Generated %d subepisodes, final target: %.6f"
            subepisodeResults.Length finalSubepisodeTarget

        for subepisode in subepisodeResults do
            allSubepisodes.Add(subepisode)
            printfn "    Trend: %A, Duration: %.2f min, Target: %.6f, Variance: %.6f"
                subepisode.Instance.Episode.Label
                subepisode.Instance.Duration
                subepisode.Target
                subepisode.Variance

        printfn ""

    printfn "=== Summary ==="
    printfn "Total sessions: %d" sessionResults.Length
    printfn "Total subepisodes: %d" allSubepisodes.Count

    allSubepisodes.ToArray()