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
// Lazy bar-chart state (single chart, rebuilds on size change)
// =============================================================================

/// Buffers every trade fed in via RecordTrade and lazily produces a volume
/// bar chart for whatever size the caller asks for. If the size matches the
/// cached build, extends it incrementally from the trades that have arrived
/// since the last query. If the size differs, throws the old builder away
/// and rebuilds from scratch over the full buffered trade history — cheap
/// enough at the 4 estimation marks of the dynamic system, and keeps the
/// critical path (AddTrade + GetVolumeBarsAutomatic at the same size) fully
/// incremental. The per-trade VWMA snapshot is stored alongside each trade
/// so that rebuilds stamp historical bars with the VWMA they would have had
/// at completion time, matching the old parallel-builders semantics exactly.
type LazyBarState() =
    let trades = ResizeArray<Trade>()
    let vwmaAtTrade = ResizeArray<float>()
    let mutable currentSize = 0.0
    let mutable builder : VolumeBarBuilder = Unchecked.defaultof<_>
    let mutable nextIdx = 0

    member _.RecordTrade(t: Trade, vwma: float) =
        trades.Add t
        vwmaAtTrade.Add vwma

    member _.BarsFor(size: float) =
        if size <= 0.0 then ImmutableList<VolumeBar>.Empty
        elif size = currentSize then
            for i in nextIdx .. trades.Count - 1 do
                builder.AddTrade(trades.[i], vwmaAtTrade.[i])
            nextIdx <- trades.Count
            builder.Bars
        else
            let b = VolumeBarBuilder(size)
            for i in 0 .. trades.Count - 1 do
                b.AddTrade(trades.[i], vwmaAtTrade.[i])
            builder <- b
            currentSize <- size
            nextIdx <- trades.Count
            b.Bars

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
type VwapSystemEffectDynamic =
    inherit VwapSystemEffect
    abstract member GetVolumeBarsAutomatic : ImmutableList<VolumeBar>
    abstract member UpdateSelector : Trade -> unit

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

let defaultEstimationOffsets = [| 5.0; 30.0; 150.0; 750.0 |]

/// Static bar-size system: always exposes volume bars of a fixed size via
/// GetVolumeBarsAutomatic. UpdateSelector is a no-op since bar size is fixed.
let createStaticVwapSystem (fixedBarSize: float) =
    let state = LazyBarState()
    let vwma = VwmaTracker()
    { new VwapSystemEffectDynamic with
        member _.AddTrade trade =
            vwma.AddTrade trade
            state.RecordTrade(trade, vwma.Vwap)
        member _.GetVwap = vwma.Vwap
        member _.GetVolumeBarsAutomatic = state.BarsFor fixedBarSize
        member _.UpdateSelector _ = ()
    }

/// Dynamic bar-size system: selector accumulates volume across premarket
/// (via UpdateSelector) and regular hours (via AddTrade), and picks a new
/// bar size each time an estimation mark after the opening print is crossed.
/// Before the first mark GetVolumeBarsAutomatic returns the empty singleton
/// so downstream consumers naturally no-op until a size is chosen.
let createDynamicVwapSystem (estimationOffsetsSec: float[], volumePcts: float[]) =
    let state = LazyBarState()
    let vwma = VwmaTracker()
    let selector = BarSizeSelector(estimationOffsetsSec, volumePcts)
    { new VwapSystemEffectDynamic with
        member _.AddTrade trade =
            vwma.AddTrade trade
            state.RecordTrade(trade, vwma.Vwap)
            selector.Update trade
        member _.GetVwap = vwma.Vwap
        member _.GetVolumeBarsAutomatic = state.BarsFor selector.CurrentBarSize
        member _.UpdateSelector trade = selector.Update trade
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

let track_volume_bars (vwapSystem: VwapSystemEffectDynamic) =
    function
    | BeforeOpeningPrint trade -> vwapSystem.UpdateSelector trade
    | AfterOpeningPrint trade | AfterOpeningPrintAndPause trade | BeforeClosing trade | AfterClosing trade -> vwapSystem.AddTrade trade

type SimulatorState =
    | Active of price : float * position : float
    | Done

type TradingDecision = {
    Timestamp: DateTime
    Price: float
    Shares: float
}

let make_trading_decisions (vwapSystem: VwapSystemEffectDynamic, positionSize: float) on_succ =
    let mutable state = Active(0.0, 0.0)
    let mutable prevBars : ImmutableList<VolumeBar> = ImmutableList<VolumeBar>.Empty
    function
    | BeforeOpeningPrint _ | AfterOpeningPrint _ | AfterClosing _ -> () // Not intended for this node.
    | AfterOpeningPrintAndPause trade ->
        let bars = vwapSystem.GetVolumeBarsAutomatic
        if not (Object.ReferenceEquals(bars, prevBars)) && bars.Count > 0 then
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

/// Daily loss limit enforcement stage. Sits between make_trading_decisions and
/// the final decision sink. Forwards every decision through the tracker and,
/// as soon as the tracker's running realized P&L drops below -lossLimit,
/// injects a flatten at the same timestamp/price and then silently drops all
/// further decisions for the rest of the day.
let enforce_loss_limit (tracker: DecisionTracker) (lossLimit: float) on_succ =
    let mutable tripped = false
    fun (d: TradingDecision) ->
        if tripped then ()
        else
            on_succ d
            if tracker.RealizedPnL <= -lossLimit then
                tripped <- true
                if d.Shares <> 0.0 then
                    on_succ { Timestamp = d.Timestamp; Price = d.Price; Shares = 0.0 }

/// Daily activity limit enforcement stage. Forwards decisions until the
/// configured maximum is reached, then injects a same-price flatten if a
/// position is still open and drops everything after. The forced flatten is
/// not counted against the cap — maxTrades represents discretionary entries
/// and flips produced by make_trading_decisions.
let enforce_activity_limit (maxTrades: int) on_succ =
    let mutable count = 0
    let mutable tripped = false
    fun (d: TradingDecision) ->
        if tripped then ()
        else
            on_succ d
            count <- count + 1
            if count >= maxTrades then
                tripped <- true
                if d.Shares <> 0.0 then
                    on_succ { Timestamp = d.Timestamp; Price = d.Price; Shares = 0.0 }

/// Daily losing-trade limit enforcement stage. Watches the tracker's running
/// realized P&L after each decision is forwarded — whenever it drops, that
/// decision counts as a losing trade. Once the configured maximum is reached,
/// injects a same-price flatten if still in position and drops everything
/// after. Winning and flat decisions do not count against the cap: the idea
/// is that high activity is fine as long as the system is producing profits.
let enforce_loss_count_limit (tracker: DecisionTracker) (maxLosses: int) on_succ =
    let mutable losses = 0
    let mutable prevPnL = 0.0
    let mutable tripped = false
    fun (d: TradingDecision) ->
        if tripped then ()
        else
            on_succ d
            let newPnL = tracker.RealizedPnL
            if newPnL < prevPnL then
                losses <- losses + 1
            prevPnL <- newPnL
            if losses >= maxLosses then
                tripped <- true
                if d.Shares <> 0.0 then
                    on_succ { Timestamp = d.Timestamp; Price = d.Price; Shares = 0.0 }

type VwapSimulator(window : MarketHours, vwapSystem: VwapSystemEffectDynamic, positionSize: float, ?lossLimit: float, ?maxTrades: int, ?maxLosses: int) =
    let decision_tracker = DecisionTracker()
    let on_decision =
        let mutable sink : TradingDecision -> unit = decision_tracker.Add
        match lossLimit with
        | Some limit -> sink <- enforce_loss_limit decision_tracker limit sink
        | None -> ()
        match maxLosses with
        | Some n -> sink <- enforce_loss_count_limit decision_tracker n sink
        | None -> ()
        match maxTrades with
        | Some n -> sink <- enforce_activity_limit n sink
        | None -> ()
        sink
    let pipeline =
        segregate_trades window
            (splitter
                (track_volume_bars vwapSystem)
                (make_trading_decisions
                    (vwapSystem, positionSize)
                    on_decision))
            
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
