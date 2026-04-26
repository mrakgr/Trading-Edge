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
    sampleLogNormal ctx.Effects.Rng (mean * 0.85) mean

// =============================================================================
// Patterns
// =============================================================================

// Many patterns here are using `volumeUnitsPerMove` rather than total volume. Volume units act as a pseudo duration. Assuming that volume abnormality is 1,
// each volume unit will be 1s on average. 

/// Downtrend day pattern: Morning and Close sessions drift down with occasional holds,
/// Mid session maintains flat drift. Uses abnormal volume (3x) for trending moves.
/// Uses wide long drifting moves.
let downtrendDay (baseParams: BaseParams) : Pattern<'r> =
    fun ctx cont ->
        let volumeUnitsPerMove = 50.0 * 60.0  // Multiply by 60 since rate is in trades per second

        // Wide target sigma allows significant price movement
        let targetSigma = 50. * baseParams.BaseVolatility * sqrt (baseParams.BaseVolume * baseParams.BaseRate)
        
        // Flat drift: normal volume, no directional bias
        let driftFlat : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 1.
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * baseParams.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)
                generateDrift baseParams ["DriftFlat"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove true ctx cont // (fun ctx -> cont {ctx with StartTarget = ctx.StartPrice})

        // Downward drift: 3x abnormal volume, target biased -0.3 sigma below current
        let driftDown : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 3.
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * baseParams.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, -0.3 * moveSigma, moveSigma)
                generateDrift baseParams ["DriftDown"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove true ctx cont // (fun ctx -> cont {ctx with StartTarget = ctx.StartPrice})

        // Hold pattern: 7.5x abnormal volume, alternates between loose and tight consolidation, followed by a release that has 4x abnormal volume and -1.5 sigma target below current.
        let hold : Pattern<'r> =
            let hold ctx cont =
                let volumeAbnormality = 7.5
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = 0.5 * baseParams.BaseVolatility * sqrt volumePerMove
                let ctx = {ctx with StartTarget = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)}
                let looseSigma = 10. * baseParams.BaseVolatility * sqrt (baseParams.BaseVolume * baseParams.BaseRate)
                let tightSigma = 1. * baseParams.BaseVolatility * sqrt (baseParams.BaseVolume * baseParams.BaseRate)
                generateHold baseParams ["Hold"; "DowntrendDay"] volumeAbnormality (tightSigma, volumePerMove * 0.8 * 0.3) (looseSigma, volumePerMove * 0.2 * 0.3) volumePerMove false ctx cont
                
            let release ctx cont =
                let volumeAbnormality = 4.
                let volumePerMove = volumePerMove baseParams ctx (1.5 * volumeUnitsPerMove)
                let moveSigma = volumeAbnormality * baseParams.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, -1.5 * moveSigma, moveSigma)
                let ctx = {ctx with StartTarget = ctx.StartTarget - 0.7 * targetSigma}
                generateDrift baseParams ["HoldRelease"; "DowntrendDay"] volumeAbnormality target targetSigma volumePerMove false ctx cont
            sequence [retarget; hold; release]

        // Session-specific patterns
        let morning : Pattern<'r> =
            choice [
                driftDown, 0.9
                hold, 0.1
            ]
        let mid : Pattern<'r> = driftFlat
        let close : Pattern<'r> = morning

        // Apply repeat to each session pattern and sequence them
        sequence [repeat morning; retarget; repeat mid; retarget; repeat close] ctx cont

/// Neutral day pattern: No directional bias, normal volume throughout.
/// Models a typical day with random walk behavior and occasional small consolidations.
let neutralDay (baseParams: BaseParams) : Pattern<'r> =
    fun ctx cont ->
        let volumeUnitsPerMove = 30.0 * 60.0  // Shorter moves than downtrend

        // Moderate target sigma for typical price movement
        let targetSigma = 20. * baseParams.BaseVolatility * sqrt (baseParams.BaseVolume * baseParams.BaseRate)

        // Random walk drift: normal volume, no directional bias
        let driftNeutral : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = 1.
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * baseParams.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)
                generateDrift baseParams ["DriftNeutral"; "NeutralDay"] volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Small consolidation: 2x volume, tight range
        let smallHold : Pattern<'r> =
            let hold ctx cont =
                let volumeAbnormality = 2.
                let volumePerMove = volumePerMove baseParams ctx (0.5 * volumeUnitsPerMove)
                let tightSigma = 2. * baseParams.BaseVolatility * sqrt (baseParams.BaseVolume * baseParams.BaseRate)
                generateHold baseParams ["SmallHold"; "NeutralDay"] volumeAbnormality (tightSigma, volumePerMove * 0.9) (tightSigma * 2., volumePerMove * 0.1) volumePerMove false ctx cont
            sequence [retarget; hold]

        // Session pattern: mostly neutral drift with occasional small holds
        let session : Pattern<'r> =
            choice [
                driftNeutral, 0.85
                smallHold, 0.15
            ]

        repeat session ctx cont