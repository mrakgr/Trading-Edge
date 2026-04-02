module TradingEdge.Parsing.VwapSystem

open System
open System.Collections.Immutable
open TradeLoader
open VolumeBars

// =============================================================================
// Incremental Volume Bar Builder
// =============================================================================

type VolumeBarBuilder(barSize: float) =
    // Current in-progress bar state
    let mutable currentPriceVolumeSum = 0.0
    let mutable currentPriceSquaredVolumeSum = 0.0
    let mutable currentVolumeSum = 0.0
    let mutable currentTradeCount = 0
    let mutable currentStartTime = Unchecked.defaultof<DateTime>
    let mutable hasStartTime = false

    // Cumulative state across all bars
    let mutable cumulativeVolume = 0.0

    // Completed bars
    let mutable bars = ImmutableList<VolumeBar>.Empty

    let finishBar (endTime: DateTime) (cumulativeVwap: float) =
        let vwap = currentPriceVolumeSum / currentVolumeSum
        let variance = currentPriceSquaredVolumeSum / currentVolumeSum - vwap * vwap
        let stddev = sqrt (max 0.0 variance)
        cumulativeVolume <- cumulativeVolume + currentVolumeSum
        let bar = {
            CumulativeVolume = cumulativeVolume
            VWAP = vwap
            StdDev = stddev
            Volume = currentVolumeSum
            StartTime = currentStartTime
            EndTime = endTime
            NumTrades = currentTradeCount
            VWMA = cumulativeVwap
        }
        bars <- bars.Add(bar)
        currentPriceVolumeSum <- 0.0
        currentPriceSquaredVolumeSum <- 0.0
        currentVolumeSum <- 0.0
        currentTradeCount <- 0
        hasStartTime <- false

    member _.AddTrade(trade: Trade, cumulativeVwap: float) =
        let mutable remaining = trade.Volume
        while remaining > 0.0 do
            if not hasStartTime then
                currentStartTime <- trade.Timestamp
                hasStartTime <- true

            let spaceLeft = barSize - currentVolumeSum
            if remaining <= spaceLeft then
                currentPriceVolumeSum <- currentPriceVolumeSum + trade.Price * remaining
                currentPriceSquaredVolumeSum <- currentPriceSquaredVolumeSum + trade.Price * trade.Price * remaining
                currentVolumeSum <- currentVolumeSum + remaining
                currentTradeCount <- currentTradeCount + 1
                remaining <- 0.0
            else
                if spaceLeft > 0.0 then
                    currentPriceVolumeSum <- currentPriceVolumeSum + trade.Price * spaceLeft
                    currentPriceSquaredVolumeSum <- currentPriceSquaredVolumeSum + trade.Price * trade.Price * spaceLeft
                    currentVolumeSum <- currentVolumeSum + spaceLeft
                    currentTradeCount <- currentTradeCount + 1
                    remaining <- remaining - spaceLeft
                if currentVolumeSum >= barSize then
                    finishBar trade.Timestamp cumulativeVwap

    member _.Bars = bars
    member _.BarSize = barSize

// =============================================================================
// Bar Size Constants
// =============================================================================

/// Powers of 2 from 1k to 1024k: 1000, 2000, 4000, ..., 1024000
let barSizes = [| for i in 0..10 -> float (pown 2 i) * 1000.0 |]

// =============================================================================
// Cumulative VWMA Tracker (anchored on opening print)
// =============================================================================

type VwmaTracker() =
    let mutable priceVolumeSum = 0.0
    let mutable totalVolume = 0.0
    let mutable isAfterOpen = false

    member _.AddTrade(trade: Trade) =
        match trade.Session with
        | OpeningPrint ->
            isAfterOpen <- true
            priceVolumeSum <- priceVolumeSum + trade.Price * trade.Volume
            totalVolume <- totalVolume + trade.Volume
        | (RegularHours | ClosingPrint) when isAfterOpen ->
            priceVolumeSum <- priceVolumeSum + trade.Price * trade.Volume
            totalVolume <- totalVolume + trade.Volume
        | _ -> ()

    member _.IsActive = isAfterOpen
    member _.Vwap = if totalVolume > 0.0 then priceVolumeSum / totalVolume else 0.0

// =============================================================================
// VwapSystemEffect
// =============================================================================

type VwapSystemEffect =
    abstract member AddTrade : Trade -> unit
    abstract member GetVwap : float
    abstract member GetTotalVolume : float
    abstract member GetVolumeBars : float -> ImmutableList<VolumeBar>

let createVwapSystem () =
    let builders = barSizes |> Array.map VolumeBarBuilder
    let vwma = VwmaTracker()
    let mutable totalVolume = 0.0

    { new VwapSystemEffect with
        member _.AddTrade(trade) =
            // Track total volume from all sessions (including premarket)
            totalVolume <- totalVolume + trade.Volume
            // VWMA only uses regular hours data
            vwma.AddTrade(trade)
            // Volume bars built from start of day (all sessions)
            for b in builders do b.AddTrade(trade, vwma.Vwap)

        member _.GetVwap = vwma.Vwap

        member _.GetTotalVolume = totalVolume

        member _.GetVolumeBars(desiredSize) =
            let mutable bestIdx = 0
            let mutable bestDist = abs (barSizes.[0] - desiredSize)
            for i in 1 .. barSizes.Length - 1 do
                let dist = abs (barSizes.[i] - desiredSize)
                if dist < bestDist then
                    bestIdx <- i
                    bestDist <- dist
            builders.[bestIdx].Bars
    }
