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
let volumePerMove (baseParams: BaseParams) (ctx : GenerationContext<'r>) (volumeUnitsPerMove: float) : float =
    let mean = volumeUnitToTotal baseParams volumeUnitsPerMove
    sampleLogNormal ctx.Effects.Rng (mean * 0.7) mean

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
        let targetSigma = 10. * baseParams.BaseVolatility * sqrt (baseParams.BaseVolume * baseParams.BaseRate)

        // Flat drift: normal volume, no directional bias
        let driftFlat : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 1.
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * baseParams.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)
                generateDrift baseParams ["DriftFlat"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Downward drift: 3x abnormal volume, target biased -1 sigma below current
        let driftDown : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 3.
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * baseParams.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, -0.3 * moveSigma, moveSigma)
                generateDrift baseParams ["DriftDown"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Hold pattern: 3x abnormal volume, alternates between loose and tight consolidation
        // Target nudged slightly with small random walk
        let hold : Pattern<'r> =
            let hold ctx cont =
                let volumeAbnormality = 7.5
                let volumePerMove = volumePerMove baseParams ctx (2. * volumeUnitsPerMove)
                let moveSigma = 0.5 * baseParams.BaseVolatility * sqrt volumePerMove
                let ctx = {ctx with StartTarget = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)}
                generateHold baseParams ["Hold"; "DowntrendDay"] volumeAbnormality (targetSigma * 0.1, volumePerMove * 0.8 * 0.3) (targetSigma, volumePerMove * 0.2 * 0.3) volumePerMove false ctx cont
            let release ctx cont =
                let volumeAbnormality = 4.
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * baseParams.BaseVolatility * sqrt volumePerMove
                let z = Normal.Sample(ctx.Effects.Rng, -1. * moveSigma, moveSigma)
                let target = ctx.StartTarget + z
                let ctx = {ctx with StartTarget = ctx.StartTarget + z * 0.33}
                generateDrift baseParams ["HoldRelease"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove false ctx cont
            sequence [hold; release]

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