module TradingEdge.Simulation.OrderFlowGeneration

open System
open MathNet.Numerics.Distributions
open EpisodeMCMC

// /// Common parameters for recursive generation
// type GenerationContext = {
//     ParentVariance: float
//     StartPrice: float
//     ParentTarget: float
//     ParentVolume: float
//     ParentRate: float
//     ParentDuration: float
// }
//
// /// Auction target parameters
// type AuctionParams = {
//     BaseVolBps: float
//     MeanVolume: float
//     MeanRate: float
//     SessionMultiplier: float  // sqrt(3) for Morning/Close, 1.0 for Mid
// }
//
// /// Session-wide baseline parameters
// type SessionBaseline = {
//     ProposalVolBps: float   // Price proposal random walk σ in bps (scaled by sqrt size)
// }
//
// let defaultBaseline = { ProposalVolBps = 0.35 }
//
// /// Multi-try step with pluggable log-density function
// let multiTryStepGeneric (rng: Random) (price: float) (proposalVol: float) (density: float -> float) (n: int) : float =
//     let candidates = Array.zeroCreate (2 * n + 1)
//     let logWeights = Array.zeroCreate (2 * n + 1)
//     candidates.[0] <- price
//     logWeights.[0] <- density price
//     for i in 0 .. n - 1 do
//         let z = Normal.Sample(rng, 0.0, proposalVol)
//         let yPos = price + z
//         let yNeg = price - z
//         candidates.[2 * i + 1] <- yPos
//         candidates.[2 * i + 2] <- yNeg
//         logWeights.[2 * i + 1] <- density yPos
//         logWeights.[2 * i + 2] <- density yNeg
//     let maxW = Array.max logWeights
//     let weights = logWeights |> Array.map (fun w -> exp(w - maxW))
//     let idx = Categorical.Sample(rng, weights)
//     candidates.[idx]
//
// /// Multi-try Metropolis step: propose n moves + n negated + current, select by target likelihood
// let multiTryStep (rng: Random) (price: float) (proposalVol: float) (targetMean: float) (targetSigma: float) (n: int) : float =
//     multiTryStepGeneric rng price proposalVol (fun x -> Normal.PDFLn(targetMean, targetSigma, x)) n
//
// let stochasticRound (rng: Random) (x: float) : int =
//     let floor = Math.Floor(x)
//     let frac = x - floor
//     int (if rng.NextDouble() < frac then floor + 1.0 else floor)
//
// let logNormalSigma (median: float) (mean: float) : float =
//     sqrt(2.0 * log(mean / median))
//
// let logNormalMuSigma (median: float) (mean: float) : float * float =
//     let mu = log median
//     let sigma = logNormalSigma median mean
//     (mu, sigma)
//
// let sampleSize (rng: Random) (median: float) (mean: float) : int =
//     let mu, sigma = logNormalMuSigma median mean
//     let rec loop () =
//         let size = LogNormal(mu, sigma, rng).Sample()
//         let rounded = stochasticRound rng size
//         if rounded > 0 then rounded else loop ()
//     loop ()
//
// let sampleGap (rng: Random) (median: float) (mean: float) : float =
//     let mu, sigma = logNormalMuSigma median mean
//     LogNormal(mu, sigma, rng).Sample()
//
// /// Generate trades for a leaf episode (no child episodes)
// let generateTrades
//     (rng: Random)
//     (baseVolBps: float)
//     (ctx: GenerationContext)
//     (startTime: float)
//     (parentLabel: string)
//     : Trade[] * float =
//
//     let bps = 1e-4
//     let proposalVol = baseVolBps * bps
//     let targetSigma = sqrt ctx.ParentVariance
//
//     let trades = ResizeArray<Trade>()
//     let mutable price = ctx.StartPrice
//     let mutable time = startTime
//
//     let volumeMedian = ctx.ParentVolume / 2.0
//     let gapMean = 1.0 / ctx.ParentRate
//     let gapMedian = gapMean / 2.0
//
//     while time < startTime + ctx.ParentDuration do
//         let gap = sampleGap rng gapMedian gapMean
//         time <- time + gap
//         if time < startTime + ctx.ParentDuration then
//             let size = sampleSize rng volumeMedian ctx.ParentVolume
//             let sqrtSize = sqrt (float size)
//             price <- multiTryStep rng price (proposalVol * sqrtSize) ctx.ParentTarget targetSigma 10
//             trades.Add({
//                 Time = time
//                 Price = price
//                 Size = size
//                 TargetMeanAndVariances = [(ctx.ParentTarget, ctx.ParentVariance)]
//                 Label = [parentLabel]
//             })
//
//     let endPrice = price
//     trades.ToArray(), endPrice

// /// Sample target mean for an episode
// /// Generate trades for a single subepisode
// let generateSubepisodeTrades (rng: Random) (auction_args: AuctionParams) (gen_args: GenerateParams) : GenerateOutput =
//     let bps = 1e-4
//     let proposalVol = auction_args.BaseVolBps * bps

//     let trades = ResizeArray<Trade>()
//     let mutable logPrice = log gen_args.StartPrice
//     let mutable time = 0.0

//     while time < gen_args.Duration do
//         let gap = Exponential.Sample(rng, 1.0 / auction_args.MeanRate)
//         time <- time + gap
//         if time < gen_args.Duration then
//             let size = sampleSize rng (auction_args.MeanVolume / 2.0) auction_args.MeanVolume
//             let sqrtSize = sqrt (float size)
//             logPrice <- multiTryStep rng logPrice (proposalVol * sqrtSize) gen_args.TargetMean gen_args.TargetVariance 10
//             trades.Add({
//                 Time = time
//                 Price = exp logPrice
//                 Size = size
//                 TargetMean = exp targetMean
//                 TargetSigma = LogNormal(targetMean, targetSigma).StdDev
//             })

//     let endPrice = exp logPrice
//     trades.ToArray(), endPrice


// let calculateVariance (auctionParams: AuctionParams) (durationSeconds: float) : float =
//     let bps = 1e-4
//     let baseVol = auctionParams.BaseVolBps * bps
//     let multiplierSq = auctionParams.SessionMultiplier * auctionParams.SessionMultiplier
//     baseVol * baseVol * auctionParams.MeanVolume * auctionParams.MeanRate * multiplierSq * durationSeconds

// let sampleAuctionTarget (rng: Random) (variance: float) : float =
//     Normal.Sample(rng, 0.0, sqrt variance)



// /// Sample from LogNormal, and continue to resample if the conditional returns false
// let inline sampleLogNormalConditional cond (rng: Random) (median: float) (mean: float) : float =
//     let mu, sigma = logNormalMuSigma median mean
//     let rec loop () =
//         let v = LogNormal(mu, sigma, rng).Sample()
//         if cond v then v else loop ()
//     loop ()

// /// Sample from LogNormal, restricted to below the median
// let sampleLogNormalBelow (rng: Random) (median: float) (mean: float) : float =
//     sampleLogNormalConditional (fun v -> v <= median) rng median mean

// /// Sample from LogNormal, restricted to above the median
// let sampleLogNormalAbove (rng: Random) (median: float) (mean: float) : float =
//     sampleLogNormalConditional (fun v -> v >= median) rng median mean

// /// Sample a duration (seconds) from LogNormal
// let sampleDuration (rng: Random) (median: float) (mean: float) : float =
//     let mu, sigma = logNormalMuSigma median mean
//     LogNormal(mu, sigma, rng).Sample()

// /// Query t-digest at percentile (0.0 to 1.0)
// let queryPercentile (digest: TDigest.MergingDigest) (percentile: float) : float =
//     digest.Quantile(percentile)

// /// Sample size from t-digest uniformly
// let sampleSizeFromDigest (rng: Random) (digest: TDigest.MergingDigest) : int =
//     let percentile = rng.NextDouble()
//     let size = queryPercentile digest percentile
//     stochasticRound rng size

// /// Sample gap from t-digest uniformly
// let sampleGapFromDigest (rng: Random) (digest: TDigest.MergingDigest) : float =
//     let percentile = rng.NextDouble()
//     queryPercentile digest percentile

// /// Extract centroids from t-digest as (value, weight) pairs
// let extractCentroids (digest: MergingDigest) : (float * float) array =
//     digest.Centroids()
//     |> Seq.map (fun c -> c.Mean(), float c.Count)
//     |> Seq.toArray

// /// Calculate mean from t-digest
// let digestMean (digest: MergingDigest) : float =
//     let centroids = extractCentroids digest
//     let totalWeight = centroids |> Array.sumBy snd
//     centroids |> Array.map (fun (v, w) -> v * w) |> Array.sum |> fun s -> s / totalWeight

// /// Create a tilted t-digest with a target mean using exponential tilting
// let createTiltedDigest (rng: Random) (digest: MergingDigest) (targetMean: float) : MergingDigest =
//     let centroids = extractCentroids digest
//     let _lambda, tiltedWeights = tiltWeights centroids targetMean

//     // Reconstruct t-digest with tilted weights
//     let tiltedDigest = MergingDigest digest.Compression
//     let values = centroids |> Array.map fst
//     let totalWeight = float Int32.MaxValue

//     // Add samples proportional to tilted weights
//     for i in 0 .. values.Length - 1 do
//         let x = tiltedWeights.[i] * totalWeight
//         tiltedDigest.Add(values.[i], stochasticRound rng x)

//     tiltedDigest

// /// Create tilted digests for a session using multipliers
// let createSessionTiltedDigests (rng: Random) (digests: TradeDataDigests) (multiplier: float) : TradeDataDigests =
//     let baselineSizeMean = digestMean digests.SizeDigest
//     let baselineGapMean = digestMean digests.GapDigest
//     {
//         SizeDigest = createTiltedDigest rng digests.SizeDigest (baselineSizeMean * multiplier)
//         GapDigest = createTiltedDigest rng digests.GapDigest (baselineGapMean / multiplier)
//     }


// /// Print diagnostic statistics for exponential tilting
// let printBetaDigestDiagnostics (digests: TradeDataDigests) : unit =
//     printfn "\nExponential tilting approach - beta diagnostics removed"

// /// Test exponential tilting on t-digest
// let printExponentialTiltDiagnostics (digests: TradeDataDigests) : unit =
//     printfn "\nExponential Tilting Test:"

//     let centroids = extractCentroids digests.SizeDigest
//     let totalWeight = centroids |> Array.sumBy snd
//     let originalMean = centroids |> Array.map (fun (v, w) -> v * w) |> Array.sum |> fun s -> s / totalWeight

//     printfn "Original size mean: %.2f" originalMean
//     printfn "\nTesting tilting to different target means:"
//     printfn "%-15s %10s %10s %10s" "Target" "Lambda" "Actual" "Error"
//     printfn "%s" (String.replicate 45 "-")

//     for targetMean in [50.0; 100.0; 150.0; 200.0] do
//         let lambda, tiltedWeights = tiltWeights centroids targetMean
//         let values = centroids |> Array.map fst
//         let actualMean = Array.map2 (fun v w -> v * w) values tiltedWeights |> Array.sum
//         printfn "%-15.2f %10.6f %10.2f %10.6f" targetMean lambda actualMean (actualMean - targetMean)

// /// Print summary statistics for generated trades
// let printTradesSummary (trades: Trade[]) : unit =
//     if trades.Length = 0 then
//         printfn "No trades generated"
//     else
//         let duration = trades.[trades.Length - 1].Time
//         let avgRate = float trades.Length / duration
//         let totalVolume = trades |> Array.sumBy (fun t -> t.Size)
//         let avgSize = float totalVolume / float trades.Length
//         let maxSize = trades |> Array.map (fun t -> t.Size) |> Array.max
//         let startPrice = trades.[0].Price
//         let endPrice = trades.[trades.Length - 1].Price
//         let returnPct = (endPrice - startPrice) / startPrice * 100.0
//         printfn "  Trades: %d, Duration: %.1fs, Rate: %.2f/s" trades.Length duration avgRate
//         printfn "  Volume: %d, Avg: %.1f, Max: %d" totalVolume avgSize maxSize
//         printfn "  Price: %.4f -> %.4f (%.2f%%)" startPrice endPrice returnPct
