module TradingEdge.Simulation.OrderFlowGeneration

open System
open MathNet.Numerics.Distributions
open TradingEdge.Simulation.EpisodeMCMC
open TradingEdge.Simulation.TradeDataTDigests
open TradingEdge.Simulation.ExponentialTilt
open TDigest

/// A single trade
type Trade = {
    Time: float
    Price: float
    Size: int
    Trend: Trend
    TargetMean: float   // Target distribution mean (price space)
    TargetSigma: float  // Target distribution sigma (price space)
}

/// Target statistics for sampling from t-digests
type TargetStats = {
    MedianSize: float
    MeanSize: float
    MedianGap: float  // In seconds
    MeanGap: float    // In seconds
}

/// Order flow parameters per trend (computed from target stats)
type OrderFlowParams = {
    BetaMean: float  // Target mean for beta distribution (activity level)
    BetaMeanGap: float  // Beta mean for gap sampling (inverted)
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
    HoldSigmaFraction: float     // Hold sigma as fraction of TargetVolBps (tight side)
    HoldProposalFraction: float  // Hold proposal vol as fraction of baseline ProposalVolBps
    PinnedMassFraction: float    // Mass fraction on the pinned side (e.g. 0.8 = 80% of density)
    HoldDurationMedian: float    // Median hold duration in seconds
    HoldDurationMean: float      // Mean hold duration in seconds
    ReleaseDurationMedian: float // Median release/fakeout duration in seconds
    ReleaseDurationMean: float   // Mean release/fakeout duration in seconds
}

let getHoldParams (duration: HoldDuration) : HoldParams =
    let holdMedian, holdMean, releaseMedian, releaseMean =
        match duration with
        | Short  -> 1.2, 1.8, 0.2, 0.3
        | Medium -> 12.0, 18.0, 1.0, 1.5
        | Long   -> 120.0, 180.0, 10.0, 15.0
    {
        HoldSigmaFraction = 0.05
        HoldProposalFraction = 1.0
        PinnedMassFraction = 0.8
        HoldDurationMedian = holdMedian
        HoldDurationMean = holdMean
        ReleaseDurationMedian = releaseMedian
        ReleaseDurationMean = releaseMean
    }

/// Session-wide baseline parameters
type SessionBaseline = {
    ProposalVolBps: float   // Price proposal random walk σ in bps (scaled by sqrt size)
    BetaScaleProposalBps: float  // Beta scale proposal random walk σ in bps (scaled by sqrt size)
}

let medianSize = 100 // Scales the proposal volatilities.
let defaultBaseline = { ProposalVolBps = 0.35; BetaScaleProposalBps = 200.0 }

let getTargetStats (trend: Trend) : TargetStats =
    match trend with
    | Move (_, Strong) -> { MedianSize = 100.0; MeanSize = 150.0; MedianGap = 0.01; MeanGap = 0.015 }
    | Move (_, Mid)    -> { MedianSize = 80.0; MeanSize = 120.0; MedianGap = 0.02; MeanGap = 0.03 }
    | Move (_, Weak)   -> { MedianSize = 60.0; MeanSize = 90.0; MedianGap = 0.04; MeanGap = 0.06 }
    | Consolidation    -> { MedianSize = 50.0; MeanSize = 75.0; MedianGap = 0.08; MeanGap = 0.12 }
    | Hold (_, Strong, _) -> { MedianSize = 150.0; MeanSize = 250.0; MedianGap = 0.005; MeanGap = 0.008 }
    | Hold (_, Mid, _)    -> { MedianSize = 100.0; MeanSize = 150.0; MedianGap = 0.01; MeanGap = 0.015 }
    | Hold (_, Weak, _)   -> { MedianSize = 80.0; MeanSize = 120.0; MedianGap = 0.02; MeanGap = 0.03 }

let getTargetParams (trend: Trend) : TargetParams =
    match trend with
    | Move (_, Strong) -> { MoveSigmaMedian = 4.0; MoveSigmaMean = 5.0; TargetVolBps = 24.0 }
    | Move (_, Mid)    -> { MoveSigmaMedian = 2.5; MoveSigmaMean = 3.0; TargetVolBps = 18.0 }
    | Move (_, Weak)   -> { MoveSigmaMedian = 1.2; MoveSigmaMean = 1.5; TargetVolBps = 12.0 }
    | Consolidation    -> { MoveSigmaMedian = 0.2; MoveSigmaMean = 0.5; TargetVolBps = 9.0 }
    | Hold _           -> { MoveSigmaMedian = 0.1; MoveSigmaMean = 0.2; TargetVolBps = 9.0 }

let getActivityParams (trend: Trend) : ActivityParams =
    match trend with
    | Move (_, Strong) -> { MedianSize = medianSize; MeanSize = 250.0 }
    | Move (_, Mid)    -> { MedianSize = medianSize; MeanSize = 175.0 }
    | Move (_, Weak)   -> { MedianSize = medianSize; MeanSize = 130.0 }
    | Consolidation    -> { MedianSize = medianSize; MeanSize = 115.0 }
    | Hold (_, Strong, _) -> { MedianSize = medianSize; MeanSize = 400.0 }
    | Hold (_, Mid, _)    -> { MedianSize = medianSize; MeanSize = 250.0 }
    | Hold (_, Weak, _)   -> { MedianSize = medianSize; MeanSize = 175.0 }

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

let private baselineSizeMu, private baselineSizeSigma =
    let p = getActivityParams Consolidation
    logNormalMuSigma p.MedianSize p.MeanSize

let sampleSize (rng: Random) (median: float) (mean: float) : int =
    let alpha = mean / (mean - median)
    let rec loop () =
        let v = LogNormal(baselineSizeMu, baselineSizeSigma, rng).Sample()
        let size = if v <= median then v else Pareto(median, alpha, rng).Sample()
        let rounded = stochasticRound rng size
        if rounded > 0 then rounded else loop ()
    loop ()

/// Sample from LogNormal, and continue to resample if the conditional returns false
let inline sampleLogNormalConditional cond (rng: Random) (median: float) (mean: float) : float =
    let mu, sigma = logNormalMuSigma median mean
    let rec loop () =
        let v = LogNormal(mu, sigma, rng).Sample()
        if cond v then v else loop ()
    loop ()

/// Sample from LogNormal, restricted to below the median
let sampleLogNormalBelow (rng: Random) (median: float) (mean: float) : float =
    sampleLogNormalConditional (fun v -> v <= median) rng median mean

/// Sample from LogNormal, restricted to above the median
let sampleLogNormalAbove (rng: Random) (median: float) (mean: float) : float =
    sampleLogNormalConditional (fun v -> v >= median) rng median mean

/// Sample a duration (seconds) from LogNormal
let sampleDuration (rng: Random) (median: float) (mean: float) : float =
    let mu, sigma = logNormalMuSigma median mean
    LogNormal(mu, sigma, rng).Sample()

/// Sample from beta distribution with given mean and scale
let sampleBeta (rng: Random) (mean: float) (scale: float) : float =
    let a = mean * scale
    let b = (1.0 - mean) * scale
    Beta(a, b, rng).Sample()

/// Query t-digest at percentile (0.0 to 1.0)
let queryPercentile (digest: TDigest.MergingDigest) (percentile: float) : float =
    digest.Quantile(percentile)

/// Sample size from t-digest using beta distribution (high beta = high percentile = large size)
let sampleSizeFromDigest (rng: Random) (digest: TDigest.MergingDigest) (betaMean: float) (betaScale: float) : int =
    let percentile = sampleBeta rng betaMean betaScale
    let size = queryPercentile digest percentile
    stochasticRound rng size

/// Sample gap from t-digest using beta distribution (high beta = low percentile = short gap)
let sampleGapFromDigest (rng: Random) (digest: TDigest.MergingDigest) (betaMean: float) (betaScale: float) : float =
    let percentile = 1.0 - sampleBeta rng betaMean betaScale
    queryPercentile digest percentile

/// Extract centroids from t-digest as (value, weight) pairs
let extractCentroids (digest: MergingDigest) : (float * float) array =
    digest.Centroids()
    |> Seq.map (fun c -> c.Mean(), float c.Count)
    |> Seq.toArray

/// Create a tilted t-digest with a target mean using exponential tilting
let createTiltedDigest (digest: MergingDigest) (targetMean: float) (compression: float) : MergingDigest =
    let centroids = extractCentroids digest
    let lambda, tiltedWeights = tiltWeights centroids targetMean

    // Reconstruct t-digest with tilted weights
    let tiltedDigest = MergingDigest(compression)
    let values = centroids |> Array.map fst
    let totalWeight = Array.sum tiltedWeights

    // Add samples proportional to tilted weights
    for i in 0 .. values.Length - 1 do
        let count = int (tiltedWeights.[i] * totalWeight * 10000.0) // Scale up for precision
        for _ in 1 .. count do
            tiltedDigest.Add(values.[i])

    tiltedDigest

/// Compute expected median and mean for a given beta distribution + t-digest
let computeExpectedStats (digest: TDigest.MergingDigest) (betaMean: float) (betaScale: float) (numSamples: int) : float * float =
    let rng = Random(42)
    let samples = Array.init numSamples (fun _ ->
        let percentile = sampleBeta rng betaMean betaScale
        queryPercentile digest percentile
    )
    let sorted = Array.sort samples
    let median = sorted.[numSamples / 2]
    let mean = Array.average samples
    (median, mean)

/// Find beta mean that produces target median and mean from a t-digest
let findBetaMean (digest: TDigest.MergingDigest) (targetMedian: float) (targetMean: float) (betaScale: float) (numSamples: int) : float =
    let mutable bestBetaMean = 0.5
    let mutable bestError = infinity

    for betaMean in [0.05.. 0.05 .. 0.95] do
        let (median, mean) = computeExpectedStats digest betaMean betaScale numSamples
        let medianError = abs(median - targetMedian) / targetMedian
        let meanError = abs(mean - targetMean) / targetMean
        let error = medianError + meanError
        if error < bestError then
            bestError <- error
            bestBetaMean <- betaMean

    bestBetaMean

/// Compute order flow parameters from target stats and t-digests
let computeOrderFlowParams (digests: TradeDataDigests) (trend: Trend) : OrderFlowParams =
    let targets = getTargetStats trend
    let betaScale = 10.0
    let numSamples = 10000

    let betaMean = findBetaMean digests.SizeDigest targets.MedianSize targets.MeanSize betaScale numSamples
    let betaMeanGap = findBetaMean digests.GapDigest targets.MedianGap targets.MeanGap betaScale numSamples

    { BetaMean = betaMean; BetaMeanGap = betaMeanGap }

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
        | Move (Up, _) -> 1.0
        | Move (Down, _) -> -1.0
        | Consolidation | Hold _ -> if rng.NextDouble() < 0.5 then 1.0 else -1.0
    prevTargetMean + sign * moveSize

/// Generate trades for a single trend episode with beta scale random walk
let generateEpisodeTrades (rng: Random) (startPrice: float) (prevTargetMean: float) (startBetaScale: float) (baseline: SessionBaseline) (digests: TradeDataDigests) (episode: Episode<Trend>) : Trade[] * float * float * float =
    let durationSeconds = episode.Duration * 60.0
    let orderFlowParams = computeOrderFlowParams digests episode.Label
    let targetParams = getTargetParams episode.Label
    let bps = 1e-4

    let targetMean = sampleTargetMean rng prevTargetMean targetParams episode.Label
    let targetSigma = targetParams.TargetVolBps * bps
    let proposalVol = baseline.ProposalVolBps * bps / sqrt (float medianSize)
    let betaScaleProposalVol = baseline.BetaScaleProposalBps * bps / sqrt (float medianSize)
    let betaMean = orderFlowParams.BetaMean
    let logBetaScaleTarget = log 10.0
    let logBetaScaleTargetSigma = 0.5

    let priceTransition =
        match episode.Label with
        | Hold (holdSide, _, holdDuration) ->
            let holdParams = getHoldParams holdDuration
            let mutable holding = true
            let holdLevel = targetMean
            let tightSigma = targetSigma * holdParams.HoldSigmaFraction
            let holdProposalVol = proposalVol * holdParams.HoldProposalFraction
            let logPinned = log holdParams.PinnedMassFraction
            let logUnpinned = log (1.0 - holdParams.PinnedMassFraction)
            let sampleHoldTime () = sampleDuration rng holdParams.HoldDurationMedian holdParams.HoldDurationMean
            let sampleReleaseTime () = sampleDuration rng holdParams.ReleaseDurationMedian holdParams.ReleaseDurationMean
            let mutable timeLeft = sampleLogNormalAbove rng holdParams.HoldDurationMedian holdParams.HoldDurationMean
            fun gap size logBetaScale logPrice ->
                timeLeft <- timeLeft - gap
                if timeLeft <= 0.0 then
                    holding <- not holding
                    timeLeft <- if holding then sampleHoldTime () else sampleReleaseTime ()
                let pVol = if holding then holdProposalVol else proposalVol
                let logDensity =
                    if holding then
                        match holdSide with
                        | Neutral ->
                            fun x -> Normal.PDFLn(holdLevel, tightSigma, x)
                        | Bid ->
                            fun x ->
                                let massLn = if x >= holdLevel then logPinned else logUnpinned
                                Normal.PDFLn(holdLevel, tightSigma, x) + massLn
                        | Ask ->
                            fun x ->
                                let massLn = if x <= holdLevel then logPinned else logUnpinned
                                Normal.PDFLn(holdLevel, tightSigma, x) + massLn
                    else fun x -> Normal.PDFLn(holdLevel, targetSigma, x)
                let sqrtSize = sqrt (float size)
                multiTryStepGeneric rng logPrice (pVol * sqrtSize) logDensity 10
        | Move _ | Consolidation ->
            fun gap size logBetaScale logPrice ->
                let sqrtSize = sqrt (float size)
                multiTryStep rng logPrice (proposalVol * sqrtSize) targetMean targetSigma 10

    let betaScaleTransition _gap size logBetaScale =
        let sqrtSize = sqrt (float size)
        multiTryStep rng logBetaScale (betaScaleProposalVol * sqrtSize) logBetaScaleTarget logBetaScaleTargetSigma 10

    let trades = ResizeArray<Trade>()
    let mutable logPrice = log startPrice
    let mutable logBetaScale = log 10.0  // Constant scale of 10.0
    let mutable time = 0.0

    while time < durationSeconds do
        let betaScale = exp logBetaScale  // Constant scale of 10.0
        let gap = sampleGapFromDigest rng digests.GapDigest orderFlowParams.BetaMeanGap betaScale
        time <- time + gap
        if time < durationSeconds then
            let size = sampleSizeFromDigest rng digests.SizeDigest orderFlowParams.BetaMean betaScale
            // logBetaScale <- betaScaleTransition gap size logBetaScale  // Commented out - using constant scale
            logPrice <- priceTransition gap size logBetaScale logPrice
            trades.Add({
                Time = time
                Price = exp logPrice
                Size = size
                Trend = episode.Label
                TargetMean = exp targetMean
                TargetSigma = LogNormal(targetMean, targetSigma).StdDev
            })

    let endPrice = exp logPrice
    trades.ToArray(), endPrice, targetMean, logBetaScale

/// Print diagnostic statistics for all episode types
let printBetaDigestDiagnostics (digests: TradeDataDigests) : unit =
    let numSamples = 10000
    let betaScale = 10.0

    printfn "\nBeta-reweighted t-digest statistics (scale=%.1f, samples=%d):" betaScale numSamples
    printfn "\nSIZE DISTRIBUTION:"
    printfn "%-20s %10s %10s %10s %10s" "Episode Type" "BetaMean" "Median" "Mean" "Mean/Med"
    printfn "%s" (String.replicate 60 "-")

    let trends = [
        ("Move Up Strong", Move (Up, Strong))
        ("Move Up Mid", Move (Up, Mid))
        ("Move Up Weak", Move (Up, Weak))
        ("Consolidation", Consolidation)
        ("Hold Bid Strong", Hold (Bid, Strong, Short))
        ("Hold Bid Mid", Hold (Bid, Mid, Short))
        ("Hold Bid Weak", Hold (Bid, Weak, Short))
    ]

    for (name, trend) in trends do
        let orderFlowParams = computeOrderFlowParams digests trend
        let (median, mean) = computeExpectedStats digests.SizeDigest orderFlowParams.BetaMean betaScale numSamples
        let ratio = mean / median
        printfn "%-20s %10.2f %10.1f %10.1f %10.2f" name orderFlowParams.BetaMean median mean ratio

    printfn "\nGAP DISTRIBUTION (seconds):"
    printfn "%-20s %10s %10s %10s %10s" "Episode Type" "BetaMean" "Median" "Mean" "Mean/Med"
    printfn "%s" (String.replicate 60 "-")

    for (name, trend) in trends do
        let orderFlowParams = computeOrderFlowParams digests trend
        let (median, mean) = computeExpectedStats digests.GapDigest orderFlowParams.BetaMeanGap betaScale numSamples
        let ratio = mean / median
        printfn "%-20s %10.2f %10.6f %10.6f %10.2f" name orderFlowParams.BetaMeanGap median mean ratio

/// Test exponential tilting on t-digest
let printExponentialTiltDiagnostics (digests: TradeDataDigests) : unit =
    printfn "\nExponential Tilting Test:"

    let centroids = extractCentroids digests.SizeDigest
    let totalWeight = centroids |> Array.sumBy snd
    let originalMean = centroids |> Array.map (fun (v, w) -> v * w) |> Array.sum |> fun s -> s / totalWeight

    printfn "Original size mean: %.2f" originalMean
    printfn "\nTesting tilting to different target means:"
    printfn "%-15s %10s %10s %10s" "Target" "Lambda" "Actual" "Error"
    printfn "%s" (String.replicate 45 "-")

    for targetMean in [50.0; 100.0; 150.0; 200.0] do
        let lambda, tiltedWeights = tiltWeights centroids targetMean
        let values = centroids |> Array.map fst
        let actualMean = Array.map2 (fun v w -> v * w) values tiltedWeights |> Array.sum
        printfn "%-15.2f %10.6f %10.2f %10.6f" targetMean lambda actualMean (actualMean - targetMean)

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
