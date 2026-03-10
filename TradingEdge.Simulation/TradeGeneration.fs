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
// Generation Context and Utilities
// =============================================================================

/// Common parameters for recursive generation
type GenerationContext = {
    ParentVariance: float
    StartPrice: float
    ParentTarget: float
    ParentVolume: float
    ParentRate: float
    ParentDuration: float
}

let stochasticRound (rng: Random) (x: float) : int =
    let floor = Math.Floor(x)
    let frac = x - floor
    int (if rng.NextDouble() < frac then floor + 1.0 else floor)

let logNormalSigma (median: float) (mean: float) : float =
    sqrt(2.0 * log(mean / median))

let logNormalMuSigma (median: float) (mean: float) : float * float =
    let mu = log median
    let sigma = logNormalSigma median mean
    (mu, sigma)

let sampleSize (rng: Random) (median: float) (mean: float) : int =
    let mu, sigma = logNormalMuSigma median mean
    let rec loop () =
        let size = LogNormal(mu, sigma, rng).Sample()
        let rounded = stochasticRound rng size
        if rounded > 0 then rounded else loop ()
    loop ()

let sampleGap (rng: Random) (median: float) (mean: float) : float =
    let mu, sigma = logNormalMuSigma median mean
    LogNormal(mu, sigma, rng).Sample()

// =============================================================================
// Target Sampling (Multi-try MCMC)
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

// =============================================================================
// Trade Generation
// =============================================================================

/// Generate trades for a leaf episode (no child episodes)
let generateTrades
    (rng: Random)
    (baseVolBps: float)
    (ctx: GenerationContext)
    (startTime: float)
    (parentLabel: string)
    : Trade[] * float =

    let bps = 1e-4
    let proposalVol = baseVolBps * bps
    let targetSigma = sqrt ctx.ParentVariance

    let trades = ResizeArray<Trade>()
    let mutable price = ctx.StartPrice
    let mutable time = startTime

    let volumeMedian = ctx.ParentVolume / 2.0
    let gapMean = 1.0 / ctx.ParentRate
    let gapMedian = gapMean / 2.0

    while time < startTime + ctx.ParentDuration do
        let gap = sampleGap rng gapMedian gapMean
        time <- time + gap
        if time < startTime + ctx.ParentDuration then
            let size = sampleSize rng volumeMedian ctx.ParentVolume
            let sqrtSize = sqrt (float size)
            price <- multiTryStep rng price (proposalVol * sqrtSize) ctx.ParentTarget targetSigma 10
            trades.Add({
                Time = time
                Price = price
                Size = size
                TargetMeanAndVariances = [(ctx.ParentTarget, ctx.ParentVariance)]
                Label = [parentLabel]
            })

    let endPrice = price
    trades.ToArray(), endPrice

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
    (ctx: GenerationContext)
    (childEpisodeSeries: EpisodeSeries<'a>)
    : SubepisodeResult<'a>[] * float =

    // Use MCMC to sample child episode instances (with durations)
    let childInstances = MCMC.run MCMC.defaultConfig childEpisodeSeries ctx.ParentDuration rng

    // Calculate total volume across all children
    let totalVolume =
        childInstances |> Array.sumBy (fun instance ->
            let actualVolume = ctx.ParentVolume * instance.Episode.VolumeMean
            let actualRate = ctx.ParentRate * instance.Episode.RateMean
            actualVolume * actualRate * instance.Duration
        )

    // Distribute parent variance proportionally based on volume
    let childVariances =
        childInstances |> Array.map (fun instance ->
            let actualVolume = ctx.ParentVolume * instance.Episode.VolumeMean
            let actualRate = ctx.ParentRate * instance.Episode.RateMean
            let childVolume = actualVolume * actualRate * instance.Duration
            ctx.ParentVariance * (childVolume / totalVolume)
        )

    // Parent uses 75% of variance for target sigma
    let parentTargetSigma = sqrt(variancePartitionParent * ctx.ParentVariance)

    // Sample target for each child as a random walk starting from startPrice
    let mutable currentTarget = ctx.StartPrice
    let results =
        Array.map2 (fun instance childVariance ->
            let newTarget = multiTryStep rng currentTarget (sqrt childVariance) ctx.ParentTarget parentTargetSigma 10
            currentTarget <- newTarget
            { Instance = instance; Target = newTarget; Variance = (1. - variancePartitionParent) * childVariance }
        ) childInstances childVariances
    results, currentTarget

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
    let dayVolume = 100.0
    let dayRate = 10.0
    let dayDuration = 390.0  // minutes

    // Generate sessions
    printfn "=== Generating Sessions ==="
    let dayContext = {
        ParentVariance = dayVariance
        StartPrice = startPrice
        ParentTarget = dayTarget
        ParentVolume = dayVolume
        ParentRate = dayRate
        ParentDuration = dayDuration
    }
    let sessionResults, finalSessionTarget =
        generateSubepisodes rng dayContext SessionLevel.episodes

    printfn "Start price: %.1f" startPrice
    printfn "Day target: %.6f" dayTarget
    printfn "Day sigma: %.6f" daySigma
    printfn "Day variance: %.6f" dayVariance
    printfn "Final session target: %.6f" finalSessionTarget
    printfn ""

    // Print session variances
    printfn "Session variances:"
    for sr in sessionResults do
        printfn "  %A: %.6f" sr.Instance.Episode.Label sr.Variance
    let totalSessionVariance = sessionResults |> Array.sumBy (fun sr -> sr.Variance)
    printfn "  Total session variance: %.6f (should be %.6f)" totalSessionVariance (0.25 * dayVariance)
    printfn ""

    // For each session, generate subepisodes
    printfn "=== Generating Subepisodes ==="
    let allSubepisodes = ResizeArray<SubepisodeResult<TrendLevel.Trend>>()
    let mutable currentPrice = startPrice

    for sessionResult in sessionResults do
        let session = sessionResult.Instance.Episode
        let sessionDuration = sessionResult.Instance.Duration
        let sessionTarget = sessionResult.Target
        let sessionVariance = sessionResult.Variance

        printfn "Session: %A, Duration: %.2f min, Target: %.6f, Start price: %.6f"
            session.Label sessionDuration sessionTarget currentPrice

        // Generate subepisodes for this session (keep in minutes, don't convert to seconds)
        let sessionContext = {
            ParentVariance = sessionResult.Variance
            StartPrice = currentPrice
            ParentTarget = sessionTarget
            ParentVolume = session.VolumeMean
            ParentRate = session.RateMean
            ParentDuration = sessionDuration
        }
        let subepisodeResults, finalSubepisodeTarget =
            generateSubepisodes rng sessionContext TrendLevel.episodes

        currentPrice <- finalSubepisodeTarget  // Update price for next session

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