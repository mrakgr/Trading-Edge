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

// =============================================================================
// Trading Simulation
// =============================================================================

type Side = Long | Short

type TradingDecision = {
    Timestamp: DateTime
    Price: float
    Side: Side
    Shares: float
}

type TradingResult = {
    Decisions: ImmutableList<TradingDecision>
    RealizedPnL: float
}

type TradingSimulator(vwapSystem: VwapSystemEffect, barSize: float, positionSize: float, flattenTime: DateTime) =
    let mutable decisions = ImmutableList<TradingDecision>.Empty
    let mutable currentSide = None
    let mutable entryPrice = 0.0
    let mutable shares = 0.0
    let mutable realizedPnL = 0.0
    let mutable openingPrintTime = None
    let mutable isFlattened = false
    let mutable prevBarCount = 0

    let closePnL (exitPrice: float) =
        match currentSide with
        | Some Long -> (exitPrice - entryPrice) * shares
        | Some Short -> (entryPrice - exitPrice) * shares
        | None -> 0.0

    let flatten (timestamp: DateTime) (price: float) =
        if currentSide.IsSome && not isFlattened then
            realizedPnL <- realizedPnL + closePnL price
            let flatSide = match currentSide.Value with Long -> Short | Short -> Long
            decisions <- decisions.Add {
                Timestamp = timestamp
                Price = price
                Side = flatSide
                Shares = shares
            }
            currentSide <- None
            shares <- 0.0
            isFlattened <- true

    let enterPosition (side: Side) (price: float) (timestamp: DateTime) =
        let newShares = round (positionSize / price)
        decisions <- decisions.Add {
            Timestamp = timestamp
            Price = price
            Side = side
            Shares = newShares
        }
        currentSide <- Some side
        entryPrice <- price
        shares <- newShares

    let processBar (bar: VolumeBar) =
        let signal = if bar.VWAP >= bar.VWMA then Long else Short
        match currentSide with
        | None ->
            enterPosition signal bar.VWAP bar.EndTime
        | Some current when current <> signal ->
            realizedPnL <- realizedPnL + closePnL bar.VWAP
            enterPosition signal bar.VWAP bar.EndTime
        | _ -> ()

    member _.AddTrade(trade: Trade) =
        if isFlattened then ()
        else

        // Detect opening print
        if trade.Session = OpeningPrint && openingPrintTime.IsNone then
            openingPrintTime <- Some trade.Timestamp

        vwapSystem.AddTrade trade

        // Check flatten time before processing new bars
        if trade.Timestamp >= flattenTime then
            flatten trade.Timestamp trade.Price
        else

        // Only trade after 5s past opening print, and only if VWMA is active
        match openingPrintTime with
        | Some opTime when trade.Timestamp >= opTime.AddSeconds 5.0 ->
            let bars = vwapSystem.GetVolumeBars barSize
            let newCount = bars.Count
            // Process any newly completed bars
            for i in prevBarCount .. newCount - 1 do
                if not isFlattened then
                    processBar bars.[i]
            prevBarCount <- newCount
        | _ ->
            prevBarCount <- (vwapSystem.GetVolumeBars barSize).Count

    member _.Result = {
        Decisions = decisions
        RealizedPnL = realizedPnL
    }

    member _.CurrentSide = currentSide
    member _.UnrealizedPnL =
        match currentSide with
        | Some _ -> closePnL (vwapSystem.GetVwap)
        | None -> 0.0
