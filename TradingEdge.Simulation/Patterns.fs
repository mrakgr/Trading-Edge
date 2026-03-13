module TradingEdge.Simulation.Patterns

open System
open MathNet.Numerics.Distributions
open TradeGeneration
open SessionDuration

// =============================================================================
// Volume Unit Conversion
// =============================================================================

let volumeUnitToTotal (ctx: GenerationContext<'r>) (volumeUnit: float) : float =
    volumeUnit * ctx.BaseVolume * ctx.BaseRate

/// Sample volume per move from log normal distribution
/// Median is 85% of mean for slight right skew
let volumePerMove (ctx: GenerationContext<'r>) (volumeUnitsPerMove: float) : float =
    let mean = volumeUnitToTotal ctx volumeUnitsPerMove
    let mu, sigma = logNormalMuSigma (mean * 0.85) mean
    LogNormal.Sample(mu, sigma)

// =============================================================================
// Patterns
// =============================================================================

/// Downtrend day pattern: Morning and Close sessions drift down with occasional holds,
/// Mid session maintains flat drift. Uses abnormal volume (3x) for trending moves.
let downtrendDay (volumeUnitsPerMove : float) : Pattern<'r> =
    fun ctx cont ->
        // Wide target sigma allows significant price movement
        let targetSigma = ctx.BaseVolatility * 10.

        // Flat drift: normal volume, no directional bias
        let driftFlat : Pattern<'r> =
            fun ctx cont ->
                let volumePerMove = volumePerMove ctx volumeUnitsPerMove
                let volumeAbnormality = 1.
                let moveSigma = volumeAbnormality * ctx.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(0.0, moveSigma)
                generateDrift volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Downward drift: 3x abnormal volume, target biased -1 sigma below current
        let driftDown : Pattern<'r> =
            fun ctx cont ->
                let volumePerMove = volumePerMove ctx volumeUnitsPerMove
                let volumeAbnormality = 3.
                let moveSigma = volumeAbnormality * ctx.BaseVolatility * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(-1. * moveSigma, moveSigma)
                generateDrift volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Hold pattern: 3x abnormal volume, alternates between loose and tight consolidation
        // Target nudged slightly with small random walk
        let hold : Pattern<'r> =
            fun ctx cont ->
                let volumePerMove = volumePerMove ctx volumeUnitsPerMove
                let volumeAbnormality = 3.
                let moveSigma = 0.5 * ctx.BaseVolatility * sqrt volumePerMove
                let ctx = {ctx with StartTarget = ctx.StartTarget + Normal.Sample(0.0, moveSigma)}
                generateHold volumeAbnormality (targetSigma, volumePerMove * 0.9) (targetSigma * 0.1, volumePerMove * 0.1) volumePerMove true ctx cont

        // Session-specific patterns
        let morning : Pattern<'r> =
            choice [
                driftDown, 0.8
                hold, 0.2
            ]
        let mid : Pattern<'r> = driftFlat
        let close : Pattern<'r> = morning

        // Select pattern based on current session and repeat until session changes
        let sessionPattern =
            match ctx.Effects.Session with
            | SessionLevel.Morning ->  morning
            | SessionLevel.Mid -> mid
            | SessionLevel.Close -> close
        repeat sessionPattern ctx cont