module TradingEdge.Simulation.Patterns

open System
open MathNet.Numerics.Distributions
open TradeGeneration
open SessionDuration

// =============================================================================
// Volume Unit Conversion
// =============================================================================

let volumeUnitToTotal (baseParams: BaseParams) (volumeUnit: float) : float =
    volumeUnit * baseParams.BaseVolume * baseParams.BaseRate

/// Sample volume per move from log normal distribution
/// Median is 85% of mean for slight right skew
let volumePerMove (baseParams: BaseParams) (ctx : GenerationContext<'r>) (volumeUnitsPerMove: float) : float =
    let mean = volumeUnitToTotal baseParams volumeUnitsPerMove
    let mu, sigma = logNormalMuSigma (mean * 0.85) mean
    LogNormal.Sample(ctx.Effects.Rng, mu, sigma)

/// Calculate volatility sigma scaled by square root of volume
let sigmaPerVolume (baseParams: BaseParams) (volume : float) =
    baseParams.BaseVolatility * sqrt volume

// =============================================================================
// Patterns
// =============================================================================

// Many patterns here are using `volumeUnitsPerMove` rather than total volume. Volume units act as a pseudo duration. Assuming that volume abnormality is 1,
// each volume unit will be 1s on average. 

/// Downtrend day pattern: Morning and Close sessions drift down with occasional holds,
/// Mid session maintains flat drift. Uses abnormal volume (3x) for trending moves.
let downtrendDay (baseParams: BaseParams) (volumeUnitsPerMove : float) : Pattern<'r> =
    fun ctx cont ->
        // Wide target sigma allows significant price movement
        let targetSigma = 30. * baseParams.BaseVolatility * sqrt (baseParams.BaseVolume * baseParams.BaseRate)

        // Flat drift: normal volume, no directional bias
        let driftFlat : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 1.
                let volumePerMove = volumePerMove baseParams ctx (volumeAbnormality * volumeUnitsPerMove)
                let moveSigma = volumeAbnormality * sigmaPerVolume baseParams volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)
                generateDrift baseParams ["DriftFlat"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Downward drift: 3x abnormal volume, target biased -1 sigma below current
        let driftDown : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 3.
                let volumePerMove = volumePerMove baseParams ctx (volumeAbnormality * volumeUnitsPerMove)
                let moveSigma = volumeAbnormality * sigmaPerVolume baseParams volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, -1. * moveSigma, moveSigma)
                generateDrift baseParams ["DriftDown"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Hold pattern: 3x abnormal volume, alternates between loose and tight consolidation
        // Target nudged slightly with small random walk
        let hold : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 7.5
                let volumePerMove = volumePerMove baseParams ctx (volumeAbnormality * volumeUnitsPerMove)
                let moveSigma = 0.5 * sigmaPerVolume baseParams volumePerMove
                let ctx = {ctx with StartTarget = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)}
                generateHold baseParams ["Hold"; "DowntrendDay"] volumeAbnormality (targetSigma, volumePerMove * 0.9) (targetSigma * 0.1, volumePerMove * 0.1) volumePerMove true ctx cont

        // Session-specific patterns
        let morning : Pattern<'r> =
            choice [
                driftDown, 0.9
                hold, 0.1
            ]
        let mid : Pattern<'r> = driftFlat
        let close : Pattern<'r> = morning

        // Apply repeat to each session pattern and sequence them
        sequence [repeat morning; repeat mid; repeat close] ctx cont