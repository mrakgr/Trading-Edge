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
    let currentTrades = ResizeArray<Trade>()
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

/// Stateful continuation that classifies trades into stages and manages
/// dynamic bar sizing. Buffers all trades with their includeInVwma flags.
/// When an estimation offset is crossed, rebuilds the vwapSystemArgsBuilder
/// with barSize = totalVolume * volPcts[i] and replays all buffered trades.
/// Emits (VwapSystemBar option, TradeStage, Trade) via onNext per trade.
let segregateTrades (window : MarketHours) (volPcts : float []) =
    let mutable trades_and_flags : (Trade * bool) ResizeArray = ResizeArray()
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

type FillSimulatorResult = {
    Fills: ImmutableArray<Fill>
}

/// Stateful continuation that simulates limit-order execution against the
/// trade stream. Builds a t-digest from each volume bar's trades to determine
/// the limit price at the given percentile. Buy orders use percentile directly
/// (lower = more aggressive), sell orders use 1 - percentile.
///
/// Fill rule: a buy order at price P is filled by any trade printed at <= P,
/// and a sell order at P by any trade at >= P. Fills are recorded at the limit
/// price, not the trade price. Partial fills are tracked.
///
/// Returns (processor, getResult).
let fillSimulator (percentile: float) =
    let mutable orderPrice = 0.0
    let mutable orderRemaining = 0
    let mutable filledPosition = 0
    let mutable lastDigest : MergingDigest option = None
    let fills = ResizeArray<Fill>()

    let computePrice (digest: MergingDigest) (isBuy: bool) =
        digest.Quantile(if isBuy then percentile else 1.0 - percentile)

    let updateOrderPrice () =
        match lastDigest with
        | Some digest when orderRemaining > 0 ->
            orderPrice <- computePrice digest true
        | Some digest when orderRemaining < 0 ->
            orderPrice <- computePrice digest false
        | _ -> ()

    (fun (bar: VwapSystemBar option, decision: TradingDecision option, trade: Trade) ->
        // 1. New bar: rebuild t-digest and recompute limit price
        match bar with
        | Some b ->
            let digest = MergingDigest(100.0)
            for t in b.Bar.Trades do
                digest.Add(t.Price, int t.Volume)
            lastDigest <- Some digest
            updateOrderPrice ()
        | None -> ()

        // 2. New decision: update target position and outstanding order
        match decision with
        | Some d ->
            orderRemaining <- d.Shares - filledPosition
            updateOrderPrice ()
        | None -> ()

        // 3. Check for fills against the current trade
        let tradeShares = int trade.Volume
        if orderRemaining > 0 && trade.Price <= orderPrice then
            let fillQty = min orderRemaining tradeShares
            fills.Add { Timestamp = trade.Timestamp; Price = orderPrice; Quantity = fillQty }
            orderRemaining <- orderRemaining - fillQty
            filledPosition <- filledPosition + fillQty
        elif orderRemaining < 0 && trade.Price >= orderPrice then
            let fillQty = -(min -orderRemaining tradeShares)
            fills.Add { Timestamp = trade.Timestamp; Price = orderPrice; Quantity = fillQty }
            orderRemaining <- orderRemaining - fillQty
            filledPosition <- filledPosition + fillQty
    ),
    (fun () -> { Fills = fills.ToImmutableArray() })

/// Assembles the full VWAP trading pipeline: segregateTrades -> vwapSystem ->
/// enforce chain -> trackDecisions. Returns (addTrade, getResult) where
/// addTrade feeds a trade through the entire pipeline and getResult returns
/// the accumulated decisions and realized PnL.
let createPipeline
        (window: MarketHours)
        (volPcts: float[])
        (positionSize: float)
        (referenceVol: float option)
        (lossLimit: float option)
        (maxTrades: int option)
        (maxLosses: int option) =
    let track, getResult = trackDecisions ()
    let getPnL () = (getResult()).RealizedPnL
    let terminal = fun (_bar: VwapSystemBar option, _decision: TradingDecision option, _trade: Trade) -> ()
    let mutable chain = track terminal
    lossLimit |> Option.iter (fun limit -> chain <- enforceLossLimit getPnL limit chain)
    maxLosses |> Option.iter (fun n -> chain <- enforceLossCountLimit getPnL n chain)
    maxTrades |> Option.iter (fun n -> chain <- enforceActivityLimit n chain)
    let decide = vwapSystem (positionSize, referenceVol)
    let segregate = segregateTrades window volPcts
    let addTrade = segregate (decide chain)
    addTrade, getResult
