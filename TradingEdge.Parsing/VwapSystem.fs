module TradingEdge.Parsing.VwapSystem

open System
open System.Collections.Immutable
open TradeLoader
open VolumeBars

// =============================================================================
// Incremental Volume Bar Builder (Reactive)
// =============================================================================

/// Stateful continuation that aggregates a group of trades into a VolumeBar.
/// Maintains a running cumulative volume across successive calls. Each
/// invocation computes VWAP and StdDev from the trades and passes the
/// resulting VolumeBar to onNext.
let volumeBarOfTrades () =
    let mutable cumulativeVolumeSum = 0.0
    fun onNext (trades: ImmutableList<Trade>) ->
        let mutable priceVolumeSum = 0.0
        let mutable priceSquaredVolumeSum = 0.0
        let mutable volumeSum = 0.0
        for t in trades do
            priceVolumeSum <- priceVolumeSum + t.Price * t.Volume
            priceSquaredVolumeSum <- priceSquaredVolumeSum + t.Price * t.Price * t.Volume
            volumeSum <- volumeSum + t.Volume
        let vwap = priceVolumeSum / volumeSum
        let variance = priceSquaredVolumeSum / volumeSum - vwap * vwap
        cumulativeVolumeSum <- cumulativeVolumeSum + volumeSum
        onNext {
            CumulativeVolume = cumulativeVolumeSum
            VWAP = vwap
            StdDev = sqrt (max 0.0 variance)
            Volume = volumeSum
            StartTime = trades.[0].Timestamp
            EndTime = trades.[trades.Count - 1].Timestamp
            NumTrades = trades.Count
        }

/// Splits a stream of trades into fixed-volume groups. Accumulates trades
/// (splitting individual trades at bar boundaries) until the volume sum
/// reaches barSize, then emits the group as an ImmutableList<Trade> via
/// onNext. One input trade can complete multiple groups.
let groupTrades barSize =
    let mutable currentTrades = ImmutableList<Trade>.Empty
    let mutable currentVolumeSum = 0.0

    fun onNext (trade : Trade) ->
        let mutable remaining = trade.Volume
        while remaining > 0.0 do
            let spaceLeft = barSize - currentVolumeSum
            if remaining <= spaceLeft then
                currentTrades <- currentTrades.Add { trade with Volume = remaining }
                currentVolumeSum <- currentVolumeSum + remaining
                remaining <- 0.0
            else
                if spaceLeft > 0.0 then
                    currentTrades <- currentTrades.Add { trade with Volume = spaceLeft }
                    currentVolumeSum <- currentVolumeSum + spaceLeft
                    remaining <- remaining - spaceLeft
                if currentVolumeSum >= barSize then
                    onNext currentTrades
                    currentTrades <- ImmutableList<Trade>.Empty
                    currentVolumeSum <- 0.0

/// Composes groupTrades and volumeBarOfTrades into a single stateful
/// continuation: Trade -> VolumeBar via onNext.
let volumeBarBuilder barSize =
    let tradesGrouper = groupTrades barSize 
    let barBuilder = volumeBarOfTrades ()
    fun onNext trade -> tradesGrouper (barBuilder onNext) trade

/// Batch-builds volume bars from a complete trade list by wiring groupTrades
/// into volumeBarOfTrades. Returns all completed bars as an ImmutableList.
let rebuildVolumeBars barSize (trades : Trade ImmutableList) =
    let mutable l = ImmutableList.Empty
    let barBuilder = volumeBarBuilder barSize
    Seq.iter (barBuilder (fun bar -> l <- l.Add bar)) trades
    l

// =============================================================================
// Pairwise bar variance (for volatility-adjusted position sizing)
// =============================================================================

/// Computes the combined variance of two Gaussians (volume bars) treated as a
/// mixture. Each bar is N(VWAP, StdDev²) weighted by Volume.
let pairwiseCombinedVariance (a: VolumeBar) (b: VolumeBar) =
    let vA = a.Volume
    let vB = b.Volume
    let vTotal = vA + vB
    if vTotal <= 0.0 then 0.0
    else
        let muCombined = (vA * a.VWAP + vB * b.VWAP) / vTotal
        let secondMoment = (vA * (a.StdDev * a.StdDev + a.VWAP * a.VWAP) + vB * (b.StdDev * b.StdDev + b.VWAP * b.VWAP)) / vTotal
        secondMoment - muCombined * muCombined

type VwapSystemBar = {
    Bar : VolumeBar
    Vwma : float
    VolFactor : float
}

/// Stateful continuation that enriches each VolumeBar with the expanding
/// VWMA and pairwise volatility factor. Wraps volumeBarBuilder, maintaining
/// a running VWAP sum/count and adjacent-bar variance accumulator. Emits
/// (VolumeBar, vwma, volFactor) via onNext on each completed bar.
/// When includeInVwma is false, the trade still produces volume bars (and
/// updates the vol factor) but does not affect the VWMA — useful for
/// premarket trades that should contribute to volatility estimation only.
let vwapSystemArgsBuilder barSize =
    let inner = volumeBarBuilder barSize
    let mutable vwapSum = 0.0
    let mutable barCount = 0
    let mutable varianceSum = 0.0
    let mutable pairCount = 0
    let mutable prevBar = ValueOption<VolumeBar>.None
    fun onNext (trade, includeInVwma) ->
        inner (fun bar ->
            if includeInVwma then
                vwapSum <- vwapSum + bar.VWAP
                barCount <- barCount + 1
            let vwma = if barCount > 0 then vwapSum / float barCount else 0.0
            match prevBar with
            | ValueSome prev ->
                varianceSum <- varianceSum + pairwiseCombinedVariance prev bar
                pairCount <- pairCount + 1
            | ValueNone -> ()
            prevBar <- ValueSome bar
            let volFactor = if pairCount > 0 then sqrt (varianceSum / float pairCount) else 0.0
            onNext {
                Bar = bar
                Vwma = vwma
                VolFactor = volFactor
            } 
        ) trade

type TradeStage =
    | BeforeOpeningPrint
    | AfterOpeningPrint
    | BeforeClosing

type MarketHours = {
    openTime : DateTime
    closeTime : DateTime
}

let defaultEstimationOffsets = [| 5.0; 30.0; 150.0; 750.0 |] // Offsets after the opening print.

let segregateTrades (window : MarketHours) (volPcts : float []) =
    let mutable trades_and_flags : (Trade * bool) ImmutableList = ImmutableList.Empty
    let mutable openingPrintTime : DateTime option = None
    let mutable vwapSystemArgs = None
    let mutable volPctsOffset = None
    let closing_pause = -60.0
    fun onNext (trade : Trade) ->
        let mutable finalBar = None
        match openingPrintTime with
        | Some openingPrintTime ->
            if window.closeTime.AddSeconds closing_pause <= trade.Timestamp then
                BeforeClosing
            else
                let currentOffset = (trade.Timestamp - openingPrintTime).TotalSeconds
                let i = Array.tryFindIndexBack (fun defaultOffset -> defaultOffset <= currentOffset) defaultEstimationOffsets
                if volPctsOffset <> i then
                    volPctsOffset <- i
                    volPctsOffset |> Option.iter (fun volPctsOffset ->
                        let totalVolume = trades_and_flags |> Seq.sumBy (fst >> _.Volume)
                        let newVwapArgsSystem = vwapSystemArgsBuilder (totalVolume * volPcts.[volPctsOffset])
                        for tf in trades_and_flags do newVwapArgsSystem (fun bar -> finalBar <- Some bar) tf
                        vwapSystemArgs <- Some newVwapArgsSystem
                    )
                AfterOpeningPrint
        | None ->
            if trade.Session = OpeningPrint then
                openingPrintTime <- Some trade.Timestamp
                AfterOpeningPrint
            else
                BeforeOpeningPrint
        |> fun stage ->
            let trade_and_flag = trade, stage <> BeforeOpeningPrint
            trades_and_flags <- trades_and_flags.Add trade_and_flag
            match vwapSystemArgs with
            | Some vwapSystem -> vwapSystem (fun bar -> finalBar <- Some bar) trade_and_flag
            | None -> ()
            onNext (finalBar, stage, trade)
                
// =============================================================================
// Trading Simulation
// =============================================================================

type SimulatorState =
    | Active of price : float * position : int
    | Done

type TradingDecision = {
    Timestamp: DateTime
    Price: float
    Shares: int
}

let vwapSystem (positionSize: float, referenceVol: float option) =
    let mutable state = Active(0.0, 0)
    let effectiveSize vf =
        match referenceVol with
        | None -> positionSize
        | Some refVol -> min positionSize (positionSize * refVol / vf)
    fun onNext (bar : option<VwapSystemBar>, stage : TradeStage, trade : Trade) ->
        match stage with
        | BeforeOpeningPrint -> None
        | AfterOpeningPrint ->
            match bar with
            | Some bar ->
                match state with
                | Active(_, position) ->
                    let lastBar = bar.Bar
                    let targetShares = round (effectiveSize bar.VolFactor / trade.Price) |> int
                    if targetShares > 0 then
                        if lastBar.VWAP >= bar.Vwma && position <= 0 then
                            state <- Active(lastBar.VWAP, targetShares)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = targetShares }
                        elif lastBar.VWAP < bar.Vwma && position >= 0 then
                            state <- Active(lastBar.VWAP, -targetShares)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = -targetShares }
                        else
                            None
                    else
                        if lastBar.VWAP >= bar.Vwma && position < 0 then
                            state <- Active(lastBar.VWAP, 0)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = 0 }
                        elif lastBar.VWAP < bar.Vwma && position > 0 then
                            state <- Active(lastBar.VWAP, 0)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = 0 }
                        else
                            None
                | Done -> None
            | None -> None
        | BeforeClosing ->
            match state with
            | Active(_, position) when position <> 0 ->
                state <- Done
                Some { Timestamp = trade.Timestamp; Price = trade.Price; Shares = 0 }
            | _ -> None
        |> fun tradingDecision -> 
            onNext (tradingDecision, trade)
    
// type TradingResult = {
//     Decisions: ImmutableList<TradingDecision>
//     RealizedPnL: float
// }

// type DecisionTracker() =
//     let mutable decisions = ImmutableList<TradingDecision>.Empty
//     let mutable realizedPnL = 0.0

//     member _.Add (d : TradingDecision) =
//         if decisions.Count > 0 then
//             let prev = decisions.[decisions.Count - 1]
//             realizedPnL <- realizedPnL + (d.Price - prev.Price) * prev.Shares
//         decisions <- decisions.Add d

//     member _.Decisions = decisions
//     member _.RealizedPnL = realizedPnL

//     member _.Position =
//         if decisions.Count > 0 then decisions.[decisions.Count - 1].Shares
//         else 0.0

//     member _.LastPrice =
//         if decisions.Count > 0 then decisions.[decisions.Count - 1].Price
//         else 0.0

// let splitter a b trade = a trade; b trade

// /// Daily loss limit enforcement stage. Sits between make_trading_decisions and
// /// the final decision sink. Forwards every decision through the tracker and,
// /// as soon as the tracker's running realized P&L drops below -lossLimit,
// /// injects a flatten at the same timestamp/price and then silently drops all
// /// further decisions for the rest of the day.
// let enforce_loss_limit (tracker: DecisionTracker) (lossLimit: float) on_succ =
//     let mutable tripped = false
//     fun (d: TradingDecision) ->
//         if tripped then ()
//         else
//             on_succ d
//             if tracker.RealizedPnL <= -lossLimit then
//                 tripped <- true
//                 if d.Shares <> 0.0 then
//                     on_succ { Timestamp = d.Timestamp; Price = d.Price; Shares = 0.0 }

// /// Daily activity limit enforcement stage. Forwards decisions until the
// /// configured maximum is reached, then injects a same-price flatten if a
// /// position is still open and drops everything after. The forced flatten is
// /// not counted against the cap — maxTrades represents discretionary entries
// /// and flips produced by make_trading_decisions.
// let enforce_activity_limit (maxTrades: int) on_succ =
//     let mutable count = 0
//     let mutable tripped = false
//     fun (d: TradingDecision) ->
//         if tripped then ()
//         else
//             on_succ d
//             count <- count + 1
//             if count >= maxTrades then
//                 tripped <- true
//                 if d.Shares <> 0.0 then
//                     on_succ { Timestamp = d.Timestamp; Price = d.Price; Shares = 0.0 }

// /// Daily losing-trade limit enforcement stage. Watches the tracker's running
// /// realized P&L after each decision is forwarded — whenever it drops, that
// /// decision counts as a losing trade. Once the configured maximum is reached,
// /// injects a same-price flatten if still in position and drops everything
// /// after. Winning and flat decisions do not count against the cap: the idea
// /// is that high activity is fine as long as the system is producing profits.
// let enforce_loss_count_limit (tracker: DecisionTracker) (maxLosses: int) on_succ =
//     let mutable losses = 0
//     let mutable prevPnL = 0.0
//     let mutable tripped = false
//     fun (d: TradingDecision) ->
//         if tripped then ()
//         else
//             on_succ d
//             let newPnL = tracker.RealizedPnL
//             if newPnL < prevPnL then
//                 losses <- losses + 1
//             prevPnL <- newPnL
//             if losses >= maxLosses then
//                 tripped <- true
//                 if d.Shares <> 0.0 then
//                     on_succ { Timestamp = d.Timestamp; Price = d.Price; Shares = 0.0 }

// type VwapSimulator(window : MarketHours, vwapSystem: VwapSystemEffectDynamic, positionSize: float, ?referenceVol: float, ?lossLimit: float, ?maxTrades: int, ?maxLosses: int) =
//     let decision_tracker = DecisionTracker()
//     let on_decision =
//         let mutable sink : TradingDecision -> unit = decision_tracker.Add
//         match lossLimit with
//         | Some limit -> sink <- enforce_loss_limit decision_tracker limit sink
//         | None -> ()
//         match maxLosses with
//         | Some n -> sink <- enforce_loss_count_limit decision_tracker n sink
//         | None -> ()
//         match maxTrades with
//         | Some n -> sink <- enforce_activity_limit n sink
//         | None -> ()
//         sink
//     let pipeline =
//         segregate_trades window
//             (splitter
//                 (track_volume_bars vwapSystem)
//                 (make_trading_decisions
//                     (vwapSystem, positionSize, referenceVol)
//                     on_decision))
            
//     member _.AddTrade(trade: Trade) = pipeline trade

//     member _.Result = {
//         Decisions = decision_tracker.Decisions
//         RealizedPnL = decision_tracker.RealizedPnL
//     }

//     member _.Position = decision_tracker.Position

//     member _.UnrealizedPnL =
//         if decision_tracker.Position <> 0.0 then
//             (vwapSystem.GetVwap - decision_tracker.LastPrice) * decision_tracker.Position
//         else 0.0
