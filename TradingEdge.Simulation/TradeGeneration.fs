module TradingEdge.Simulation.TradeGeneration

open System
open MathNet.Numerics.Distributions
open EpisodeMCMC

// =============================================================================
// Variance Calculation
// =============================================================================

[<Literal>]
let bps = 1e-4

/// Calculate variance for a given duration using the auction formula
let calculateVariance (baseVolBps: float) (volumeMean: float) (rateMean: float) (durationSeconds: float) : float =
    let baseVol = baseVolBps * bps
    baseVol * baseVol * volumeMean * rateMean * durationSeconds

// =============================================================================
// Generation Context and Utilities
// =============================================================================

/// Common parameters for recursive generation
type GenerationContext<'label> = {
    Label: 'label
    StartPrice: float
    StartTime: float
    ParentTargetAndVariance: (float * float) list
    ParentVolume: float
    ParentRate: float
    ParentDuration: float
    ParentLabels: string list
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

/// Multi-try Metropolis template: proposes n symmetric pairs around current price, weights by density,
/// and selects via categorical sampling. When is_calculate_ev is true, also computes risk-adjusted EV
/// as (expected_price - price) / proposalVol; otherwise returns nan to skip the extra work.
let inline private multiTryStepTemplate (is_calculate_ev : bool) (rng: Random) (price: float) (proposalVol: float) (density: float -> float) (n: int) =
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
    let riskAdjustedEV =
        if is_calculate_ev then
            let totalWeight = Array.sum weights
            let normalizedWeights = weights |> Array.map (fun w -> w / totalWeight)
            let expectedPrice = Array.map2 (fun c w -> c * w) candidates normalizedWeights |> Array.sum
            let ev = expectedPrice - price
            ev / proposalVol
        else nan
    candidates.[idx], riskAdjustedEV

/// Multi-try step with pluggable log-density function
let inline multiTryStepGeneric (rng: Random) (price: float) (proposalVol: float) (density: float -> float) (n: int) : float =
    multiTryStepTemplate false rng price proposalVol density n |> fst

/// Multi-try step with pluggable log-density function, also returns risk-adjusted EV
let inline multiTryStepGenericWithEV (rng: Random) (price: float) (proposalVol: float) (density: float -> float) (n: int) : float * float =
    multiTryStepTemplate true rng price proposalVol density n

/// Multi-try Metropolis step: propose n moves + n negated + current, select by target likelihood
let multiTryStep (rng: Random) (price: float) (proposalVol: float) (targetMean: float) (targetSigma: float) (n: int) : float =
    multiTryStepGeneric rng price proposalVol (fun x -> Normal.PDFLn(targetMean, targetSigma, x)) n

/// Multi-try Metropolis step that also returns risk-adjusted EV
let multiTryStepWithEV (rng: Random) (price: float) (proposalVol: float) (targetMean: float) (targetSigma: float) (n: int) : float * float =
    multiTryStepGenericWithEV rng price proposalVol (fun x -> Normal.PDFLn(targetMean, targetSigma, x)) n

// =============================================================================
// Trade Generation
// =============================================================================

/// Generate trades for a leaf episode (no child episodes)
let generateTrades
    (rng: Random)
    (baseVolBps: float)
    (ctx: GenerationContext<'label>)
    : Trade[] * float =

    let proposalVol = baseVolBps * bps
    let parentTarget, parentVariance = ctx.ParentTargetAndVariance |> List.head
    let targetSigma = sqrt parentVariance

    let trades = ResizeArray<Trade>()
    let mutable price = ctx.StartPrice
    let mutable time = ctx.StartTime

    let volumeMedian = ctx.ParentVolume / 2.0
    let gapMean = 1.0 / ctx.ParentRate
    let gapMedian = gapMean / 2.0

    while time < ctx.StartTime + ctx.ParentDuration do
        let gap = sampleGap rng gapMedian gapMean
        time <- time + gap
        if time < ctx.StartTime + ctx.ParentDuration then
            let size = sampleSize rng volumeMedian ctx.ParentVolume
            let sqrtSize = sqrt (float size)
            price <- multiTryStep rng price (proposalVol * sqrtSize) parentTarget targetSigma 10
            trades.Add({
                Time = time
                Price = price
                Size = size
                TargetMeanAndVariances = ctx.ParentTargetAndVariance
                Label = ctx.ParentLabels
            })

    let endPrice = price
    trades.ToArray(), endPrice

// =============================================================================
// Subepisode Generation
// =============================================================================

// Controls the variance distribution between the parent and child.
let variancePartitionParent = 0.75

/// Generate subepisodes with targets from a parent episode
let generateSubepisodes
    (rng: Random)
    (baseVolBps: float)
    (ctx: GenerationContext<'a>)
    (childEpisodeSeries: EpisodeSeries<'a,'b>)
    : GenerationContext<'b>[] =

    // Use MCMC to sample child episode instances (with durations)
    let childInstances = MCMC.run MCMC.defaultConfig childEpisodeSeries ctx.Label ctx.ParentDuration rng

    // Calculate the volume for the children.
    let childVolumes = 
        childInstances |> Array.map (fun instance ->
            let actualVolume = ctx.ParentVolume * instance.Episode.VolumeMean
            let actualRate = ctx.ParentRate * instance.Episode.RateMean
            actualVolume * actualRate * instance.Duration
            )
    // Calculate total volume across all children
    let totalVolume = Array.sum childVolumes

    // Distribute parent variance proportionally based on volume
    let parentTarget, parentVariance = ctx.ParentTargetAndVariance |> List.head
    let childVariances = childVolumes |> Array.map (fun cv -> parentVariance * (cv / totalVolume))

    // Parent uses 75% of variance for target sigma
    let parentTargetSigma = sqrt(variancePartitionParent * parentVariance)

    // Sample target for each child and build contexts
    let mutable currentTarget = ctx.StartPrice
    let mutable currentTime = ctx.StartTime
    Array.map3 (fun instance childVolume childVariance ->
        let newTarget = multiTryStep rng currentTarget (baseVolBps * bps * sqrt childVolume) parentTarget parentTargetSigma 10
        let childVariance' = (1. - variancePartitionParent) * childVariance
        let childCtx = {
            Label = instance.Episode.Label
            StartPrice = currentTarget
            StartTime = currentTime
            ParentTargetAndVariance = (newTarget, childVariance') :: ctx.ParentTargetAndVariance
            ParentVolume = ctx.ParentVolume * instance.Episode.VolumeMean
            ParentRate = ctx.ParentRate * instance.Episode.RateMean
            ParentDuration = instance.Duration
            ParentLabels = string instance.Episode.Label :: ctx.ParentLabels
        }
        currentTarget <- newTarget
        currentTime <- currentTime + instance.Duration
        childCtx
    ) childInstances childVolumes childVariances

// =============================================================================
// Recursive Episode Processing
// =============================================================================

/// Leaf function: Generate trades from subepisode contexts
let generateTradesFromSubepisodes
    (rng: Random)
    (tradeVolBps: float)
    (subepisodes: GenerationContext<'label>[])
    : Trade[] =

    let allTrades = ResizeArray<Trade>()
    let mutable currentPrice = (Array.head subepisodes).StartPrice
    for ctx in subepisodes do
        let updatedCtx = { ctx with StartPrice = currentPrice }
        let trades, endPrice = generateTrades rng tradeVolBps updatedCtx
        allTrades.AddRange(trades)
        currentPrice <- endPrice
    allTrades.ToArray()

/// Expand contexts by generating child subepisodes for each
let expandContexts<'a,'b>
    (rng: Random)
    (baseVolBps: float)
    (parentContexts: GenerationContext<'a>[])
    (childEpisodes: EpisodeSeries<'a,'b>)
    : GenerationContext<'b>[] =

    let allChildren = ResizeArray<GenerationContext<'b>>()
    let mutable currentPrice = (Array.head parentContexts).StartPrice
    let mutable currentTime = (Array.head parentContexts).StartTime

    for parentCtx in parentContexts do
        let updatedParentCtx = { parentCtx with StartPrice = currentPrice; StartTime = currentTime }
        let children = generateSubepisodes rng baseVolBps updatedParentCtx childEpisodes
        allChildren.AddRange(children)

        let lastChild = Array.last children
        currentPrice <- lastChild.StartPrice
        currentTime <- lastChild.StartTime + lastChild.ParentDuration

    allChildren.ToArray()

/// Node function: Generate child subepisodes from parent and child episode series
let generateNodeLevel<'a,'b,'c>
    (rng: Random)
    (baseVolBps: float)
    (parentCtx: GenerationContext<'a>)
    (parentEpisodes: EpisodeSeries<'a,'b>)
    (childEpisodes: EpisodeSeries<'b,'c>)
    : GenerationContext<'c>[] =

    let parentContexts = generateSubepisodes rng baseVolBps parentCtx parentEpisodes
    expandContexts rng baseVolBps parentContexts childEpisodes

/// Generate trades from two levels of episodes
let generateNodeLevelTrades<'a,'b,'c>
    (rng: Random)
    (baseVolBps: float)
    (tradeVolBps: float)
    (parentCtx: GenerationContext<'a>)
    (parentEpisodes: EpisodeSeries<'a,'b>)
    (childEpisodes: EpisodeSeries<'b,'c>)
    : Trade[] =

    let childContexts = generateNodeLevel rng baseVolBps parentCtx parentEpisodes childEpisodes
    generateTradesFromSubepisodes rng tradeVolBps childContexts

// =============================================================================
// Testing
// =============================================================================

let testNestedGeneration () =
    let rng = Random(42)

    // Top level: Day parameters
    let startPrice = 100.0
    let dayTarget = 105.0
    let daySigma = 1.0
    let dayVariance = daySigma * daySigma
    let dayVolume = 100.0
    let dayRate = 10.0 * 60.
    let dayDuration = 390.0  // minutes
    let baseVolBps = 2.
    let tradeVolBps = 1.

    // Generate sessions
    printfn "=== Generating Sessions ==="
    let dayContext = {
        Label = ()
        StartPrice = startPrice
        StartTime = 0.0
        ParentTargetAndVariance = [(dayTarget, dayVariance)]
        ParentVolume = dayVolume
        ParentRate = dayRate
        ParentDuration = dayDuration
        ParentLabels = ["Day"]
    }
    let sessionResults = generateSubepisodes rng baseVolBps dayContext SessionLevel.episodes

    printfn "Start price: %.1f" startPrice
    printfn "Day target: %.6f" dayTarget
    printfn "Day sigma: %.6f" daySigma
    printfn "Day variance: %.6f" dayVariance
    printfn ""

    // Print session variances
    printfn "Session variances:"
    for ctx in sessionResults do
        let label = ctx.ParentLabels |> List.head
        let _, variance = ctx.ParentTargetAndVariance |> List.head
        printfn "  %s: %.6f" label variance
    let totalSessionVariance = sessionResults |> Array.sumBy (fun ctx -> ctx.ParentTargetAndVariance |> List.head |> snd)
    printfn "  Total session variance: %.6f (should be %.6f)" totalSessionVariance (0.25 * dayVariance)
    printfn ""

    // For each session, generate subepisodes
    printfn "=== Generating Subepisodes ==="
    let allSubepisodes = ResizeArray<GenerationContext<TrendLevel.Trend>>()

    for sessionCtx in sessionResults do
        let sessionLabel = sessionCtx.ParentLabels |> List.head
        let sessionTarget, sessionVariance = sessionCtx.ParentTargetAndVariance |> List.head

        printfn "Session: %s, Duration: %.2f min, Target: %.6f, Start price: %.6f"
            sessionLabel sessionCtx.ParentDuration sessionTarget sessionCtx.StartPrice

        let subepisodeResults = generateSubepisodes rng baseVolBps sessionCtx TrendLevel.episodes

        printfn "  Generated %d subepisodes" subepisodeResults.Length

        for subepisodeCtx in subepisodeResults do
            allSubepisodes.Add(subepisodeCtx)
            let trendLabel = subepisodeCtx.ParentLabels |> List.head
            let trendTarget, trendVariance = subepisodeCtx.ParentTargetAndVariance |> List.head
            printfn "    Trend: %s, Duration: %.2f min, Target: %.6f, Variance: %.6f"
                trendLabel subepisodeCtx.ParentDuration trendTarget trendVariance

        printfn ""

    printfn "=== Summary ==="
    printfn "Total sessions: %d" sessionResults.Length
    printfn "Total subepisodes: %d" allSubepisodes.Count

    allSubepisodes.ToArray()

/// Generate trades for a full day
let generateDayTrades
    (rng: Random)
    (startPrice: float)
    (baseVolBps: float)
    (tradeVolBps: float)
    (dayTarget: float)
    (daySigma: float)
    (dayVolume: float)
    (dayRate: float)
    (dayDuration: float)
    : Trade[] =

    let dayVariance = daySigma * daySigma
    let dayContext = {
        Label = ()
        StartPrice = startPrice
        StartTime = 0.0
        ParentTargetAndVariance = [(dayTarget, dayVariance)]
        ParentVolume = dayVolume
        ParentRate = dayRate
        ParentDuration = dayDuration
        ParentLabels = ["Day"]
    }
    generateNodeLevelTrades rng baseVolBps tradeVolBps dayContext SessionLevel.episodes TrendLevel.episodes