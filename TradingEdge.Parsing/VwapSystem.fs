module TradingEdge.Parsing.VwapSystem

open System
open System.Collections.Immutable
open TDigest
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
    fun onNext (trades: ImmutableArray<Trade>) ->
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
            EndTime = trades.[trades.Length - 1].Timestamp
            NumTrades = trades.Length
            Trades = trades
        }

/// Splits a stream of trades into fixed-volume groups. Accumulates trades
/// (splitting individual trades at bar boundaries) until the volume sum
/// reaches barSize, then emits the group as a ResizeArray<Trade> via
/// onNext. One input trade can complete multiple groups.
let groupTrades barSize =
    let currentTrades = ImmutableArray.CreateBuilder<Trade>()
    
    let mutable currentVolumeSum = 0.0

    fun onNext (trade : Trade) ->
        let mutable remaining = trade.Volume
        while remaining > 0.0 do
            let spaceLeft = barSize - currentVolumeSum
            if remaining <= spaceLeft then
                currentTrades.Add { trade with Volume = remaining }
                currentVolumeSum <- currentVolumeSum + remaining
                remaining <- 0.0
            else
                if spaceLeft > 0.0 then
                    currentTrades.Add { trade with Volume = spaceLeft }
                    currentVolumeSum <- currentVolumeSum + spaceLeft
                    remaining <- remaining - spaceLeft
                if currentVolumeSum >= barSize then
                    onNext (currentTrades.ToImmutableArray())
                    currentTrades.Clear()
                    currentVolumeSum <- 0.0

/// Composes groupTrades and volumeBarOfTrades into a single stateful
/// continuation: Trade -> VolumeBar via onNext.
let volumeBarBuilder barSize =
    let tradesGrouper = groupTrades barSize 
    let barBuilder = volumeBarOfTrades ()
    fun onNext trade -> tradesGrouper (barBuilder onNext) trade

/// Batch-builds volume bars from a complete trade list by wiring groupTrades
/// into volumeBarOfTrades. Returns all completed bars as an ImmutableArray.
let rebuildVolumeBars barSize (trades : Trade ImmutableArray) =
    let mutable l = ResizeArray()
    let barBuilder = volumeBarBuilder barSize
    Seq.iter (barBuilder (fun bar -> l.Add bar)) trades
    l.ToImmutableArray()

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
        let absoluteVariance = secondMoment - muCombined * muCombined
        if muCombined > 0.0 then absoluteVariance / (muCombined * muCombined)
        else 0.0

let pairwiseRealizedVariance (a: VolumeBar) (b: VolumeBar) =
    assert (a.Volume = b.Volume)
    log (a.VWAP / b.VWAP) ** 2


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
    let mutable logVwapSum = 0.0
    let mutable barCount = 0
    let mutable varianceSum = 0.0
    let mutable pairCount = 0
    let mutable prevBar = ValueOption<VolumeBar>.None
    fun onNext (trade, includeInVwma, includeInVol) ->
        inner (fun bar ->
            if includeInVwma then
                logVwapSum <- logVwapSum + log bar.VWAP
                barCount <- barCount + 1
            let vwma = if barCount > 0 then exp (logVwapSum / float barCount) else 0.0
            match prevBar with
            | ValueSome prev ->
                if includeInVol then
                    varianceSum <- varianceSum + pairwiseRealizedVariance prev bar
                    pairCount <- pairCount + 1
            | ValueNone -> ()
            prevBar <- ValueSome bar
            let volFactor = if pairCount > 0 then exp (sqrt (varianceSum / float pairCount)) - 1.0 else 0.0
            onNext {
                Bar = bar
                Vwma = vwma
                VolFactor = volFactor
            }
        ) trade

type TradeStage =
    | EarlyPremarket
    | LatePremarket
    | AfterOpeningPrint
    | BeforeClosing

type MarketHours = {
    openTime : DateTime
    closeTime : DateTime
}

let defaultEstimationOffsets = [| 5.0; 30.0; 150.0; 750.0 |] // Offsets after the opening print.

/// Stateful continuation that classifies trades into stages and manages
/// dynamic bar sizing. Buffers all trades with their includeInVwma flags.
/// When an estimation offset is crossed, rebuilds the vwapSystemArgsBuilder
/// with barSize = totalVolume * volPcts[i] and replays all buffered trades.
/// Emits (VwapSystemBar option, TradeStage, Trade) via onNext per trade.
let segregateTrades (window : MarketHours) (volPcts : float []) =
    let mutable trades_and_flags : (Trade * bool * bool) ResizeArray = ResizeArray()
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
                        let totalVolume = trades_and_flags |> Seq.sumBy (fun (t, _, _) -> t.Volume)
                        let newVwapArgsSystem = vwapSystemArgsBuilder (totalVolume * volPcts.[volPctsOffset])
                        for tf in trades_and_flags do newVwapArgsSystem (fun bar -> finalBar <- Some bar) tf
                        vwapSystemArgs <- Some newVwapArgsSystem
                    )
                AfterOpeningPrint
        | None ->
            if trade.Session = OpeningPrint then
                openingPrintTime <- Some trade.Timestamp
                AfterOpeningPrint
            elif window.openTime.AddHours(-1.0) <= trade.Timestamp then
                LatePremarket
            else
                EarlyPremarket
        |> fun stage ->
            let includeInVwma =
                match stage with
                | AfterOpeningPrint | BeforeClosing -> true
                | _ -> false
            let includeInVol =
                match stage with
                | LatePremarket | AfterOpeningPrint | BeforeClosing -> true
                | _ -> false
            let trade_and_flag = trade, includeInVwma, includeInVol
            trades_and_flags.Add trade_and_flag
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

/// Stateful continuation that makes trading decisions based on VWAP signals.
/// Goes long when bar VWAP >= VWMA, short when below. Position size is
/// adjusted by volFactor when referenceVol is provided. Flattens on
/// BeforeClosing. Emits (VwapSystemBar option, TradingDecision option, Trade)
/// via onNext.
let vwapSystem (positionSize: float, referenceVol: float option, bandVol: float) =
    let mutable state = Active(0.0, 0)
    let effectiveSize vf =
        match referenceVol with
        | None -> positionSize
        | Some refVol -> min positionSize (positionSize * refVol / vf)
    fun onNext (bar : option<VwapSystemBar>, stage : TradeStage, trade : Trade) ->
        match stage with
        | EarlyPremarket | LatePremarket -> None
        | AfterOpeningPrint ->
            match bar with
            | Some bar ->
                match state with
                | Active(_, position) ->
                    let lastBar = bar.Bar
                    let targetShares = round (effectiveSize bar.VolFactor / trade.Price) |> int
                    let band = bandVol * bar.VolFactor * lastBar.VWAP
                    if targetShares > 0 then
                        if lastBar.VWAP + band >= bar.Vwma && position <= 0 then
                            state <- Active(lastBar.VWAP, targetShares)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = targetShares }
                        elif lastBar.VWAP - band < bar.Vwma && position >= 0 then
                            state <- Active(lastBar.VWAP, -targetShares)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = -targetShares }
                        elif lastBar.VWAP >= bar.Vwma && position <= 0 then
                            state <- Active(lastBar.VWAP, targetShares)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = 0 }
                        elif lastBar.VWAP < bar.Vwma && position >= 0 then
                            state <- Active(lastBar.VWAP, -targetShares)
                            Some { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = 0 }
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
            onNext (bar, tradingDecision, trade)
    
type TradingResult = {
    Decisions: ImmutableList<TradingDecision>
    RealizedPnL: float
}

/// Stateful continuation that tracks trading decisions and computes running
/// realized PnL. Returns (processor, getResult) where processor follows the
/// standard fun onNext input -> pattern, forwarding the full tuple downstream.
let trackDecisions () =
    let mutable decisions = ImmutableList<TradingDecision>.Empty
    let mutable realizedPnL = 0.0
    (fun onNext (bar: VwapSystemBar option, decision: TradingDecision option, trade: Trade) ->
        match decision with
        | Some d ->
            if decisions.Count > 0 then
                let prev = decisions.[decisions.Count - 1]
                realizedPnL <- realizedPnL + (d.Price - prev.Price) * float prev.Shares
            decisions <- decisions.Add d
        | None -> ()
        onNext (bar, decision, trade)),
    (fun () -> { Decisions = decisions; RealizedPnL = realizedPnL })

/// Daily loss limit enforcement. Forwards decisions to onNext until running
/// PnL drops below -lossLimit, then injects a flatten and suppresses all
/// further decisions. Bar and trade data always flows through.
let enforceLossLimit (getPnL: unit -> float) (lossLimit: float) =
    let mutable tripped = false
    fun onNext (bar: VwapSystemBar option, decision: TradingDecision option, trade: Trade) ->
        if tripped then
            onNext (bar, None, trade)
        else
            onNext (bar, decision, trade)
            match decision with
            | Some d when getPnL() <= -lossLimit ->
                tripped <- true
                if d.Shares <> 0 then
                    onNext (bar, Some { d with Shares = 0 }, trade)
            | _ -> ()

/// Daily activity limit enforcement. Forwards decisions until the configured
/// maximum is reached, then injects a flatten and suppresses all further
/// decisions. Bar and trade data always flows through.
let enforceActivityLimit (maxTrades: int) =
    let mutable count = 0
    let mutable tripped = false
    fun onNext (bar: VwapSystemBar option, decision: TradingDecision option, trade: Trade) ->
        if tripped then
            onNext (bar, None, trade)
        else
            onNext (bar, decision, trade)
            match decision with
            | Some d ->
                count <- count + 1
                if count >= maxTrades then
                    tripped <- true
                    if d.Shares <> 0 then
                        onNext (bar, Some { d with Shares = 0 }, trade)
            | None -> ()

/// Daily losing-trade limit enforcement. Counts decisions that cause PnL to
/// drop. Once maxLosses is reached, injects a flatten and suppresses all
/// further decisions. Winning/flat decisions don't count against the cap.
/// Bar and trade data always flows through.
let enforceLossCountLimit (getPnL: unit -> float) (maxLosses: int) =
    let mutable losses = 0
    let mutable prevPnL = 0.0
    let mutable tripped = false
    fun onNext (bar: VwapSystemBar option, decision: TradingDecision option, trade: Trade) ->
        if tripped then
            onNext (bar, None, trade)
        else
            onNext (bar, decision, trade)
            match decision with
            | Some d ->
                let newPnL = getPnL()
                if newPnL < prevPnL then
                    losses <- losses + 1
                prevPnL <- newPnL
                if losses >= maxLosses then
                    tripped <- true
                    if d.Shares <> 0 then
                        onNext (bar, Some { d with Shares = 0 }, trade)
            | None -> ()

// =============================================================================
// Fill Simulator
// =============================================================================

type Fill = {
    Timestamp: DateTime
    Price: float
    Quantity: int
}

type LimitOrder = {
    /// (price, activationTime) pairs, newest first. On reprice, a new entry
    /// is prepended; older entries remain active until the newer one activates.
    Prices: (float * DateTime) list
    /// (quantity, activationTime) pairs, newest first. On quantity change, a
    /// new entry is prepended; the broker-side quantity updates after the delay.
    Quantities: (int * DateTime) list
    /// How much of the order has actually been filled so far.
    FilledQuantity: int
    CancellationTime: DateTime option
}

type FillResult = {
    Fills: ImmutableArray<Fill>
    RealizedPnL: float
    Commissions: float
}

/// Stateful continuation that simulates limit-order execution against the
/// trade stream. Manages explicit buy/sell limit orders with activation and
/// cancellation delays. Only one side (buy or sell) can be active at a time.
///
/// On a flip: cancels the current side (cancellation confirms after delay),
/// then places the new side only once cancellation is confirmed (placement
/// also takes a delay to activate).
///
/// Fill rule: a buy order at price P is filled by any trade printed at <= P,
/// and a sell order at P by any trade at >= P. Fills are recorded at the limit
/// price, not the trade price. Partial fills are tracked.
///
/// Returns (processor, getResult).
let fillSimulator (percentile: float) (delayMs: float) =
    let mutable buyOrder : LimitOrder option = None
    let mutable sellOrder : LimitOrder option = None
    let mutable targetPosition = 0
    let mutable filledPosition = 0
    let mutable lastDigest : MergingDigest option = None
    // When we're waiting for a cancellation before flipping, store the
    // target we need to submit once the cancel confirms.
    let mutable pendingTarget : int option = None
    let delay = TimeSpan.FromMilliseconds delayMs
    let fills = ResizeArray<Fill>()

    let computePrice (digest: MergingDigest) (isBuy: bool) =
        digest.Quantile(if isBuy then percentile else 1.0 - percentile)

    let placeOrder (now: DateTime) =
        let needed = targetPosition - filledPosition
        let activationTime = now + delay
        match lastDigest with
        | Some digest when needed > 0 ->
            assert (sellOrder.IsNone)
            buyOrder <- Some {
                Prices = [computePrice digest true, activationTime]
                Quantities = [needed, activationTime]
                FilledQuantity = 0
                CancellationTime = None
            }
        | Some digest when needed < 0 ->
            assert (buyOrder.IsNone)
            sellOrder <- Some {
                Prices = [computePrice digest false, activationTime]
                Quantities = [-needed, activationTime]
                FilledQuantity = 0
                CancellationTime = None
            }
        | _ -> ()

    let cancelBuyOrder (now: DateTime) =
        match buyOrder with
        | Some order when order.CancellationTime.IsNone ->
            buyOrder <- Some { order with CancellationTime = Some (now + delay) }
        | _ -> ()

    let cancelSellOrder (now: DateTime) =
        match sellOrder with
        | Some order when order.CancellationTime.IsNone ->
            sellOrder <- Some { order with CancellationTime = Some (now + delay) }
        | _ -> ()

    /// Find the latest active value from a (value, activationTime) list
    /// (newest first; first one whose activationTime <= now).
    let activeValue (now: DateTime) (entries: ('a * DateTime) list) =
        entries |> List.tryFind (fun (_, activationTime) -> now >= activationTime)
                |> Option.map fst

    let isCancelled (now: DateTime) (order: LimitOrder) =
        match order.CancellationTime with
        | Some cancelTime -> now >= cancelTime
        | None -> false

    let tryFillBuy (trade: Trade) =
        match buyOrder with
        | Some order when not (isCancelled trade.Timestamp order) ->
            match activeValue trade.Timestamp order.Prices, activeValue trade.Timestamp order.Quantities with
            | Some price, Some qty when trade.Price <= price ->
                let remaining = qty - order.FilledQuantity
                if remaining > 0 then
                    let fillQty = min remaining (int trade.Volume)
                    if fillQty > 0 then
                        fills.Add { Timestamp = trade.Timestamp; Price = price; Quantity = fillQty }
                        filledPosition <- filledPosition + fillQty
                        buyOrder <- Some { order with FilledQuantity = order.FilledQuantity + fillQty }
            | _ -> ()
        | _ -> ()

    let tryFillSell (trade: Trade) =
        match sellOrder with
        | Some order when not (isCancelled trade.Timestamp order) ->
            match activeValue trade.Timestamp order.Prices, activeValue trade.Timestamp order.Quantities with
            | Some price, Some qty when trade.Price >= price ->
                let remaining = qty - order.FilledQuantity
                if remaining > 0 then
                    let fillQty = min remaining (int trade.Volume)
                    if fillQty > 0 then
                        fills.Add { Timestamp = trade.Timestamp; Price = price; Quantity = -fillQty }
                        filledPosition <- filledPosition - fillQty
                        sellOrder <- Some { order with FilledQuantity = order.FilledQuantity + fillQty }
            | _ -> ()
        | _ -> ()

    let processCancellations (now: DateTime) =
        // Check if buy cancellation has confirmed
        match buyOrder with
        | Some order ->
            match order.CancellationTime with
            | Some cancelTime when now >= cancelTime -> buyOrder <- None
            | _ -> ()
        | None -> ()
        // Check if sell cancellation has confirmed
        match sellOrder with
        | Some order ->
            match order.CancellationTime with
            | Some cancelTime when now >= cancelTime -> sellOrder <- None
            | _ -> ()
        | None -> ()
        // Remove fully filled orders only when the newest quantity is both
        // activated (confirmed by broker) and fully filled.
        match buyOrder with
        | Some order ->
            let newestQty, activationTime = order.Quantities.Head
            if now >= activationTime && newestQty <= order.FilledQuantity then
                buyOrder <- None
        | None -> ()
        match sellOrder with
        | Some order ->
            let newestQty, activationTime = order.Quantities.Head
            if now >= activationTime && newestQty <= order.FilledQuantity then
                sellOrder <- None
        | None -> ()

    let processPending (now: DateTime) =
        // If we were waiting for a cancellation to flip, check if we can now place
        match pendingTarget with
        | Some _ when buyOrder.IsNone && sellOrder.IsNone ->
            pendingTarget <- None
            placeOrder now
        | _ -> ()

    (fun onNext (bar: VwapSystemBar option, decision: TradingDecision option, trade: Trade) ->
        // 1. Process cancellations
        processCancellations trade.Timestamp

        // 2. New bar: rebuild t-digest and reprice the active order
        match bar with
        | Some b ->
            let digest = MergingDigest(100.0)
            for t in b.Bar.Trades do
                digest.Add(t.Price, int t.Volume)
            lastDigest <- Some digest
            // Reprice active order: prepend new price with activation delay
            match buyOrder with
            | Some order when order.CancellationTime.IsNone ->
                let newPrice = computePrice digest true, trade.Timestamp + delay
                buyOrder <- Some { order with Prices = newPrice :: order.Prices }
            | _ -> ()
            match sellOrder with
            | Some order when order.CancellationTime.IsNone ->
                let newPrice = computePrice digest false, trade.Timestamp + delay
                sellOrder <- Some { order with Prices = newPrice :: order.Prices }
            | _ -> ()
        | None -> ()

        // 3. New decision: determine if we need to flip sides or adjust quantity
        match decision with
        | Some d ->
            targetPosition <- d.Shares
            let needed = targetPosition - filledPosition
            let activationTime = trade.Timestamp + delay
            if needed > 0 then
                // Need to buy
                if sellOrder.IsSome then
                    cancelSellOrder trade.Timestamp
                    pendingTarget <- Some targetPosition
                elif buyOrder.IsNone then
                    pendingTarget <- Some targetPosition
                else
                    // Already have a buy order, update quantity
                    match buyOrder with
                    | Some order when order.CancellationTime.IsNone ->
                        let newQty = needed, activationTime
                        buyOrder <- Some { order with Quantities = newQty :: order.Quantities }
                    | _ -> ()
            elif needed < 0 then
                // Need to sell
                if buyOrder.IsSome then
                    cancelBuyOrder trade.Timestamp
                    pendingTarget <- Some targetPosition
                elif sellOrder.IsNone then
                    pendingTarget <- Some targetPosition
                else
                    // Already have a sell order, update quantity
                    match sellOrder with
                    | Some order when order.CancellationTime.IsNone ->
                        let newQty = -needed, activationTime
                        sellOrder <- Some { order with Quantities = newQty :: order.Quantities }
                    | _ -> ()
            else
                // Target reached, cancel any outstanding
                cancelBuyOrder trade.Timestamp
                cancelSellOrder trade.Timestamp
        | None -> ()

        // 4. Check fills
        let prevFillCount = fills.Count
        tryFillBuy trade
        tryFillSell trade
        for i in prevFillCount .. fills.Count - 1 do
            onNext fills.[i]

        // 5. Process pending orders
        processPending trade.Timestamp
    ),
    (fun () -> fills.ToImmutableArray())

/// Stateful component that tracks fill-based realized PnL with commissions.
/// Returns (processor, getResult) where processor accepts fills via onNext.
let trackFills (commissionPerShare: float) =
    let mutable realizedPnL = 0.0
    let mutable commissions = 0.0
    let mutable avgCost = 0.0
    let mutable currentPosition = 0
    let fills = ResizeArray<Fill>()
    (fun (fill: Fill) ->
        fills.Add fill
        let qty = fill.Quantity
        let commission = float (abs qty) * commissionPerShare
        commissions <- commissions + commission
        if currentPosition = 0 then
            avgCost <- fill.Price
            currentPosition <- qty
        elif sign qty = sign currentPosition then
            let totalQty = currentPosition + qty
            avgCost <- (avgCost * float currentPosition + fill.Price * float qty) / float totalQty
            currentPosition <- totalQty
        else
            let closingQty = min (abs qty) (abs currentPosition)
            let pnl = float (sign currentPosition) * (fill.Price - avgCost) * float closingQty
            realizedPnL <- realizedPnL + pnl - commission
            let remaining = currentPosition + qty
            if remaining = 0 then
                currentPosition <- 0
                avgCost <- 0.0
            elif sign remaining <> sign currentPosition then
                avgCost <- fill.Price
                currentPosition <- remaining
            else
                currentPosition <- remaining
            commissions <- commissions - commission),
    (fun () -> { Fills = fills.ToImmutableArray(); RealizedPnL = realizedPnL; Commissions = commissions })

type FillParams = {
    Percentile: float
    DelayMs: float
    CommissionPerShare: float
}

/// Assembles the full VWAP trading pipeline: segregateTrades -> vwapSystem ->
/// enforce chain -> trackDecisions -> fillSimulator -> trackFills.
/// Returns (addTrade, getDecisionResult, getFillResult).
let createPipeline
        (window: MarketHours)
        (volPcts: float[])
        (positionSize: float)
        (referenceVol: float option)
        (bandVol: float)
        (lossLimit: float option)
        (maxTrades: int option)
        (maxLosses: int option)
        (fillParams: FillParams option) =
    let track, getDecisionResult = trackDecisions ()
    let getPnL () = (getDecisionResult()).RealizedPnL
    let recordFill, getFillResult =
        match fillParams with
        | Some fp -> trackFills fp.CommissionPerShare
        | None -> (fun _ -> ()), (fun () -> { Fills = ImmutableArray.Empty; RealizedPnL = 0.0; Commissions = 0.0 })
    let fillSim =
        match fillParams with
        | Some fp -> fillSimulator fp.Percentile fp.DelayMs |> fun (proc, _) -> proc recordFill
        | None -> fun _ -> ()
    let mutable chain = track fillSim
    lossLimit |> Option.iter (fun limit -> chain <- enforceLossLimit getPnL limit chain)
    maxLosses |> Option.iter (fun n -> chain <- enforceLossCountLimit getPnL n chain)
    maxTrades |> Option.iter (fun n -> chain <- enforceActivityLimit n chain)
    let decide = vwapSystem (positionSize, referenceVol, bandVol)
    let segregate = segregateTrades window volPcts
    let addTrade (trade: TradeLoader.Trade) =
        segregate (fun (bar, stage, t) -> decide chain (bar, stage, t)) trade
    addTrade, getDecisionResult, getFillResult
