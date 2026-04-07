module TradingEdge.Parsing.VwapSystem

open System
open System.Reactive.Linq
open System.Reactive.Subjects
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

/// Returns (sumOfVariances, pairCount) from adjacent-bar pairs in a bar list.
let pairwiseVarianceAccum (bars: ImmutableList<VolumeBar>) =
    let mutable sum = 0.0
    let mutable count = 0
    for i in 0 .. bars.Count - 2 do
        sum <- sum + pairwiseCombinedVariance bars.[i] bars.[i + 1]
        count <- count + 1
    sum, count

/// Computes volFactor = sqrt(average pairwise variance) from two bar sources
/// (premarket + post-open). Returns None if fewer than 1 pair total.
let computeVolFactor (premarketBars: ImmutableList<VolumeBar>) (postOpenBars: ImmutableList<VolumeBar>) =
    let s1, c1 = pairwiseVarianceAccum premarketBars
    let s2, c2 = pairwiseVarianceAccum postOpenBars
    let totalCount = c1 + c2
    if totalCount = 0 then None
    else Some (sqrt ((s1 + s2) / float totalCount))

/// Stateful continuation that enriches each VolumeBar with the expanding
/// VWMA and pairwise volatility factor. Wraps volumeBarBuilder, maintaining
/// a running VWAP sum/count and adjacent-bar variance accumulator. Emits
/// (VolumeBar, vwma, volFactor) via onNext on each completed bar.
/// When includeInVwma is false, the trade still produces volume bars (and
/// updates the vol factor) but does not affect the VWMA — useful for
/// premarket trades that should contribute to volatility estimation only.
let vwapSystemBuilder barSize =
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
            onNext (bar, vwma, volFactor)
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

let segregate_trades (window : MarketHours) (volPcts : float []) =
    let mutable trades = ImmutableList.Empty
    let mutable openingPrintTime : DateTime option = None
    let mutable vwapSystem = None
    let mutable volPctsOffset = None
    let closing_pause = -60.0
    fun onNext (trade : Trade) ->
        match openingPrintTime with
        | Some openingPrintTime ->
            if window.closeTime.AddSeconds closing_pause <= trade.Timestamp then
                BeforeClosing
            else
                let offset = (trade.Timestamp - openingPrintTime).TotalSeconds
                let i = Array.tryFindIndexBack (fun x -> offset <= x) defaultEstimationOffsets
                if volPctsOffset <> i then
                    volPctsOffset <- i
                    vwapSystem <- None
                AfterOpeningPrint
        | None ->
            if trade.Session = OpeningPrint then
                openingPrintTime <- Some trade.Timestamp
                AfterOpeningPrint
            else
                BeforeOpeningPrint
        |> fun stage ->
            trades <- trades.Add (trade, stage <> BeforeOpeningPrint)
            match vwapSystem with
            | None ->
                volPctsOffset |> Option.iter (fun volPctsOffset ->
                    let newVwapSystem = vwapSystemBuilder (sumVolumeUpTo trade.Timestamp * volPcts.[volPctsOffset])
                    let mutable prevBar = None
                    for trade in trades do newVwapSystem (fun bar -> prevBar <- Some bar) trade
                    vwapSystem <- Some newVwapSystem
                    Option.iter onNext prevBar
                )
            | Some vwapSystem ->
                vwapSystem onNext (trade, stage <> BeforeOpeningPrint)
                
// /// Creates a reactive volume bar builder. Takes a bar size and an
// /// IObservable<Trade> source. Returns an IObservable<VolumeBar> that emits
// /// each completed bar. Uses Observable.Create so that disposal propagates
// /// correctly — unsubscribing from the output unsubscribes from the input.
// /// One input trade can produce multiple output bars when it spans boundaries.
// let createVolumeBarBuilder (barSize: float) (trades: IObservable<Trade>) =
//     Observable.Create<VolumeBar>(fun (observer: IObserver<VolumeBar>) ->
//         trades.Subscribe(groupTrades barSize observer.OnNext)
//     )
//
// // =============================================================================
// // VWMA over Volume Bars (simple moving average of bar VWAPs)
// // =============================================================================
//
// /// Expanding average of bar VWAPs. Since volume bars have approximately equal
// /// volume, this is equivalent to a volume-weighted moving average.
// let createVwma (bars: IObservable<VolumeBar>) =
//     bars.Scan(
//         (Unchecked.defaultof<VolumeBar>, 0.0, 0),
//         fun (_, sum, count) bar -> (bar, sum + bar.VWAP, count + 1))
//         .Select(fun (bar, sum, count) -> (bar, sum / float count))
//
// // =============================================================================
// // Dynamic Volume Bar System (bar size selection + building + VWMA)
// // =============================================================================
//
// /// Filters a trade stream to only pass through trades at and after the
// /// opening print. Trades before the opening print are dropped.
// let filterFromOpen (trades: IObservable<Trade>) =
//     trades.SkipWhile(fun t -> t.Session <> OpeningPrint)
//
// /// Accumulates trades into a growing immutable list. Emits the updated list
// /// on every incoming trade.
// let accumTrades (trades: IObservable<Trade>) =
//     trades.Scan(ImmutableList<Trade>.Empty, fun acc trade -> acc.Add trade).Replay(1).RefCount()
//
// // =============================================================================
// // Dynamic Bar Builder (rebuilds on trade list or bar size change)
// // =============================================================================
//
// [<RequireQualifiedAccess>]
// type BarBuilderInput =
//     | Trade of Trade
//     | BarSize of float
//
// /// Takes a merged stream of trade-list updates and bar-size changes.
// /// Bar size starts as None; once set, every input triggers a full rebuild
// /// of the volume bars from the current trade list. Emits the rebuilt
// /// ImmutableList<VolumeBar> after each rebuild.
// let createDynamicBarBuilder (input: IObservable<BarBuilderInput>) =
//     Observable.Create<_>(fun (observer: IObserver<VolumeBar list>) ->
//         let mutable currentTrades = ImmutableList<Trade>.Empty
//         let mutable currentBars = []
//         let addBar bar = currentBars <- bar :: currentBars
//         let mutable barBuilder = groupTrades infinity addBar
//
//         input.Subscribe(function
//             | BarBuilderInput.Trade trade ->
//                 currentTrades <- currentTrades.Add trade
//                 let prevBars = currentBars
//                 barBuilder trade
//                 if prevBars <> currentBars then
//                     observer.OnNext currentBars
//             | BarBuilderInput.BarSize size ->
//                 currentBars <- []
//                 barBuilder <- groupTrades size addBar
//                 Seq.iter barBuilder currentTrades
//                 observer.OnNext currentBars
//         )
//     )
//
// // TODO: We'll try making the barBuilderTemplate that also calculates the VWMA.
// let createDynamicBarBuilder' barBuilderTemplate (input: IObservable<BarBuilderInput>) =
//     Observable.Create<_>(fun (observer: IObserver<_ ImmutableList>) ->
//         let mutable currentTrades = ImmutableList<Trade>.Empty
//         let mutable currentBars = ImmutableList.Empty
//         let addBar bar = currentBars <- currentBars.Add bar
//         let mutable barBuilder = barBuilderTemplate infinity addBar
//
//         input.Subscribe(function
//             | BarBuilderInput.Trade trade ->
//                 currentTrades <- currentTrades.Add trade
//                 let prevBars = currentBars
//                 barBuilder trade
//                 if prevBars <> currentBars then
//                     observer.OnNext currentBars
//             | BarBuilderInput.BarSize size ->
//                 currentBars <- ImmutableList.Empty
//                 barBuilder <- barBuilderTemplate size addBar
//                 Seq.iter barBuilder currentTrades
//                 observer.OnNext currentBars
//         )
//     )
//
//
// let createMemoizer () =
//     let mutable prevValue = Unchecked.defaultof<_>
//     let mutable prevKey = None
//
//     fun f key ->
//         match prevKey with
//         | Some prevKey when Object.ReferenceEquals(prevKey, key) -> prevValue
//         | _ ->
//             let value = f key
//             prevValue <- value
//             prevKey <- Some key
//             value
//
// let combineVwma (input: IObservable<VolumeBar list>) =
//     Observable.Create<_>(fun (observer: IObserver<(VolumeBar * float) list>) ->
//         let memoize = createMemoizer()
//         let rec loop l =
//             memoize (function
//                 | x :: xs ->
//                     let xs, (price, count) = loop xs
//                     let price, count = price + x.VWAP, count + 1
//                     (x, price / float count) :: xs, (price, count)
//                 | [] -> [], (0.0, 0)
//             ) l
//         input.Subscribe(loop >> fst >> observer.OnNext)
//     )
//
// /// Takes all trades (premarket + post-open) and produces volume bars with
// /// dynamic bar sizing. Premarket trades contribute to total volume for bar
// /// size estimation but are not included in the bars themselves. Bar size is
// /// chosen at each estimation offset as volumePcts[i] * totalVolume. When bar
// /// size changes, all post-open trades are replayed through a new builder.
// /// Emits (bars, vwma) on every bar completion or bar-size rebuild.
// let createDynamicVolumeBars (estimationOffsetsSec: float[]) (volumePcts: float[]) (trades: IObservable<Trade>) =
//     let trades_after_open = filterFromOpen trades
//     Observable.Create<ImmutableList<VolumeBar> * float>(fun (observer : IObserver<ImmutableList<VolumeBar> * float>) ->
//         // Bar size selection state
//         let mutable totalVolume = 0.0
//         let mutable openingPrintTime : DateTime option = None
//         let mutable selectorIdx = 0
//         let mutable currentBarSize = 0.0
//
//         // Bar building state
//         let postOpenTrades = ResizeArray<Trade>()
//         let mutable bars = ImmutableList<VolumeBar>.Empty
//         let mutable vwapSum = 0.0
//         let mutable barCount = 0
//         let mutable innerInput : Subject<Trade> = null
//         let mutable innerSub : IDisposable = null
//
//         let emit () =
//             let vwma = if barCount > 0 then vwapSum / float barCount else 0.0
//             observer.OnNext((bars, vwma))
//
//         let rebuildBars () =
//             if innerSub <> null then innerSub.Dispose()
//             bars <- ImmutableList<VolumeBar>.Empty
//             vwapSum <- 0.0
//             barCount <- 0
//             let input = new Subject<Trade>()
//             innerSub <- (createVolumeBarBuilder currentBarSize input).Subscribe(fun bar ->
//                 bars <- bars.Add bar
//                 vwapSum <- vwapSum + bar.VWAP
//                 barCount <- barCount + 1
//                 emit ()
//             )
//             innerInput <- input
//             for t in postOpenTrades do
//                 input.OnNext t
//
//         let outerSub = trades.Subscribe(fun trade ->
//             totalVolume <- totalVolume + trade.Volume
//
//             if openingPrintTime.IsNone && trade.Session = OpeningPrint then
//                 openingPrintTime <- Some trade.Timestamp
//
//             // Check estimation marks
//             let mutable sizeChanged = false
//             match openingPrintTime with
//             | None -> ()
//             | Some ot ->
//                 while selectorIdx < estimationOffsetsSec.Length &&
//                       trade.Timestamp >= ot.AddSeconds estimationOffsetsSec.[selectorIdx] do
//                     currentBarSize <- volumePcts.[selectorIdx] * totalVolume
//                     selectorIdx <- selectorIdx + 1
//                     sizeChanged <- true
//
//             // Buffer post-open trades for rebuilds
//             if openingPrintTime.IsSome then
//                 postOpenTrades.Add trade
//
//             // Rebuild on size change, otherwise feed to current builder
//             if sizeChanged then
//                 rebuildBars ()
//             elif currentBarSize > 0.0 && innerInput <> null then
//                 innerInput.OnNext trade
//         )
//
//         { new IDisposable with
//             member _.Dispose() =
//                 outerSub.Dispose()
//                 if innerSub <> null then innerSub.Dispose() }
//     )
//
// // =============================================================================
// // Lazy bar-chart state (single chart, rebuilds on size change)
// // =============================================================================
//
// /// Buffers every trade fed in via RecordTrade and lazily produces a volume
// /// bar chart for whatever size the caller asks for. When the size changes,
// /// creates a new reactive volume bar builder and replays all buffered trades.
// /// Subscribes to the builder's output to keep the latest bars snapshot.
// type LazyBarState() =
//     let trades = ResizeArray<Trade>()
//     let mutable currentSize = 0.0
//     let mutable input : Subject<Trade> = Unchecked.defaultof<_>
//     let mutable bars = ImmutableList<VolumeBar>.Empty
//     let mutable subscription : IDisposable = null
//     let mutable nextIdx = 0
//
//     member _.RecordTrade(t: Trade) =
//         trades.Add t
//
//     member _.BarsFor(size: float) =
//         if size <= 0.0 then ImmutableList<VolumeBar>.Empty
//         elif size = currentSize then
//             for i in nextIdx .. trades.Count - 1 do
//                 input.OnNext(trades.[i])
//             nextIdx <- trades.Count
//             bars
//         else
//             if subscription <> null then subscription.Dispose()
//             let newInput = new Subject<Trade>()
//             let output = createVolumeBarBuilder size newInput
//             bars <- ImmutableList<VolumeBar>.Empty
//             subscription <- output.Subscribe(fun bar -> bars <- bars.Add(bar))
//             input <- newInput
//             for i in 0 .. trades.Count - 1 do
//                 input.OnNext(trades.[i])
//             currentSize <- size
//             nextIdx <- trades.Count
//             bars

// =============================================================================
// VwapSystemEffect
// =============================================================================

type VwapSystemEffect =
    abstract member AddTrade : Trade -> unit
    abstract member GetVwap : float

// =============================================================================
// VwapSystemEffectDynamic — adds automatic (policy-driven) bar selection
// =============================================================================

/// Extension of VwapSystemEffect that hides the bar-size selection policy
/// behind a single accessor. Consumers (e.g. make_trading_decisions) become
/// agnostic to whether the bar size is fixed or chosen dynamically at runtime.
///
/// UpdateSelector is a premarket-only entry point: it feeds a trade into the
/// selection policy without building volume bars or moving the VWMA. This lets
/// the selector accumulate premarket volume so the post-open estimation marks
/// (5s / 30s / ...) see a denominator that reflects the full day's flow so far.
///
/// GetVolFactor returns the current volatility scaling factor computed from
/// premarket + post-open pairwise bar variance, or None if insufficient bars.
type VwapSystemEffectDynamic =
    inherit VwapSystemEffect
    abstract member GetVolumeBarsAutomatic : ImmutableList<VolumeBar>
    abstract member UpdateSelector : Trade -> unit
    abstract member GetVolFactor : float option

/// Accumulates its own running total volume from every trade fed in (via
/// AddTrade for post-open and UpdateSelector for premarket). At estimation
/// offset i after the opening print, picks barSize = volumePcts[i] * totalVolume
/// exactly — no snap, so the sweep can explore fine-grained bar sizes. Each
/// change triggers a full rebuild of the bar chart downstream, which is the
/// explicit trade-off for the granularity.
type BarSizeSelector(estimationOffsetsSec: float[], volumePcts: float[]) =
    do
        if estimationOffsetsSec.Length <> volumePcts.Length then
            invalidArg "volumePcts" "volumePcts must have the same length as estimationOffsetsSec"
    let mutable openingPrintTime : DateTime option = None
    let mutable nextIdx = 0
    let mutable currentBarSize = 0.0
    let mutable totalVolume = 0.0

    member _.CurrentBarSize = currentBarSize
    member _.TotalVolume = totalVolume

    member _.Update(trade: Trade) =
        totalVolume <- totalVolume + trade.Volume
        if openingPrintTime.IsNone && trade.Session = OpeningPrint then
            openingPrintTime <- Some trade.Timestamp
        match openingPrintTime with
        | None -> ()
        | Some ot ->
            while nextIdx < estimationOffsetsSec.Length &&
                  trade.Timestamp >= ot.AddSeconds estimationOffsetsSec.[nextIdx] do
                currentBarSize <- volumePcts.[nextIdx] * totalVolume
                nextIdx <- nextIdx + 1

/// Static bar-size system: always exposes volume bars of a fixed size via
/// GetVolumeBarsAutomatic. UpdateSelector is a no-op since bar size is fixed.
let createStaticVwapSystem (fixedBarSize: float) =
    let state = LazyBarState()
    let premarketState = LazyBarState()
    let vwma = VwmaTracker()
    { new VwapSystemEffectDynamic with
        member _.AddTrade trade =
            vwma.AddTrade trade
            state.RecordTrade(trade)
        member _.GetVwap = vwma.Vwap
        member _.GetVolumeBarsAutomatic = state.BarsFor fixedBarSize
        member _.UpdateSelector trade = premarketState.RecordTrade(trade)
        member _.GetVolFactor = computeVolFactor (premarketState.BarsFor fixedBarSize) (state.BarsFor fixedBarSize)
    }

/// Dynamic bar-size system: selector accumulates volume across premarket
/// (via UpdateSelector) and regular hours (via AddTrade), and picks a new
/// bar size each time an estimation mark after the opening print is crossed.
/// Before the first mark GetVolumeBarsAutomatic returns the empty singleton
/// so downstream consumers naturally no-op until a size is chosen.
let createDynamicVwapSystem (estimationOffsetsSec: float[], volumePcts: float[]) =
    let state = LazyBarState()
    let premarketState = LazyBarState()
    let vwma = VwmaTracker()
    let selector = BarSizeSelector(estimationOffsetsSec, volumePcts)
    { new VwapSystemEffectDynamic with
        member _.AddTrade trade =
            vwma.AddTrade trade
            state.RecordTrade(trade)
            selector.Update trade
        member _.GetVwap = vwma.Vwap
        member _.GetVolumeBarsAutomatic = state.BarsFor selector.CurrentBarSize
        member _.UpdateSelector trade =
            selector.Update trade
            premarketState.RecordTrade(trade)
        member _.GetVolFactor = computeVolFactor (premarketState.BarsFor selector.CurrentBarSize) (state.BarsFor selector.CurrentBarSize)
    }

// =============================================================================
// Trading Simulation
// =============================================================================

// let track_volume_bars (vwapSystem: VwapSystemEffectDynamic) =
//     function
//     | BeforeOpeningPrint trade -> vwapSystem.UpdateSelector trade
//     | AfterOpeningPrint trade | AfterOpeningPrintAndPause trade | BeforeClosing trade | AfterClosing trade -> vwapSystem.AddTrade trade

// type SimulatorState =
//     | Active of price : float * position : float
//     | Done

// type TradingDecision = {
//     Timestamp: DateTime
//     Price: float
//     Shares: float
// }

// let make_trading_decisions (vwapSystem: VwapSystemEffectDynamic, positionSize: float, referenceVol: float option) on_succ =
//     let mutable state = Active(0.0, 0.0)
//     let mutable prevBars : ImmutableList<VolumeBar> = ImmutableList<VolumeBar>.Empty
//     let effectiveSize () =
//         match referenceVol with
//         | None -> positionSize
//         | Some refVol ->
//             match vwapSystem.GetVolFactor with
//             | None -> positionSize
//             | Some vf -> min positionSize (positionSize * refVol / vf)
//     function
//     | BeforeOpeningPrint _ | AfterOpeningPrint _ | AfterClosing _ -> () // Not intended for this node.
//     | AfterOpeningPrintAndPause trade ->
//         let bars = vwapSystem.GetVolumeBarsAutomatic
//         if not (Object.ReferenceEquals(bars, prevBars)) && bars.Count > 0 then
//             prevBars <- bars
//             let vwma = vwapSystem.GetVwap
//             match state with
//             | Active(_, position) ->
//                 let lastBar = bars.[bars.Count - 1]
//                 let targetShares = round (effectiveSize () / trade.Price)
//                 if targetShares > 0.0 then
//                     if lastBar.VWAP >= vwma && position <= 0.0 then
//                         state <- Active(lastBar.VWAP, targetShares)
//                         on_succ { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = targetShares }
//                     elif lastBar.VWAP < vwma && position >= 0.0 then
//                         state <- Active(lastBar.VWAP, -targetShares)
//                         on_succ { Timestamp = trade.Timestamp; Price = lastBar.VWAP; Shares = -targetShares }
//             | Done -> ()
//     | BeforeClosing trade ->
//         match state with
//         | Active(_, position) when position <> 0.0 ->
//             state <- Done
//             on_succ { Timestamp = trade.Timestamp; Price = trade.Price; Shares = 0.0 }
//         | _ -> ()

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
