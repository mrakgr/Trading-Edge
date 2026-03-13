module TradingEdge.Simulation.TradeGeneration

open System
open MathNet.Numerics.Distributions
open SessionDuration

// =============================================================================
// Types
// =============================================================================

type GenerationEffect<'r> =
    abstract member Rng: Random
    abstract member Session: SessionLevel.Session
    abstract member SessionEndTime: float
    abstract member AddTrade : Trade -> unit
    abstract member OnTimeChanged : (unit -> 'r) -> 'r
    abstract member OnDone : GenerationContext<'r> -> 'r

/// Common parameters for pattern generation
and GenerationContext<'r> = {
    StartPrice: float
    StartTime: float
    StartTarget: float
    Effects : GenerationEffect<'r>
}

type BaseParams = {
    BaseVolume: float
    BaseRate: float
    BaseVolatility: float
}


type PatternContinuation<'r> = GenerationContext<'r> -> 'r
type Pattern<'r> = GenerationContext<'r> -> PatternContinuation<'r> -> 'r

type SimulationParams = {
    TotalDuration: float
    StartPrice: float
    StartTarget: float
    BaseVolume: float
    BaseRate: float
    BaseVolatility: float
}

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
// Pattern Combinators
// =============================================================================

let sequence (patterns: Pattern<'r> list) : Pattern<'r> =
    List.foldBack
        (fun p acc -> fun genCtx cont -> p genCtx (fun genCtx' -> acc genCtx' cont))
        patterns
        (fun genCtx cont -> cont genCtx)

let sequenceAtomic (patterns: Pattern<'r> list) : Pattern<'r> =
    fun ctx cont ->
        let initialSession = ctx.Effects.Session
        List.foldBack
            (fun p acc -> fun genCtx cont' ->
                p genCtx (fun genCtx' ->
                    if genCtx'.Effects.Session <> initialSession then
                        cont' genCtx'
                    else
                        acc genCtx' cont'))
            patterns
            (fun genCtx cont' -> cont' genCtx)
            ctx cont

let choice (weightedPatterns: (Pattern<'r> * float) list) : Pattern<'r> =
    fun genCtx cont ->
        let patterns, weights = List.unzip weightedPatterns
        let categorical = Categorical(Array.ofList weights, genCtx.Effects.Rng)
        let idx = categorical.Sample()
        patterns.[idx] genCtx cont

let repeat (pattern: Pattern<'r>) : Pattern<'r> =
    fun ctx cont ->
        let initialSession = ctx.Effects.Session
        let rec loop genCtx =
            pattern genCtx (fun genCtx' ->
                if genCtx'.Effects.Session <> initialSession then
                    cont genCtx'
                else
                    loop genCtx')
        loop ctx

// =============================================================================
// Default Effect Handler
// =============================================================================

let makeDefaultEffect (rng: Random) (totalDuration: float) : GenerationEffect<Trade[]> =
    let sessions =
        MCMC.run MCMC.defaultConfig SessionLevel.episodes totalDuration rng
        |> Array.map (fun s -> { s with Duration = s.Duration * 60.0 })  // Convert minutes to seconds
    let sessionEndTimes =
        sessions |> Array.scan (fun acc s -> acc + s.Duration) 0.0 |> Array.tail
    let trades = ResizeArray<Trade>()
    let mutable currentSessionIdx = 0

    { new GenerationEffect<Trade[]> with
        member _.Rng = rng
        member _.Session = sessions.[currentSessionIdx].Episode.Label
        member _.SessionEndTime = sessionEndTimes.[currentSessionIdx]
        member _.AddTrade(trade) = trades.Add(trade)
        member _.OnTimeChanged(cont) =
            currentSessionIdx <- currentSessionIdx + 1
            if currentSessionIdx < sessions.Length then
                cont ()
            else
                trades.ToArray()
        member _.OnDone(_) = trades.ToArray()
    }

let makeDefaultContext
    (rng: Random)
    (simParams: SimulationParams)
    : GenerationContext<Trade[]> =

    let effect = makeDefaultEffect rng simParams.TotalDuration

    {
        StartPrice = simParams.StartPrice
        StartTime = 0.0
        StartTarget = simParams.StartTarget
        Effects = effect
    }


// =============================================================================
// Trade Generation
// =============================================================================

let generateDrift (baseParams: BaseParams) (labels: string list) (volumeAbnormality : float) (endTarget: float) (targetSigma: float) (volumeLimit: float) (respectSessionBoundaries: bool) : Pattern<'r> =
    fun ctx cont ->
        let proposalVol = baseParams.BaseVolatility
        let sqrtVolumeAbnormality = sqrt volumeAbnormality
        let volumeMean = baseParams.BaseVolume * sqrtVolumeAbnormality
        let volumeMedian = volumeMean / 2.0
        let gapMean = 1.0 / (baseParams.BaseRate * sqrtVolumeAbnormality)
        let gapMedian = gapMean / 2.0

        let rec loop price time volumeConsumed =
            if volumeConsumed >= volumeLimit then
                cont { ctx with StartPrice = price; StartTime = time; StartTarget = endTarget }
            else
                let progress = volumeConsumed / volumeLimit
                let currentTarget = ctx.StartTarget + (endTarget - ctx.StartTarget) * progress
                let rng = ctx.Effects.Rng
                let gap = sampleGap rng gapMedian gapMean
                let newTime = time + gap
                let endTime = ctx.Effects.SessionEndTime

                if newTime >= endTime then
                    ctx.Effects.OnTimeChanged (fun () ->
                        if respectSessionBoundaries then
                            cont { ctx with StartPrice = price; StartTime = endTime; StartTarget = currentTarget }
                        else
                            loop price time volumeConsumed)
                else
                    let size = sampleSize rng volumeMedian volumeMean
                    let sizeFloat = float size
                    let sqrtSize = sqrt sizeFloat
                    let newPrice = multiTryStep rng price (proposalVol * sqrtSize) currentTarget targetSigma 10
                    ctx.Effects.AddTrade({
                        Time = newTime
                        Price = newPrice
                        Size = size
                        TargetMean = currentTarget
                        TargetSigma = targetSigma
                        Label = labels
                    })
                    loop newPrice newTime (volumeConsumed + sizeFloat)

        loop ctx.StartPrice ctx.StartTime 0.0

let generateBreakout (baseParams: BaseParams) (labels: string list) (volumeAbnormality : float) (targetSigma: float) (volumeLimit: float) (respectSessionBoundaries: bool) : Pattern<'r> =
    fun ctx cont ->
        generateDrift baseParams labels volumeAbnormality ctx.StartTarget targetSigma volumeLimit respectSessionBoundaries ctx cont

let generateHold (baseParams: BaseParams) (labels: string list) (volumeAbnormality : float) (looseSigma : float, looseVolume : float) (tightSigma : float, tightVolume : float) (volumeLimit: float) (respectSessionBoundaries: bool) : Pattern<'r> =
    fun ctx cont ->
        let rng = ctx.Effects.Rng
        let rec buildPatterns volumeRemaining acc =
            if volumeRemaining <= 0.0 then
                List.rev acc
            else
                let useTight = rng.NextDouble() < 0.5
                let (sigma, chunkVolume) = if useTight then (tightSigma, tightVolume) else (looseSigma, looseVolume)
                let actualVolume = min chunkVolume volumeRemaining
                let pattern = generateBreakout baseParams labels volumeAbnormality sigma actualVolume respectSessionBoundaries
                buildPatterns (volumeRemaining - actualVolume) (pattern :: acc)

        let patterns = buildPatterns volumeLimit []
        let sequencer = if respectSessionBoundaries then sequenceAtomic else sequence
        sequencer patterns ctx cont
