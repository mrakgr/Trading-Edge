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

type TrendDayType =
    | UptrendDay
    | DowntrendDay

let sampleVolumeAbnormality (ctx : _ GenerationContext) n =
    sampleLogNormal ctx.Effects.Rng (n * 0.9) n

/// Trend day pattern (up or down): Morning and Close sessions drift in the
/// chosen direction with occasional holds; Mid session maintains flat drift.
/// Holds in the morning/close can flip the day's trend direction with 10%
/// probability, modeling a regime change at consolidation. If
/// `initialTrendDayType` is None, the direction is chosen 50/50 at start.
let trendDay (initialTrendDayType : TrendDayType option) (baseParams: BaseParams) : Pattern<'r> =
    fun ctx cont ->
        let trendDayType =
            initialTrendDayType
            |> Option.defaultWith (fun () ->
                if ctx.Effects.Rng.NextDouble() <= 0.5 then UptrendDay else DowntrendDay)
            |> ref
        // Re-read on every call so that flips by `flippingHold` are reflected
        // in subsequent labels — do not hoist into a `let baseLabel = ...`.
        let baseLabel() = [string trendDayType.Value]

        let volumeUnitsPerMove = 30.0 * 60.0  // Multiply by 60 since rate is in trades per second

        // Target-side volatility is scaled down vs BaseVolatility so the per-trade
        // proposal vol (which still uses BaseVolatility downstream in generateDrift)
        // dominates targets by ~43%. Tightens target movement and target spread
        // without touching the trade-level noise.
        let targetVol = 0.7 * baseParams.BaseVolatility

        // Wide target sigma allows significant price movement
        let targetSigma = 50. * targetVol * sqrt (baseParams.BaseVolume * baseParams.BaseRate)

        // Flat drift: normal volume, no directional bias
        let driftFlat : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = sampleVolumeAbnormality ctx 1.0
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * targetVol * sqrt volumePerMove
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, 0.0, moveSigma)
                generateDrift baseParams ("DriftFlat" :: baseLabel()) volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Trending drift: 3x abnormal volume, target biased ±0.3 sigma in the
        // direction of the current day type.
        let driftTrending : Pattern<'r> =
            fun ctx cont ->
                let volumeAbnormality = sampleVolumeAbnormality ctx 3.0
                let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
                let moveSigma = volumeAbnormality * targetVol * sqrt volumePerMove
                let c, label =
                    match trendDayType.Value with
                    | DowntrendDay -> -0.3, "DriftDown"
                    | UptrendDay -> 0.3, "DriftUp"
                let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, c * moveSigma, moveSigma)
                generateDrift baseParams (label :: baseLabel()) volumeAbnormality target targetSigma volumePerMove true ctx cont

        // Hold pattern: 9x abnormal volume (mean), alternates between loose and tight consolidation,
        // followed by a release that has 4.5x abnormal volume (mean) and ±1.5 sigma target in the
        // direction of the current day type. The duration multiplier scales how
        // much volume the hold consumes (Short / Mid / Long) and the EV of the release.
        let durationLabel mult =
            match mult with
            | 0.5 -> "Short"
            | 1.0 -> "Mid"
            | 2.0 -> "Long"
            | other -> failwithf "Unrecognized hold duration multiplier: %g" other

        let hold volumePerMoveMult ctx cont =
            let volumeAbnormality = sampleVolumeAbnormality ctx 9.0
            let volumePerMove = volumePerMove baseParams ctx volumeUnitsPerMove
            let looseSigma = 10. * targetVol * sqrt (baseParams.BaseVolume * baseParams.BaseRate)
            let tightSigma = 0.5 * targetVol * sqrt (baseParams.BaseVolume * baseParams.BaseRate)
            generateHold baseParams (durationLabel volumePerMoveMult :: baseLabel()) volumeAbnormality
                (tightSigma, volumePerMove * 0.8 * 0.3)
                (looseSigma, volumePerMove * 0.2 * 0.3)
                (volumePerMove * volumePerMoveMult) false ctx cont

        let release volumePerMoveMult ctx cont =
            let volumeAbnormality = sampleVolumeAbnormality ctx 4.5
            let volumePerMove = volumePerMove baseParams ctx (1.5 * volumeUnitsPerMove)
            let moveSigma = volumeAbnormality * targetVol * sqrt volumePerMove
            let c =
                match trendDayType.Value with
                | DowntrendDay -> -1.5
                | UptrendDay -> 1.5
            let target = ctx.StartTarget + Normal.Sample(ctx.Effects.Rng, volumePerMoveMult * c * moveSigma, moveSigma)
            generateDrift baseParams ("HoldRelease" :: durationLabel volumePerMoveMult :: baseLabel()) volumeAbnormality target targetSigma volumePerMove false ctx cont
            
        let holdSeq volumePerMoveMult : Pattern<'r> = sequence [retarget; hold volumePerMoveMult; release volumePerMoveMult]

        // Flips the day's trend type.
        let flip ctx cont =
            trendDayType.Value <-
                match trendDayType.Value with
                | UptrendDay -> DowntrendDay
                | DowntrendDay -> UptrendDay
            cont ctx

        let flippingHoldSeq volumePerMoveMult : Pattern<'r> = sequence [retarget; hold volumePerMoveMult; flip; release volumePerMoveMult]

        // Session-specific patterns
        let morning : Pattern<'r> =
            choice [
                driftTrending, 0.8
                choice [
                    for volumePerMoveMult in [0.5; 1.0; 2.0] do
                        holdSeq volumePerMoveMult, 0.9 / volumePerMoveMult
                        flippingHoldSeq volumePerMoveMult, 0.1 / volumePerMoveMult
                ], 0.2
            ]
        let mid : Pattern<'r> = driftFlat
        let close : Pattern<'r> = morning

        // Apply repeat to each session pattern and sequence them
        sequence [repeat morning; retarget; repeat mid; retarget; repeat close] ctx cont
