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
        | RegularHours | ClosingPrint when isAfterOpen ->
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
        member _.GetVolumeBars desiredSize =
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

type IncomingTrades =
    | BeforeOpeningPrint of Trade
    | AfterOpeningPrint of Trade
    | AfterOpeningPrintAndPause of Trade
    | BeforeClosing of Trade
    | AfterClosing of Trade

type MarketHours = {
    openTime : DateTime
    closeTime : DateTime
}

let segregate_trades (window : MarketHours) on_succ =
    let mutable openingPrintTime : DateTime option = None
    let opening_pause = 5.0
    let closing_pause = -60.0
    fun (trade : Trade) ->
        match openingPrintTime with
        | None ->
            if trade.Session = OpeningPrint then
                openingPrintTime <- Some trade.Timestamp
                on_succ (AfterOpeningPrint trade)
            else
                on_succ (BeforeOpeningPrint trade)
        | Some openingPrintTime ->
            let t = trade.Timestamp
            if t < openingPrintTime.AddSeconds opening_pause then
                on_succ (AfterOpeningPrint trade)
            elif openingPrintTime.AddSeconds opening_pause <= t && t < window.closeTime.AddSeconds closing_pause then
                on_succ (AfterOpeningPrintAndPause trade)
            elif window.closeTime.AddSeconds closing_pause <= t && t < window.closeTime then
                on_succ (BeforeClosing trade)
            elif window.closeTime <= t then
                on_succ (AfterClosing trade)

let track_volume_bars (vwapSystem: VwapSystemEffect) =
    function
    | BeforeOpeningPrint _ -> ()
    | AfterOpeningPrint trade | AfterOpeningPrintAndPause trade | BeforeClosing trade | AfterClosing trade -> vwapSystem.AddTrade trade

type SimulatorState =
    | Active of price : float * position : float
    | Done

type TradingDecision = {
    Timestamp: DateTime
    Price: float
    Shares: float
}

let make_trading_decisions (vwapSystem: VwapSystemEffect, barSize: float, positionSize: float) on_succ =
    let mutable state = Active(0.0, 0.0)
    let mutable prevBars : ImmutableList<VolumeBar> = ImmutableList<VolumeBar>.Empty
    function
    | BeforeOpeningPrint _ | AfterOpeningPrint _ | AfterClosing _ -> () // Not intended for this node.
    | AfterOpeningPrintAndPause trade ->
        let bars = vwapSystem.GetVolumeBars barSize
        if not (Object.ReferenceEquals(bars, prevBars)) then
            prevBars <- bars
            let vwma = vwapSystem.GetVwap
            match state with
            | Active(_, position) ->
                let lastBar = bars.[bars.Count - 1]
                let targetShares = round (positionSize / trade.Price)
                if lastBar.VWAP >= vwma && position <= 0.0 then
                    state <- Active(lastBar.VWAP, targetShares)
                    on_succ { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = targetShares }
                elif lastBar.VWAP < vwma && position >= 0.0 then
                    state <- Active(lastBar.VWAP, -targetShares)
                    on_succ { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = -targetShares }
            | Done -> ()
    | BeforeClosing trade ->
        match state with
        | Active(_, position) when position <> 0.0 ->
            state <- Done
            on_succ { Timestamp = trade.Timestamp; Price = trade.Price; Shares = 0.0 }
        | _ -> ()

type TradingResult = {
    Decisions: ImmutableList<TradingDecision>
    RealizedPnL: float
}

type DecisionTracker() =
    let mutable decisions = ImmutableList<TradingDecision>.Empty
    let mutable realizedPnL = 0.0

    member _.Add (d : TradingDecision) =
        if decisions.Count > 0 then
            let prev = decisions.[decisions.Count - 1]
            realizedPnL <- realizedPnL + (d.Price - prev.Price) * prev.Shares
        decisions <- decisions.Add d

    member _.Decisions = decisions
    member _.RealizedPnL = realizedPnL

    member _.Position =
        if decisions.Count > 0 then decisions.[decisions.Count - 1].Shares
        else 0.0

    member _.LastPrice =
        if decisions.Count > 0 then decisions.[decisions.Count - 1].Price
        else 0.0

let splitter a b trade = a trade; b trade

type VwapSimulator(window : MarketHours, barSize: float, positionSize: float) =
    let decision_tracker = DecisionTracker()
    let vwapSystem = createVwapSystem()
    let pipeline =
        segregate_trades window
            (splitter
                (track_volume_bars vwapSystem)
                (make_trading_decisions
                    (vwapSystem, barSize, positionSize)
                    decision_tracker.Add))
            
    member _.AddTrade(trade: Trade) = pipeline trade

    member _.Result = {
        Decisions = decision_tracker.Decisions
        RealizedPnL = decision_tracker.RealizedPnL
    }

    member _.Position = decision_tracker.Position

    member _.UnrealizedPnL =
        if decision_tracker.Position <> 0.0 then
            (vwapSystem.GetVwap - decision_tracker.LastPrice) * decision_tracker.Position
        else 0.0
