module TradingEdge.Vwap.Program

open System
open System.IO
open System.Collections.Immutable
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Diagnostics
open Argu
open MathNet.Numerics.Distributions
open TradeLoader
open TradeBinary

// ============================================================================
// Local VolumeBar over TradeRecord (Profile-scoped; replaces the shared one)
// ============================================================================

type VolumeBar =
    {
        VWAP: float
        StdDev: float
        Volume: float
        Trades: ImmutableArray<struct (float * float)>
    }

// ============================================================================
// Class-based pipeline (separate classes with member inline onNext)
// ============================================================================

type VolumeBarOfTrades() =
    member inline self.Process(onNext, trades: ImmutableArray<struct (float * float)>) =
        let mutable priceVolumeSum = 0.0
        let mutable priceSquaredVolumeSum = 0.0
        let mutable volumeSum = 0.0
        for struct (price, volume) in trades do
            priceVolumeSum <- priceVolumeSum + price * volume
            priceSquaredVolumeSum <- priceSquaredVolumeSum + price * price * volume
            volumeSum <- volumeSum + volume
        let vwap = priceVolumeSum / volumeSum
        let variance = priceSquaredVolumeSum / volumeSum - vwap * vwap
        onNext {
            VWAP = vwap
            StdDev = sqrt (max 0.0 variance)
            Volume = volumeSum
            Trades = trades
        }

type GroupTrades(barSize: float) =
    member val BarSize = barSize
    member val CurrentTrades = ImmutableArray.CreateBuilder<struct (float * float)>()
    member val CurrentVolumeSum = 0.0 with get, set

    member inline self.Process(onNext, trade: Trade) =
        let price = trade.Price
        let mutable remaining = float trade.Volume
        while remaining > 0.0 do
            let spaceLeft = self.BarSize - self.CurrentVolumeSum
            if remaining <= spaceLeft then
                self.CurrentTrades.Add (struct (price, remaining))
                self.CurrentVolumeSum <- self.CurrentVolumeSum + remaining
                remaining <- 0.0
            else
                if spaceLeft > 0.0 then
                    self.CurrentTrades.Add (struct (price, spaceLeft))
                    self.CurrentVolumeSum <- self.CurrentVolumeSum + spaceLeft
                    remaining <- remaining - spaceLeft
                if self.CurrentVolumeSum >= self.BarSize then
                    onNext (self.CurrentTrades.ToImmutableArray())
                    self.CurrentTrades.Clear()
                    self.CurrentVolumeSum <- 0.0

type VolumeBarBuilder(barSize: float) =
    member val BarSize = barSize
    member val Grouper = GroupTrades(barSize)
    member val BarBuilder = VolumeBarOfTrades()

    member inline self.Process(onNext, trade: Trade) =
        self.Grouper.Process((fun trades -> self.BarBuilder.Process(onNext, trades)), trade)

// ============================================================================
// Pairwise bar variance
// ============================================================================

let pairwiseRealizedVariance (a: VolumeBar) (b: VolumeBar) =
    assert (a.Volume = b.Volume)
    log (a.VWAP / b.VWAP) ** 2

type VwapSystemBar = {
    Bar : VolumeBar
    Vwma : float
    VolFactor : float
}

// ============================================================================
// VWAP system args builder
// ============================================================================

type VwapSystemArgsBuilder(barSize: float) =
    member val Inner = VolumeBarBuilder(barSize)
    member val LogVwapSum = 0.0 with get, set
    member val BarCount = 0 with get, set
    member val VarianceSum = 0.0 with get, set
    member val PairCount = 0 with get, set
    member val PrevBar = ValueOption<VolumeBar>.None with get, set

    member inline self.Process(onNext, trade: Trade, includeInVwma: bool, includeInVol: bool) =
        self.Inner.Process(
            (fun bar ->
                if includeInVwma then
                    self.LogVwapSum <- self.LogVwapSum + log bar.VWAP
                    self.BarCount <- self.BarCount + 1
                let vwma = if self.BarCount > 0 then exp (self.LogVwapSum / float self.BarCount) else 0.0
                match self.PrevBar with
                | ValueSome prev ->
                    if includeInVol then
                        self.VarianceSum <- self.VarianceSum + pairwiseRealizedVariance prev bar
                        self.PairCount <- self.PairCount + 1
                | ValueNone -> ()
                self.PrevBar <- ValueSome bar
                let volFactor = if self.PairCount > 0 then exp (sqrt (self.VarianceSum / float self.PairCount)) - 1.0 else 0.0
                onNext {
                    Bar = bar
                    Vwma = vwma
                    VolFactor = volFactor
                }
            ),
            trade
        )

// ============================================================================
// VWMA accumulator (rolling over last N bars, or cumulative if window=Int32.MaxValue)
// ============================================================================

type VwmaAccumulator(window: int) =
    member val Window = window
    member val LogVwapSum = 0.0 with get, set
    member val Queue = System.Collections.Generic.Queue<float>()

    member self.Update(vwap: float) =
        let lv = log vwap
        self.Queue.Enqueue lv
        self.LogVwapSum <- self.LogVwapSum + lv
        if self.Queue.Count > self.Window then
            self.LogVwapSum <- self.LogVwapSum - self.Queue.Dequeue()

    member self.Value =
        if self.Queue.Count = 0 then 0.0
        else exp (self.LogVwapSum / float self.Queue.Count)

// ============================================================================
// ORB system bar + args builder (rolling-64 VWMA + session VWMA + range)
// ============================================================================

type OrbSystemBar = {
    Bar : VolumeBar
    Vwma64 : float
    VwmaSession : float
    VolFactor : float
    RangeHigh : float
    RangeLow : float
}

type OrbSystemArgsBuilder(barSize: float) =
    member val Inner = VolumeBarBuilder(barSize)
    member val Vwma64 = VwmaAccumulator(64)
    member val VwmaSession = VwmaAccumulator(Int32.MaxValue)
    member val VarianceSum = 0.0 with get, set
    member val PairCount = 0 with get, set
    member val PrevBar = ValueOption<VolumeBar>.None with get, set
    member val RangeHigh = Double.NegativeInfinity with get, set
    member val RangeLow = Double.PositiveInfinity with get, set

    member inline self.Process(onNext, trade: Trade, includeInVwma: bool, includeInVol: bool, includeInRange: bool) =
        self.Inner.Process(
            (fun bar ->
                if includeInVwma then
                    self.Vwma64.Update bar.VWAP
                    self.VwmaSession.Update bar.VWAP
                match self.PrevBar with
                | ValueSome prev ->
                    if includeInVol then
                        self.VarianceSum <- self.VarianceSum + pairwiseRealizedVariance prev bar
                        self.PairCount <- self.PairCount + 1
                | ValueNone -> ()
                self.PrevBar <- ValueSome bar
                if includeInRange then
                    if bar.VWAP > self.RangeHigh then self.RangeHigh <- bar.VWAP
                    if bar.VWAP < self.RangeLow then self.RangeLow <- bar.VWAP
                let volFactor = if self.PairCount > 0 then exp (sqrt (self.VarianceSum / float self.PairCount)) - 1.0 else 0.0
                onNext {
                    Bar = bar
                    Vwma64 = self.Vwma64.Value
                    VwmaSession = self.VwmaSession.Value
                    VolFactor = volFactor
                    RangeHigh = self.RangeHigh
                    RangeLow = self.RangeLow
                }
            ),
            trade
        )

// ============================================================================
// Trade segregator
// ============================================================================

[<Struct>]
type TradeStage =
    | EarlyPremarket
    | LatePremarket
    | AfterOpeningPrint
    | BeforeClosing

/// Delay between the opening print and when the system is allowed to trade.
/// 60s gives the ORB opening range its window (8:30 ET -> op+60s) before entries begin.
let entryDelaySeconds = 60.0

type SegregateTrades(barSize: float, baseTime) =
    member val ArgsBuilder = OrbSystemArgsBuilder(barSize)
    member val ClosingPause = -60.0
    member val BaseTime = baseTime : DateTime
    member val OpeningPrintIdx = ValueNone : int voption with get, set
    member val OpeningPrintTs : DateTime voption = ValueNone with get, set
    member val OpenTime = baseTime.AddHours(9.5)
    member val CloseTime = if Timezone.early_closes.Contains(DateOnly.FromDateTime baseTime) then baseTime.AddHours(13) else baseTime.AddHours(16)

    member inline self.Timestamp(trade: Trade) = self.BaseTime.AddTicks trade.TicksFromBase

    member self.TradeStage(trade: Trade, index: int) =
        let ts = self.Timestamp trade
        if self.OpeningPrintIdx = ValueSome index then
            self.OpeningPrintTs <- ValueSome ts
        if self.OpenTime <= ts then
            if self.CloseTime.AddSeconds self.ClosingPause <= ts then
                BeforeClosing
            else
                // Gate entry until entryDelaySeconds after the opening print.
                let readyTime =
                    match self.OpeningPrintTs with
                    | ValueSome op -> op.AddSeconds entryDelaySeconds
                    | ValueNone -> self.OpenTime
                if ts >= readyTime then AfterOpeningPrint
                else LatePremarket
        else
            if self.OpeningPrintIdx = ValueSome index then
                AfterOpeningPrint
            elif self.OpenTime.AddHours(-1.0) <= ts then
                LatePremarket
            else
                EarlyPremarket

    member self.CalculateFlags(stage: TradeStage) =
        let includeInVwma =
            match stage with
            | AfterOpeningPrint | BeforeClosing -> true
            | _ -> false
        let includeInVol =
            match stage with
            | LatePremarket | AfterOpeningPrint | BeforeClosing -> true
            | _ -> false
        let includeInRange =
            match stage with
            | LatePremarket -> true
            | _ -> false
        struct (includeInVwma, includeInVol, includeInRange)


    member inline self.Process(onNext, trade: Trade, index: int) =
        let stage = self.TradeStage(trade, index)
        let mutable lastBar = ValueNone
        let struct (inclVwma, inclVol, inclRange) = self.CalculateFlags stage
        self.ArgsBuilder.Process((fun bar -> lastBar <- ValueSome bar), trade, inclVwma, inclVol, inclRange)
        onNext (lastBar, stage, trade)

/// Compute the RTH volume target (9:30 ET → scheduled close) from the day's
/// filtered trades, then divide by `divisor` to get the per-bar target.
let computeBarSize (header: DayHeader) (trades: Trade[]) (divisor: float) : float =
    let baseTime = DateTime header.BaseTicks
    let openTicks = baseTime.AddHours(9.5).Ticks
    let closeTicks =
        if Timezone.early_closes.Contains(DateOnly.FromDateTime baseTime) then
            baseTime.AddHours(13).Ticks
        else
            baseTime.AddHours(16).Ticks
    let mutable total = 0.0
    for t in trades do
        let ts = baseTime.AddTicks(t.TicksFromBase).Ticks
        if ts >= openTicks && ts <= closeTicks then
            total <- total + float t.Volume
    total / divisor

// ============================================================================
// VWAP trading system (decisions from bars)
// ============================================================================

type TradingDecision = {
    Timestamp: DateTime
    Price: float
    Shares: int
    BarSize: float
}

[<Struct>]
type SimulatorState =
    | Active of price: float * position: int * stop: float
    | Done

[<Struct>]
type StopMode =
    /// Stop at the 64-bar VWMA at entry time.
    | StopAtVwma64
    /// Stop `stopVol * price * VolFactor` below entry price.
    | StopAtVol of stopVol: float
    /// Stop at the opposite end of the opening range (b.RangeLow for longs).
    | StopAtRange

type OrbSystem(positionSize: float, referenceVol: float voption, minVwmaDist: float, stopMode: StopMode) =
    member val PositionSize = positionSize
    member val ReferenceVol = referenceVol
    /// Minimum required distance from the 64-bar VWMA at entry, measured in vol units
    /// (`(price - vwma) / (price * VolFactor)`). `0.0` disables the filter.
    member val MinVwmaDist = minVwmaDist
    member val StopMode = stopMode
    member val State = Active(0.0, 0, 0.0) with get, set

    member inline self.EffectiveSize(vf: float) =
        match self.ReferenceVol with
        | ValueNone -> self.PositionSize
        | ValueSome refVol -> min self.PositionSize (self.PositionSize * refVol / vf)

    member inline self.Process(onNext, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade, tradeTs: DateTime) =
        let mutable decision = ValueNone
        match stage with
        | EarlyPremarket | LatePremarket -> ()
        | AfterOpeningPrint ->
            match bar with
            | ValueSome b ->
                match self.State with
                | Active(_, position, stop) ->
                    let lastBar = b.Bar
                    let price = lastBar.VWAP
                    let targetShares = round (self.EffectiveSize b.VolFactor / trade.Price) |> int
                    let barSize = lastBar.Volume
                    if position = 0 then
                        if targetShares > 0 then
                            // Longs only — shorts lose money after borrow/execution costs on this dataset.
                            let vwmaDist =
                                if b.VolFactor > 0.0 then (price - b.Vwma64) / (price * b.VolFactor)
                                else 0.0
                            if price > b.RangeHigh && vwmaDist >= self.MinVwmaDist then
                                let stopLevel =
                                    match self.StopMode with
                                    | StopAtVwma64 -> b.Vwma64
                                    | StopAtVol stopVol -> price - stopVol * price * b.VolFactor
                                    | StopAtRange -> b.RangeLow
                                self.State <- Active(price, targetShares, stopLevel)
                                decision <- ValueSome { Timestamp = tradeTs; Price = price; Shares = targetShares; BarSize = barSize }
                    elif position > 0 then
                        if price < stop then
                            self.State <- Active(price, 0, 0.0)
                            decision <- ValueSome { Timestamp = tradeTs; Price = price; Shares = 0; BarSize = barSize }
                    else
                        if price > stop then
                            self.State <- Active(price, 0, 0.0)
                            decision <- ValueSome { Timestamp = tradeTs; Price = price; Shares = 0; BarSize = barSize }
                | Done -> ()
            | ValueNone -> ()
        | BeforeClosing ->
            match self.State with
            | Active(_, position, _) when position <> 0 ->
                self.State <- Done
                decision <- ValueSome { Timestamp = tradeTs; Price = trade.Price; Shares = 0; BarSize = 0.0 }
            | _ -> ()
        onNext (decision, bar, stage, trade)

// ============================================================================
// Decision tracker (running realized PnL)
// ============================================================================

type TrackDecisions() =
    member val Decisions = ResizeArray<TradingDecision>(32)
    member val RealizedPnL = 0.0 with get, set

    member inline self.Process(onNext, decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
        match decision with
        | ValueSome d ->
            if self.Decisions.Count > 0 then
                let prev = self.Decisions.[self.Decisions.Count - 1]
                self.RealizedPnL <- self.RealizedPnL + (d.Price - prev.Price) * float prev.Shares
            self.Decisions.Add d
        | ValueNone -> ()
        onNext (decision, bar, stage, trade)

// ============================================================================
// Daily loss limit enforcement
// ============================================================================

type EnforceLossLimit(getPnL: unit -> float, lossLimit: float) =
    member val GetPnL = getPnL
    member val LossLimit = lossLimit
    member val Tripped = false with get, set

    member inline self.Process(onNext, decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
        let decision =
            match decision with
            | ValueSome d ->
                if self.Tripped then ValueNone
                else
                    if self.GetPnL() <= -self.LossLimit then
                        self.Tripped <- true
                        ValueSome { d with Shares = 0 }
                    else ValueSome d
            | ValueNone -> ValueNone
        onNext (decision, bar, stage, trade)

// ============================================================================
// Fill struct + fill tracker
// ============================================================================

[<Struct>]
type Fill = {
    Timestamp: DateTime
    Price: float
    Quantity: int
}

type TrackFills(commissionPerShare: float) =
    member val CommissionPerShare = commissionPerShare
    member val Fills = ResizeArray<Fill>(256)
    /// Excludes comissions.
    member val GrossPnL = 0.0 with get, set
    member val Commissions = 0.0 with get, set
    member self.NetPnL = self.GrossPnL - self.Commissions
    member val AvgCost = 0.0 with get, set
    member val CurrentPosition = 0 with get, set

    member inline self.Process(onNext, fill: Fill) =
        self.Fills.Add fill
        let qty = fill.Quantity
        self.Commissions <- self.Commissions + float (abs qty) * self.CommissionPerShare
        if self.CurrentPosition = 0 then
            self.AvgCost <- fill.Price
            self.CurrentPosition <- qty
        elif sign qty = sign self.CurrentPosition then
            let totalQty = self.CurrentPosition + qty
            self.AvgCost <- (self.AvgCost * float self.CurrentPosition + fill.Price * float qty) / float totalQty
            self.CurrentPosition <- totalQty
        else
            let closingQty = min (abs qty) (abs self.CurrentPosition)
            let pnl = float (sign self.CurrentPosition) * (fill.Price - self.AvgCost) * float closingQty
            self.GrossPnL <- self.GrossPnL + pnl
            let remaining = self.CurrentPosition + qty
            if remaining = 0 then
                self.CurrentPosition <- 0
                self.AvgCost <- 0.0
            elif sign remaining <> sign self.CurrentPosition then
                self.AvgCost <- fill.Price
                self.CurrentPosition <- remaining
            else
                self.CurrentPosition <- remaining
        onNext fill

// ============================================================================
// Fill simulator
// ============================================================================

type LimitOrder = {
    Prices: struct (float * DateTime) list
    Quantities: struct (int * DateTime) list
    FilledQuantity: int
    CancellationTime: DateTime voption
}

let inline tryFindV ([<InlineIfLambda>] pred: 'a -> bool) (xs: 'a list) : 'a voption =
    let mutable cur = xs
    let mutable result = ValueNone
    while result.IsNone && not cur.IsEmpty do
        let h = cur.Head
        if pred h then result <- ValueSome h
        else cur <- cur.Tail
    result

/// Weighted average price of trades in the quantile band [q_lo, q_hi].
/// Sorts the input by (price, volume), walks cumulative weight and takes
/// fractional overlaps at band edges.
let inline bandPrice (prices_and_volumes: ImmutableArray<struct (float * float)>) (q_lo: float) (q_hi: float) : float =
    let n = prices_and_volumes.Length
    let inline structFst struct (x, _) = x
    if n = 0 then nan
    elif n = 1 then prices_and_volumes.[0] |> structFst
    else
        // AsArray exposes the backing store so Array.sort can work against it
        // without copying. Callers must not mutate the returned array elsewhere.
        let prices_and_volumes = Array.sort (ImmutableCollectionsMarshal.AsArray prices_and_volumes)
        let totalWeight = prices_and_volumes |> Array.sumBy (fun struct (_, b) -> b)
        let w_lo = q_lo * totalWeight
        let w_hi = q_hi * totalWeight
        if w_lo < w_hi then
            let mutable cumWeight = 0.0
            let mutable sumPriceWeight = 0.0
            let mutable sumWeight = 0.0
            for i in 0 .. n - 1 do
                let struct (price, volume) = prices_and_volumes.[i]
                let nextCum = cumWeight + volume
                let overlapStart = max cumWeight w_lo
                let overlapEnd = min nextCum w_hi
                if overlapEnd > overlapStart then
                    let fraction = overlapEnd - overlapStart
                    sumPriceWeight <- sumPriceWeight + price * fraction
                    sumWeight <- sumWeight + fraction
                cumWeight <- nextCum
            if sumPriceWeight > 0.0 then sumPriceWeight / sumWeight else failwith "sumPriceWeight > 0.0 check failed"
        elif w_lo = w_hi then
            let target = w_lo
            let mutable cum = 0.0
            let mutable i = 0
            let mutable found = ValueNone
            while i < n && found.IsNone do
                let struct (price, volume) = prices_and_volumes.[i]
                cum <- cum + volume
                if cum >= target then
                    found <- ValueSome struct (price, volume)
                i <- i + 1
            found.Value |> structFst
        else failwith "w_lo shouldn't be higher than w_hi."

type FillSimulator(percentile: float, delayMs: float, rejectionRate: float, rngOpt: Random voption, baseTime : DateTime) =
    let compression = 100.0

    member val Percentile = percentile
    member val Delay = TimeSpan.FromMilliseconds delayMs
    member val RejectionRate = rejectionRate
    member val Rng =
        match rngOpt with
        | ValueSome r -> r
        | ValueNone -> Random(12345)

    member val BaseTime = baseTime : DateTime
    member val Fills = ResizeArray<Fill>(256)
    member val BuyOrder : LimitOrder voption = ValueNone with get, set
    member val SellOrder : LimitOrder voption = ValueNone with get, set
    member val TargetPosition = 0 with get, set
    member val FilledPosition = 0 with get, set
    member val LastPricesAndVolumes : ImmutableArray<struct (float * float)> = ImmutableArray.Empty with get, set
    member val PendingTarget : int voption = ValueNone with get, set

    member inline self.Timestamp(trade: Trade) = self.BaseTime.AddTicks trade.TicksFromBase

    member self.ComputePrice(isBuy: bool) =
        let q = max 0.0 (min 1.0 (if isBuy then percentile else 1.0 - percentile))
        let hw = q * (1.0 - q) / compression
        bandPrice self.LastPricesAndVolumes (q - hw) (q + hw)

    member self.PlaceOrder(now: DateTime) =
        let needed = self.TargetPosition - self.FilledPosition
        let activationTime = now + self.Delay
        if self.LastPricesAndVolumes.Length > 0 && needed > 0 then
            assert self.SellOrder.IsNone
            self.BuyOrder <- ValueSome {
                Prices = [struct (self.ComputePrice true, activationTime)]
                Quantities = [struct (needed, activationTime)]
                FilledQuantity = 0
                CancellationTime = ValueNone
            }
        elif self.LastPricesAndVolumes.Length > 0 && needed < 0 then
            assert self.BuyOrder.IsNone
            self.SellOrder <- ValueSome {
                Prices = [struct (self.ComputePrice false, activationTime)]
                Quantities = [struct (-needed, activationTime)]
                FilledQuantity = 0
                CancellationTime = ValueNone
            }

    member self.CancelBuyOrder(now: DateTime) =
        match self.BuyOrder with
        | ValueSome order when order.CancellationTime.IsNone ->
            self.BuyOrder <- ValueSome { order with CancellationTime = ValueSome (now + self.Delay) }
        | _ -> ()

    member self.CancelSellOrder(now: DateTime) =
        match self.SellOrder with
        | ValueSome order when order.CancellationTime.IsNone ->
            self.SellOrder <- ValueSome { order with CancellationTime = ValueSome (now + self.Delay) }
        | _ -> ()

    member self.IsCancelled(now: DateTime, order: LimitOrder) =
        match order.CancellationTime with
        | ValueSome cancelTime -> now >= cancelTime
        | ValueNone -> false

    member self.TryFillBuy(trade: Trade, now: DateTime) =
        match self.BuyOrder with
        | ValueSome order when not (self.IsCancelled(now, order)) ->
            let priceOpt = order.Prices |> tryFindV (fun struct (_, at) -> now >= at)
            let qtyOpt = order.Quantities |> tryFindV (fun struct (_, at) -> now >= at)
            match priceOpt, qtyOpt with
            | ValueSome (struct (price, _)), ValueSome (struct (qty, _)) when trade.Price <= price ->
                let remaining = qty - order.FilledQuantity
                if remaining > 0 && (self.RejectionRate = 0.0 || self.Rng.NextDouble() >= self.RejectionRate) then
                    let fillQty = min remaining (int trade.Volume)
                    if fillQty > 0 then
                        self.Fills.Add { Timestamp = now; Price = price; Quantity = fillQty }
                        self.FilledPosition <- self.FilledPosition + fillQty
                        self.BuyOrder <- ValueSome { order with FilledQuantity = order.FilledQuantity + fillQty }
            | _ -> ()
        | _ -> ()

    member self.TryFillSell(trade: Trade, now: DateTime) =
        match self.SellOrder with
        | ValueSome order when not (self.IsCancelled(now, order)) ->
            let priceOpt = order.Prices |> tryFindV (fun struct (_, at) -> now >= at)
            let qtyOpt = order.Quantities |> tryFindV (fun struct (_, at) -> now >= at)
            match priceOpt, qtyOpt with
            | ValueSome (struct (price, _)), ValueSome (struct (qty, _)) when trade.Price >= price ->
                let remaining = qty - order.FilledQuantity
                if remaining > 0 && (self.RejectionRate = 0.0 || self.Rng.NextDouble() >= self.RejectionRate) then
                    let fillQty = min remaining (int trade.Volume)
                    if fillQty > 0 then
                        self.Fills.Add { Timestamp = now; Price = price; Quantity = -fillQty }
                        self.FilledPosition <- self.FilledPosition - fillQty
                        self.SellOrder <- ValueSome { order with FilledQuantity = order.FilledQuantity + fillQty }
            | _ -> ()
        | _ -> ()

    member self.ProcessCancellations(now: DateTime) =
        match self.BuyOrder with
        | ValueSome order ->
            match order.CancellationTime with
            | ValueSome cancelTime when now >= cancelTime -> self.BuyOrder <- ValueNone
            | _ -> ()
        | ValueNone -> ()
        match self.SellOrder with
        | ValueSome order ->
            match order.CancellationTime with
            | ValueSome cancelTime when now >= cancelTime -> self.SellOrder <- ValueNone
            | _ -> ()
        | ValueNone -> ()
        match self.BuyOrder with
        | ValueSome order ->
            let struct (newestQty, activationTime) = order.Quantities.Head
            if now >= activationTime && newestQty <= order.FilledQuantity then
                self.BuyOrder <- ValueNone
        | ValueNone -> ()
        match self.SellOrder with
        | ValueSome order ->
            let struct (newestQty, activationTime) = order.Quantities.Head
            if now >= activationTime && newestQty <= order.FilledQuantity then
                self.SellOrder <- ValueNone
        | ValueNone -> ()

    member self.ProcessPending(now: DateTime) =
        match self.PendingTarget with
        | ValueSome _ when self.BuyOrder.IsNone && self.SellOrder.IsNone ->
            self.PendingTarget <- ValueNone
            self.PlaceOrder now
        | _ -> ()

    member inline self.Process(onNext, decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
        let now = self.Timestamp trade

        // 1. Process cancellations
        self.ProcessCancellations now

        // 2. New bar: build price/volume array and reprice active orders
        match bar with
        | ValueSome b ->
            self.LastPricesAndVolumes <- b.Bar.Trades
            match self.BuyOrder with
            | ValueSome order when order.CancellationTime.IsNone ->
                let newPrice = struct (self.ComputePrice true, now + self.Delay)
                self.BuyOrder <- ValueSome { order with Prices = newPrice :: order.Prices }
            | _ -> ()
            match self.SellOrder with
            | ValueSome order when order.CancellationTime.IsNone ->
                let newPrice = struct (self.ComputePrice false, now + self.Delay)
                self.SellOrder <- ValueSome { order with Prices = newPrice :: order.Prices }
            | _ -> ()
        | ValueNone -> ()

        // 3. New decision: flip / adjust / cancel
        match decision with
        | ValueSome d ->
            self.TargetPosition <- d.Shares
            let needed = self.TargetPosition - self.FilledPosition
            let activationTime = now + self.Delay
            if needed > 0 then
                if self.SellOrder.IsSome then
                    self.CancelSellOrder now
                    self.PendingTarget <- ValueSome self.TargetPosition
                elif self.BuyOrder.IsNone then
                    self.PendingTarget <- ValueSome self.TargetPosition
                else
                    match self.BuyOrder with
                    | ValueSome order when order.CancellationTime.IsNone ->
                        let newQty = struct (needed, activationTime)
                        self.BuyOrder <- ValueSome { order with Quantities = newQty :: order.Quantities }
                    | _ -> ()
            elif needed < 0 then
                if self.BuyOrder.IsSome then
                    self.CancelBuyOrder now
                    self.PendingTarget <- ValueSome self.TargetPosition
                elif self.SellOrder.IsNone then
                    self.PendingTarget <- ValueSome self.TargetPosition
                else
                    match self.SellOrder with
                    | ValueSome order when order.CancellationTime.IsNone ->
                        let newQty = struct (-needed, activationTime)
                        self.SellOrder <- ValueSome { order with Quantities = newQty :: order.Quantities }
                    | _ -> ()
            else
                self.CancelBuyOrder now
                self.CancelSellOrder now
        | ValueNone -> ()

        // 4. Check fills — emit each new fill via onNext
        let prevFillCount = self.Fills.Count
        self.TryFillBuy(trade, now)
        self.TryFillSell(trade, now)
        for i in prevFillCount .. self.Fills.Count - 1 do
            onNext self.Fills.[i]

        // 5. Process pending orders
        self.ProcessPending now

// ============================================================================
// Round-trip extraction (mirrors scripts/fill_breakdown.fsx)
// ============================================================================

type RoundTrip = {
    PnL: float
    Commission: float
    Side: int  // +1 long, -1 short
    EntryPrice: float
    ExitPrice: float
    Shares: int
}

let extractRoundTrips (fills: ResizeArray<Fill>) (commissionPerShare: float) =
    let trips = ResizeArray<RoundTrip>()
    let mutable position = 0
    let mutable avgCost = 0.0
    let mutable entryCommission = 0.0

    for f in fills do
        let qty = f.Quantity
        let commission = float (abs qty) * commissionPerShare

        if position = 0 then
            avgCost <- f.Price
            position <- qty
            entryCommission <- commission
        elif sign qty = sign position then
            let totalQty = position + qty
            avgCost <- (avgCost * float (abs position) + f.Price * float (abs qty)) / float (abs totalQty)
            position <- totalQty
            entryCommission <- entryCommission + commission
        else
            let closingQty = min (abs qty) (abs position)
            let pnl = float (sign position) * (f.Price - avgCost) * float closingQty
            let totalCommission = entryCommission * (float closingQty / float (abs position)) + commission
            trips.Add {
                PnL = pnl - totalCommission
                Commission = totalCommission
                Side = sign position
                EntryPrice = avgCost
                ExitPrice = f.Price
                Shares = closingQty
            }
            let remaining = position + qty
            if remaining = 0 then
                position <- 0
                avgCost <- 0.0
                entryCommission <- 0.0
            elif sign remaining <> sign position then
                entryCommission <- float (abs remaining) * commissionPerShare
                avgCost <- f.Price
                position <- remaining
            else
                entryCommission <- entryCommission * (1.0 - float closingQty / float (abs position))
                position <- remaining
    trips.ToArray()

// ============================================================================
// Benchmark harness
// ============================================================================

type SinkContext = {
    mutable BarCount: int
    mutable DecisionCount: int
    mutable FillCount: int
    mutable Sink : float
}

type DayResult = {
    Ticker: string
    Date: string
    DayPnL: float
    RoundTrips: RoundTrip[]
    TotalCommission: float
    NumFills: int
    AvgPositionSize: float
}

type DayData = { Date: string; Header: DayHeader; Ticker: string; Trades: Trade[] }

/// Divide the day's 9:30 ET → close filtered volume by this to get the bar size.
/// Uses lookahead (whole-day volume known at t=0); intentional — we're
/// measuring whether a VWAP-reversion system is viable in principle before
/// building an online estimator for it.
let barDivisor = 3000.0

let positionSize = 30000.0
let referenceVol = ValueSome 5.82e-4
let commissionPerShare = 0.0035
let fillPercentile = 0.05
let fillDelayMs = 100.0
let fillRejectionRate = 0.30
/// Default VWMA-distance filter (vol units). 0.0 = unfiltered.
let minVwmaDist = 0.0
/// Default stop mode.
let stopMode = StopAtVol 3.0

let configureWith (header: DayHeader) (trades: Trade[]) (divisor: float) fillPercentile minVwmaDist stopMode =
    let barSize = computeBarSize header trades divisor
    let seg = SegregateTrades(barSize, DateTime header.BaseTicks)
    seg.OpeningPrintIdx <- header.OpeningPrintIndex
    let vs = OrbSystem(positionSize, referenceVol, minVwmaDist, stopMode)
    let td = TrackDecisions()
    let tf = TrackFills(commissionPerShare)
    let ell = EnforceLossLimit((fun () -> tf.NetPnL), infinity)
    let fs = FillSimulator(fillPercentile, fillDelayMs, fillRejectionRate, ValueNone, DateTime header.BaseTicks)
    seg, vs, td, ell, fs, tf

let configure (header: DayHeader) (trades: Trade[]) bars fillPercentile minVwmaDist stopMode =
    configureWith header trades bars fillPercentile minVwmaDist stopMode

let loadDayData (jsonPath: string) =
    let entries = Convert.loadPlays jsonPath

    printfn "Loading %d days from %s ..." entries.Length jsonPath
    let swLoad = Stopwatch.StartNew()
    let dayData : DayData[] =
        [| for ticker, date in entries do
            let header, trades = loadDay {Directory = "data/trades_bin"; Ticker = ticker; Date = date}
            if header.OpeningPrintIndex <> ValueNone then
                yield { Ticker = ticker; Date = date
                        Header = header; Trades = trades } |]
    swLoad.Stop()
    let totalTrades = dayData |> Array.sumBy (fun d -> int64 d.Trades.Length)
    printfn "Loaded %d days (%s trades) in %.3fs\n" dayData.Length (totalTrades.ToString("N0")) swLoad.Elapsed.TotalSeconds
    dayData, totalTrades

/// Run the full pipeline for one day with the given pcts and return NetPnL.
[<Struct>]
type DaySummary = {
    NetPnL: float
    GrossWins: float
    GrossLosses: float   // absolute value
}

let evaluateDay (d: DayData) (divisor: float) fillPercentile (minVwmaDist: float) (stopMode: StopMode) : DaySummary =
    let seg, vs, td, ell, fs, tf = configureWith d.Header d.Trades divisor fillPercentile minVwmaDist stopMode
    let onFillSink (_: Fill) = ()
    let onFill (fill: Fill) = tf.Process(onFillSink, fill)
    let onTracked (decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
        fs.Process(onFill, decision, bar, stage, trade)
    for i in 0 .. d.Trades.Length - 1 do
        seg.Process(
            (fun (bar, stage, trade) ->
                vs.Process(
                    (fun (decision, bar, stage, trade) ->
                        ell.Process(
                            (fun (decision, bar, stage, trade) ->
                                td.Process(onTracked, decision, bar, stage, trade)),
                            decision, bar, stage, trade)),
                    bar, stage, trade, seg.Timestamp trade)),
            d.Trades.[i], i)
    let trips = extractRoundTrips tf.Fills commissionPerShare
    let mutable gw = 0.0
    let mutable gl = 0.0
    for rt in trips do
        if rt.PnL > 0.0 then gw <- gw + rt.PnL
        elif rt.PnL < 0.0 then gl <- gl + (-rt.PnL)
    { NetPnL = tf.NetPnL; GrossWins = gw; GrossLosses = gl }

/// Build N values log-spaced in [lo, hi].
let logSpaced (n: int) (lo: float) (hi: float) : float[] =
    if n <= 1 then [| lo |]
    else
        let logLo = log lo
        let logHi = log hi
        [| for k in 0 .. n - 1 ->
            exp (logLo + (logHi - logLo) * float k / float (n - 1)) |]

let stopModeLabel (sm: StopMode) =
    match sm with
    | StopAtVwma64 -> "vwma64"
    | StopAtVol v -> sprintf "vol=%.2f" v
    | StopAtRange -> "rangeLo"

/// 2D sweep over (minVwmaDist, stopMode) at a fixed bar divisor.
let runParallelSweep (dayData: DayData[]) (minDists: float[]) (stopModes: StopMode[]) =
    let nm = minDists.Length
    let ns = stopModes.Length
    let nConfigs = nm * ns
    printfn "=== Parallel sweep: %d minDist x %d stopMode x %d days = %d tasks (cores=%d) ==="
        nm ns dayData.Length (nConfigs * dayData.Length) Environment.ProcessorCount

    evaluateDay dayData.[0] barDivisor fillPercentile minDists.[0] stopModes.[0] |> ignore

    let parResults = Array2D.zeroCreate<DaySummary> nConfigs dayData.Length
    let swPar = Stopwatch.StartNew()
    for di in 0 .. dayData.Length - 1 do
        let d = dayData.[di]
        System.Threading.Tasks.Parallel.For(0, nConfigs, fun ci ->
            let mi = ci / ns
            let si = ci % ns
            parResults.[ci, di] <- evaluateDay d barDivisor fillPercentile minDists.[mi] stopModes.[si]
        ) |> ignore
    swPar.Stop()
    printfn "  Parallel:   %.3fs" swPar.Elapsed.TotalSeconds

    printfn "  Per-config results:"
    printfn "    %-4s %10s %10s  %14s  %14s  %14s  %8s" "#" "minDist" "stopMode" "NetPnL" "grossWins" "grossLosses" "PF"
    for ci in 0 .. nConfigs - 1 do
        let mi = ci / ns
        let si = ci % ns
        let mutable net = 0.0
        let mutable gw = 0.0
        let mutable gl = 0.0
        for di in 0 .. dayData.Length - 1 do
            let r = parResults.[ci, di]
            net <- net + r.NetPnL
            gw <- gw + r.GrossWins
            gl <- gl + r.GrossLosses
        let pf = if gl > 0.0 then gw / gl else infinity
        printfn "    [%2d] %10.3f %10s  $%13.2f  $%13.2f  $%13.2f  %8.3f"
            ci minDists.[mi] (stopModeLabel stopModes.[si]) net gw gl pf

    // PF matrix: rows = minDist, cols = stopMode.
    printfn ""
    printfn "  PF matrix (rows=minDist, cols=stopMode):"
    printf "    %10s" ""
    for si in 0 .. ns - 1 do printf " %10s" (stopModeLabel stopModes.[si])
    printfn ""
    for mi in 0 .. nm - 1 do
        printf "    %10.3f" minDists.[mi]
        for si in 0 .. ns - 1 do
            let ci = mi * ns + si
            let mutable gw = 0.0
            let mutable gl = 0.0
            for di in 0 .. dayData.Length - 1 do
                let r = parResults.[ci, di]
                gw <- gw + r.GrossWins
                gl <- gl + r.GrossLosses
            let pf = if gl > 0.0 then gw / gl else infinity
            printf " %10.3f" pf
        printfn ""

let runBenchmark (dayData: DayData[]) (totalTrades: int64) =
    let bench(d : DayData, ctx : SinkContext) =
        let seg, vs, td, ell, fs, tf = configure d.Header d.Trades barDivisor fillPercentile minVwmaDist stopMode
        let inline onFillSink (fill: Fill) =
            ctx.FillCount <- ctx.FillCount + 1
            ctx.Sink <- ctx.Sink + fill.Price
        let inline onFill (fill: Fill) = tf.Process(onFillSink, fill)
        let inline onTracked (decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
            match bar with
            | ValueSome b -> ctx.BarCount <- ctx.BarCount + 1; ctx.Sink <- ctx.Sink + b.Bar.VWAP
            | ValueNone -> ()
            match decision with
            | ValueSome dd -> ctx.DecisionCount <- ctx.DecisionCount + 1; ctx.Sink <- ctx.Sink + dd.Price
            | ValueNone -> ()
            fs.Process(onFill, decision, bar, stage, trade)
        for i in 0 .. d.Trades.Length - 1 do
            seg.Process(
                (fun (bar, stage, trade) ->
                    vs.Process(
                        (fun (decision, bar, stage, trade) ->
                            ell.Process(
                                (fun (decision, bar, stage, trade) ->
                                    td.Process(onTracked, decision, bar, stage, trade)),
                                decision, bar, stage, trade)),
                        bar, stage, trade, seg.Timestamp trade)),
                d.Trades.[i], i)
        ctx.Sink <- ctx.Sink + td.RealizedPnL + tf.GrossPnL - tf.Commissions

    printfn "Warming up..."
    let warmup_ctx = {BarCount = 0; DecisionCount = 0; FillCount = 0; Sink = 0.0}
    for d in dayData.[..2] do
        bench(d, warmup_ctx)

    printfn "=== OrbSystem (full pipeline) ==="
    let sw = Stopwatch.StartNew()
    let ctx = {BarCount = 0; DecisionCount = 0; FillCount = 0; Sink = 0.0}
    for d in dayData do
        bench(d, ctx)
    sw.Stop()
    printfn "  Time: %.3fs  Bars: %d  Decisions: %d  Fills: %d  Sink: %.2f  Trades/sec: %s"
        sw.Elapsed.TotalSeconds ctx.BarCount ctx.DecisionCount ctx.FillCount ctx.Sink
        ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))

let runFillBreakdown (dayData: DayData[]) barDivisor fillPercentile =
    let logPath = "logs/fill_breakdown.log"
    Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
    use logWriter = new StreamWriter(logPath, false)
    let tee fmt =
        Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

    tee "=== ORB System Fill Breakdown (single bar size, lookahead) ==="
    tee "barDivisor: %.1f  entryDelay: %.1fs" barDivisor entryDelaySeconds
    tee "Position size: $%.0f  referenceVol: %.4g  lossLimit: infinity"
        positionSize (match referenceVol with ValueSome v -> v | _ -> 0.0)
    tee "Fill sim: pctile=%.3f, delay=%.0fms, commission=$%.4f/share, rejection=%.0f%%"
        fillPercentile fillDelayMs commissionPerShare (fillRejectionRate * 100.0)
    tee ""

    let dayResults =
        [| for d in dayData do
            let seg, vs, td, ell, fs, tf = configure d.Header d.Trades barDivisor fillPercentile minVwmaDist stopMode
            let onFillSink (_: Fill) = ()
            let onFill (fill: Fill) = tf.Process(onFillSink, fill)
            let onTracked (decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
                fs.Process(onFill, decision, bar, stage, trade)
            for i in 0 .. d.Trades.Length - 1 do
                seg.Process(
                    (fun (bar, stage, trade) ->
                        vs.Process(
                            (fun (decision, bar, stage, trade) ->
                                ell.Process(
                                    (fun (decision, bar, stage, trade) ->
                                        td.Process(onTracked, decision, bar, stage, trade)),
                                    decision, bar, stage, trade)),
                            bar, stage, trade, seg.Timestamp trade)),
                    d.Trades.[i], i)
            let roundTrips = extractRoundTrips tf.Fills commissionPerShare
            let posSizes =
                [| for i in 0 .. td.Decisions.Count - 2 do
                    if td.Decisions.[i].Shares <> 0 then
                        float (abs td.Decisions.[i].Shares) * td.Decisions.[i].Price |]
            let avgPos = if posSizes.Length > 0 then (posSizes |> Array.sum) / float posSizes.Length else 0.0
            yield {
                Ticker = d.Ticker
                Date = d.Date
                DayPnL = tf.NetPnL
                RoundTrips = roundTrips
                TotalCommission = tf.Commissions
                NumFills = tf.Fills.Count
                AvgPositionSize = avgPos
            } |]

    tee "=== Per-Day Results (sorted by P&L) ==="
    tee "%-6s %-12s %10s %6s %6s %6s %10s %10s %10s %10s"
        "Ticker" "Date" "DayP&L" "trips" "W" "L" "avgWin" "avgLoss" "commiss" "avgPos$"
    tee "%s" (String.replicate 100 "-")
    let sortedDays = dayResults |> Array.sortByDescending (fun d -> d.DayPnL)
    for d in sortedDays do
        let pnls = d.RoundTrips |> Array.map (fun rt -> rt.PnL)
        let wins = pnls |> Array.filter (fun p -> p > 0.01)
        let losses = pnls |> Array.filter (fun p -> p < -0.01)
        let avgWin = if wins.Length > 0 then wins |> Array.average else 0.0
        let avgLoss = if losses.Length > 0 then losses |> Array.average else 0.0
        tee "%-6s %-12s %10.2f %6d %6d %6d %10.2f %10.2f %10.2f %10.2f"
            d.Ticker d.Date d.DayPnL d.RoundTrips.Length wins.Length losses.Length avgWin avgLoss d.TotalCommission d.AvgPositionSize

    let allTrips = dayResults |> Array.collect (fun d -> d.RoundTrips)
    let allPnLs = allTrips |> Array.map (fun rt -> rt.PnL)
    let winTrips = allPnLs |> Array.filter (fun p -> p > 0.01)
    let lossTrips = allPnLs |> Array.filter (fun p -> p < -0.01)
    let flatTrips = allPnLs |> Array.filter (fun p -> abs p <= 0.01)
    let longTrips = allTrips |> Array.filter (fun rt -> rt.Side > 0)
    let shortTrips = allTrips |> Array.filter (fun rt -> rt.Side < 0)
    let longPnL = longTrips |> Array.sumBy (fun rt -> rt.PnL)
    let shortPnL = shortTrips |> Array.sumBy (fun rt -> rt.PnL)
    let grossWins = winTrips |> Array.sum
    let grossLosses = lossTrips |> Array.sum |> abs
    let profitFactor = if grossLosses > 0.0 then grossWins / grossLosses else infinity
    let avgWin = if winTrips.Length > 0 then winTrips |> Array.average else 0.0
    let avgLoss = if lossTrips.Length > 0 then lossTrips |> Array.average else 0.0
    let avgTrade = if allPnLs.Length > 0 then allPnLs |> Array.average else 0.0
    let winRate =
        if winTrips.Length + lossTrips.Length > 0 then
            100.0 * float winTrips.Length / float (winTrips.Length + lossTrips.Length)
        else 0.0
    let expectancy =
        if winTrips.Length + lossTrips.Length > 0 then
            (winRate / 100.0) * avgWin + (1.0 - winRate / 100.0) * avgLoss
        else 0.0
    let maxWin = if winTrips.Length > 0 then winTrips |> Array.max else 0.0
    let maxLoss = if lossTrips.Length > 0 then lossTrips |> Array.min else 0.0
    let medianWin =
        if winTrips.Length > 0 then
            let s = winTrips |> Array.sort
            s.[s.Length / 2]
        else 0.0
    let medianLoss =
        if lossTrips.Length > 0 then
            let s = lossTrips |> Array.sort
            s.[s.Length / 2]
        else 0.0

    let winDays = dayResults |> Array.filter (fun d -> d.DayPnL > 0.01)
    let lossDays = dayResults |> Array.filter (fun d -> d.DayPnL < -0.01)
    let flatDays = dayResults |> Array.filter (fun d -> abs d.DayPnL <= 0.01)
    let dayWinRate =
        if winDays.Length + lossDays.Length > 0 then
            100.0 * float winDays.Length / float (winDays.Length + lossDays.Length)
        else 0.0
    let avgWinDay = if winDays.Length > 0 then (winDays |> Array.sumBy (fun d -> d.DayPnL)) / float winDays.Length else 0.0
    let avgLossDay = if lossDays.Length > 0 then (lossDays |> Array.sumBy (fun d -> d.DayPnL)) / float lossDays.Length else 0.0
    let worstDay = if lossDays.Length > 0 then lossDays |> Array.minBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0
    let bestDay = if winDays.Length > 0 then winDays |> Array.maxBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0

    let totalPnL = dayResults |> Array.sumBy (fun d -> d.DayPnL)
    let totalCommissions = dayResults |> Array.sumBy (fun d -> d.TotalCommission)
    let totalFillsCount = dayResults |> Array.sumBy (fun d -> d.NumFills)
    let avgPosOverall =
        if dayResults.Length > 0 then
            (dayResults |> Array.sumBy (fun d -> d.AvgPositionSize)) / float dayResults.Length
        else 0.0

    tee ""
    tee "=== Aggregate Statistics ==="
    tee ""
    tee "--- Overall ---"
    tee "Total P&L:         $%12.2f" totalPnL
    tee "Total commissions: $%12.2f" totalCommissions
    tee "Total days:         %12d" dayResults.Length
    tee "Total round trips:  %12d" allTrips.Length
    tee "Total fills:        %12d" totalFillsCount
    tee "Avg trips/day:      %12.1f" (float allTrips.Length / float dayResults.Length)
    tee "Avg position size:  $%12.2f" avgPosOverall
    tee ""
    tee "--- Round-Trip Level ---"
    tee "Win trades:         %12d" winTrips.Length
    tee "Loss trades:        %12d" lossTrips.Length
    tee "Flat trades:        %12d" flatTrips.Length
    tee "Win rate:           %12.1f%%" winRate
    tee "Avg winner:         $%12.2f" avgWin
    tee "Avg loser:          $%12.2f" avgLoss
    tee "Median winner:      $%12.2f" medianWin
    tee "Median loser:       $%12.2f" medianLoss
    tee "Largest winner:     $%12.2f" maxWin
    tee "Largest loser:      $%12.2f" maxLoss
    tee "Avg trade:          $%12.2f" avgTrade
    tee "Expectancy:         $%12.2f" expectancy
    tee "Gross wins:         $%12.2f" grossWins
    tee "Gross losses:       $%12.2f" grossLosses
    tee "Profit factor:      %12.2f" profitFactor
    tee ""
    tee "--- Day-Level ---"
    tee "Winning days:       %12d" winDays.Length
    tee "Losing days:        %12d" lossDays.Length
    tee "Flat days:          %12d" flatDays.Length
    tee "Day win rate:       %12.1f%%" dayWinRate
    tee "Avg winning day:    $%12.2f" avgWinDay
    tee "Avg losing day:     $%12.2f" avgLossDay
    tee "Worst day:          $%12.2f" worstDay
    tee "Best day:           $%12.2f" bestDay
    tee ""
    tee "--- Long/Short Split ---"
    tee "Long trades:        %12d" longTrips.Length
    tee "Long P&L:           $%12.2f" longPnL
    tee "Avg long trade:     $%12.2f" (if longTrips.Length > 0 then longPnL / float longTrips.Length else 0.0)
    tee "Short trades:       %12d" shortTrips.Length
    tee "Short P&L:          $%12.2f" shortPnL
    tee "Avg short trade:    $%12.2f" (if shortTrips.Length > 0 then shortPnL / float shortTrips.Length else 0.0)

// ============================================================================
// Decision-level (no fill sim) breakdown
// ============================================================================

type TradeBreakdownDay = {
    Ticker: string
    Date: string
    DayPnL: float
    TradePnLs: (float * int)[]  // (pnl, prevShares)
    AvgPositionSize: float
    NumDecisions: int
}

let runTradeBreakdown (dayData: DayData[]) bars =
    let logPath = "logs/trade_breakdown.log"
    Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
    use logWriter = new StreamWriter(logPath, false)
    let tee fmt =
        Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

    tee "=== ORB System Trade Breakdown (decision-level, no fill sim) ==="
    tee "barDivisor: %.1f  entryDelay: %.1fs" barDivisor entryDelaySeconds
    tee "Position size: $%.0f  referenceVol: %.4g  lossLimit: infinity"
        positionSize (match referenceVol with ValueSome v -> v | _ -> 0.0)
    tee ""

    let dayResults =
        [| for d in dayData do
            let barSize = computeBarSize d.Header d.Trades bars
            let seg = SegregateTrades(barSize, DateTime d.Header.BaseTicks)
            seg.OpeningPrintIdx <- d.Header.OpeningPrintIndex
            let vs = OrbSystem(positionSize, referenceVol, minVwmaDist, stopMode)
            let td = TrackDecisions()
            let ell = EnforceLossLimit((fun () -> td.RealizedPnL), infinity)
            let onTracked (_decision, _bar, _stage, _trade) = ()
            for i in 0 .. d.Trades.Length - 1 do
                seg.Process(
                    (fun (bar, stage, trade) ->
                        vs.Process(
                            (fun (decision, bar, stage, trade) ->
                                ell.Process(
                                    (fun (decision, bar, stage, trade) ->
                                        td.Process(onTracked, decision, bar, stage, trade)),
                                    decision, bar, stage, trade)),
                            bar, stage, trade, seg.Timestamp trade)),
                    d.Trades.[i], i)
            let decs = td.Decisions
            let tradePnLs =
                [| for i in 1 .. decs.Count - 1 do
                    let prev = decs.[i - 1]
                    let curr = decs.[i]
                    (curr.Price - prev.Price) * float prev.Shares, prev.Shares |]
            let posSizes =
                [| for i in 0 .. decs.Count - 2 do
                    if decs.[i].Shares <> 0 then
                        float (abs decs.[i].Shares) * decs.[i].Price |]
            let avgPos = if posSizes.Length > 0 then (posSizes |> Array.sum) / float posSizes.Length else 0.0
            yield {
                Ticker = d.Ticker
                Date = d.Date
                DayPnL = td.RealizedPnL
                TradePnLs = tradePnLs
                AvgPositionSize = avgPos
                NumDecisions = decs.Count
            } |]

    tee "=== Per-Day Results (sorted by P&L) ==="
    tee "%-6s %-12s %10s %6s %6s %6s %10s %10s %10s"
        "Ticker" "Date" "DayP&L" "trades" "W" "L" "avgWin" "avgLoss" "avgPos$"
    tee "%s" (String.replicate 90 "-")
    let sortedDays = dayResults |> Array.sortByDescending (fun d -> d.DayPnL)
    for d in sortedDays do
        let pnls = d.TradePnLs |> Array.map fst
        let wins = pnls |> Array.filter (fun p -> p > 0.01)
        let losses = pnls |> Array.filter (fun p -> p < -0.01)
        let avgWin = if wins.Length > 0 then wins |> Array.average else 0.0
        let avgLoss = if losses.Length > 0 then losses |> Array.average else 0.0
        tee "%-6s %-12s %10.2f %6d %6d %6d %10.2f %10.2f %10.2f"
            d.Ticker d.Date d.DayPnL pnls.Length wins.Length losses.Length avgWin avgLoss d.AvgPositionSize

    let allTradesWithSide = dayResults |> Array.collect (fun d -> d.TradePnLs)
    let allTrades = allTradesWithSide |> Array.map fst
    let winTrades = allTrades |> Array.filter (fun p -> p > 0.01)
    let lossTrades = allTrades |> Array.filter (fun p -> p < -0.01)
    let flatTrades = allTrades |> Array.filter (fun p -> abs p <= 0.01)
    let longTrades = allTradesWithSide |> Array.filter (fun (_, s) -> s > 0) |> Array.map fst
    let shortTrades = allTradesWithSide |> Array.filter (fun (_, s) -> s < 0) |> Array.map fst
    let longPnL = if longTrades.Length > 0 then longTrades |> Array.sum else 0.0
    let shortPnL = if shortTrades.Length > 0 then shortTrades |> Array.sum else 0.0
    let grossWins = winTrades |> Array.sum
    let grossLosses = lossTrades |> Array.sum |> abs
    let profitFactor = if grossLosses > 0.0 then grossWins / grossLosses else infinity
    let avgWin = if winTrades.Length > 0 then winTrades |> Array.average else 0.0
    let avgLoss = if lossTrades.Length > 0 then lossTrades |> Array.average else 0.0
    let avgTrade = if allTrades.Length > 0 then allTrades |> Array.average else 0.0
    let winRate =
        if winTrades.Length + lossTrades.Length > 0 then
            100.0 * float winTrades.Length / float (winTrades.Length + lossTrades.Length)
        else 0.0
    let expectancy =
        if winTrades.Length + lossTrades.Length > 0 then
            (winRate / 100.0) * avgWin + (1.0 - winRate / 100.0) * avgLoss
        else 0.0
    let maxWin = if winTrades.Length > 0 then winTrades |> Array.max else 0.0
    let maxLoss = if lossTrades.Length > 0 then lossTrades |> Array.min else 0.0
    let medianWin =
        if winTrades.Length > 0 then
            let s = winTrades |> Array.sort
            s.[s.Length / 2]
        else 0.0
    let medianLoss =
        if lossTrades.Length > 0 then
            let s = lossTrades |> Array.sort
            s.[s.Length / 2]
        else 0.0

    let winDays = dayResults |> Array.filter (fun d -> d.DayPnL > 0.01)
    let lossDays = dayResults |> Array.filter (fun d -> d.DayPnL < -0.01)
    let flatDays = dayResults |> Array.filter (fun d -> abs d.DayPnL <= 0.01)
    let dayWinRate =
        if winDays.Length + lossDays.Length > 0 then
            100.0 * float winDays.Length / float (winDays.Length + lossDays.Length)
        else 0.0
    let avgWinDay = if winDays.Length > 0 then (winDays |> Array.sumBy (fun d -> d.DayPnL)) / float winDays.Length else 0.0
    let avgLossDay = if lossDays.Length > 0 then (lossDays |> Array.sumBy (fun d -> d.DayPnL)) / float lossDays.Length else 0.0
    let worstDay = if lossDays.Length > 0 then lossDays |> Array.minBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0
    let bestDay = if winDays.Length > 0 then winDays |> Array.maxBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0

    let totalPnL = dayResults |> Array.sumBy (fun d -> d.DayPnL)
    let avgPosOverall =
        if dayResults.Length > 0 then
            (dayResults |> Array.sumBy (fun d -> d.AvgPositionSize)) / float dayResults.Length
        else 0.0

    tee ""
    tee "=== Aggregate Statistics ==="
    tee ""
    tee "--- Overall ---"
    tee "Total P&L:         $%12.2f" totalPnL
    tee "Total days:         %12d" dayResults.Length
    tee "Total trades:       %12d" allTrades.Length
    tee "Avg trades/day:     %12.1f" (float allTrades.Length / float (max 1 dayResults.Length))
    tee "Avg position size:  $%12.2f" avgPosOverall
    tee ""
    tee "--- Trade-Level ---"
    tee "Win trades:         %12d" winTrades.Length
    tee "Loss trades:        %12d" lossTrades.Length
    tee "Flat trades:        %12d" flatTrades.Length
    tee "Win rate:           %12.1f%%" winRate
    tee "Avg winner:         $%12.2f" avgWin
    tee "Avg loser:          $%12.2f" avgLoss
    tee "Median winner:      $%12.2f" medianWin
    tee "Median loser:       $%12.2f" medianLoss
    tee "Largest winner:     $%12.2f" maxWin
    tee "Largest loser:      $%12.2f" maxLoss
    tee "Avg trade:          $%12.2f" avgTrade
    tee "Expectancy:         $%12.2f" expectancy
    tee "Gross wins:         $%12.2f" grossWins
    tee "Gross losses:       $%12.2f" grossLosses
    tee "Profit factor:      %12.2f" profitFactor
    tee ""
    tee "--- Day-Level ---"
    tee "Winning days:       %12d" winDays.Length
    tee "Losing days:        %12d" lossDays.Length
    tee "Flat days:          %12d" flatDays.Length
    tee "Day win rate:       %12.1f%%" dayWinRate
    tee "Avg winning day:    $%12.2f" avgWinDay
    tee "Avg losing day:     $%12.2f" avgLossDay
    tee "Worst day:          $%12.2f" worstDay
    tee "Best day:           $%12.2f" bestDay
    tee ""
    tee "--- Long/Short Split ---"
    tee "Long trades:        %12d" longTrades.Length
    tee "Long P&L:           $%12.2f" longPnL
    tee "Avg long trade:     $%12.2f" (if longTrades.Length > 0 then longPnL / float longTrades.Length else 0.0)
    tee "Short trades:       %12d" shortTrades.Length
    tee "Short P&L:          $%12.2f" shortPnL
    tee "Avg short trade:    $%12.2f" (if shortTrades.Length > 0 then shortPnL / float shortTrades.Length else 0.0)

// ============================================================================
// CLI
// ============================================================================

type ConvertArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-t")>] Trades_Dir of string
    | [<AltCommandLine("-o")>] Output of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input plays JSON (e.g. data/continuation_plays.json)"
            | Trades_Dir _ -> "Parquet root (default: data/trades)"
            | Output _ -> "Output binary directory (default: data/trades_bin)"

type BreakdownArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-b")>] Bars of int
    | [<AltCommandLine("-p")>] Percentile of float

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input JSON with [{ticker, date}] entries (e.g. data/breakdown_2k.json)"
            | Bars _ -> "The target number of bars per session (e.g. 3000)"
            | Percentile _ -> "The target percentile to buy down in a bar (e.g. 0.05)"

type SweepArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-n")>] Steps of int
    | [<AltCommandLine("-l")>] Lo of float
    | [<AltCommandLine("-u")>] Hi of float
    | Stop_Steps of int
    | Stop_Lo of float
    | Stop_Hi of float
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input JSON with [{ticker, date}] entries"
            | Steps _ -> "Number of log-spaced minVwmaDist steps (default: 10). If lo<=0, step 0 is 0.0 and remaining n-1 are log-spaced in [0.1, hi]."
            | Lo _ -> "Lower minVwmaDist in vol units (default: 0.0 = unfiltered baseline)"
            | Hi _ -> "Upper minVwmaDist in vol units (default: 20)"
            | Stop_Steps _ -> "Number of log-spaced stopVol steps (default: 5)"
            | Stop_Lo _ -> "Lower stopVol in vol units (default: 1.0)"
            | Stop_Hi _ -> "Upper stopVol in vol units (default: 10.0)"

type BenchmarkArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input JSON with [{ticker, date}] entries"

type Command =
    | [<CliPrefix(CliPrefix.None)>] Convert of ParseResults<ConvertArgs>
    | [<CliPrefix(CliPrefix.None)>] Breakdown of ParseResults<BreakdownArgs>
    | [<CliPrefix(CliPrefix.None)>] Trade_Breakdown of ParseResults<BreakdownArgs>
    | [<CliPrefix(CliPrefix.None)>] Sweep of ParseResults<SweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Benchmark of ParseResults<BenchmarkArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Convert _ -> "Convert parquet trade files to the v2 binary format"
            | Breakdown _ -> "Run the full pipeline with fills and print fill-level breakdown"
            | Trade_Breakdown _ -> "Run decisions-only (no fill sim) and print decision-level breakdown"
            | Sweep _ -> "Run a parallel parameter sweep"
            | Benchmark _ -> "Benchmark the pipeline (throughput)"

let runConvert (args: ParseResults<ConvertArgs>) =
    let input = args.GetResult ConvertArgs.Input
    let tradesDir = args.GetResult(ConvertArgs.Trades_Dir, "data/trades")
    let outDir = args.GetResult(ConvertArgs.Output, "data/trades_bin")
    Convert.convertPlays input tradesDir outDir

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Command>(programName = "TradingEdge.Vwap")
    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)
        match results.GetSubCommand() with
        | Convert args -> runConvert args
        | Breakdown args ->
            let input = args.GetResult <@ BreakdownArgs.Input @>
            let bars = args.TryGetResult <@ BreakdownArgs.Bars @> |> Option.map float |> Option.defaultValue barDivisor
            let percentile = args.TryGetResult <@ BreakdownArgs.Percentile @> |> Option.map float |> Option.defaultValue fillPercentile
            let dayData, _ = loadDayData input
            runFillBreakdown dayData bars percentile
        | Trade_Breakdown args ->
            let input = args.GetResult <@ BreakdownArgs.Input @>
            let bars = args.TryGetResult <@ BreakdownArgs.Bars @> |> Option.map float |> Option.defaultValue barDivisor
            let dayData, _ = loadDayData input
            runTradeBreakdown dayData bars
        | Sweep args ->
            let input = args.GetResult <@ SweepArgs.Input @>
            let n = args.GetResult(<@ SweepArgs.Steps @>, 10)
            let lo = args.GetResult(<@ SweepArgs.Lo @>, 0.0)
            let hi = args.GetResult(<@ SweepArgs.Hi @>, 20.0)
            let sn = args.GetResult(<@ SweepArgs.Stop_Steps @>, 5)
            let slo = args.GetResult(<@ SweepArgs.Stop_Lo @>, 1.0)
            let shi = args.GetResult(<@ SweepArgs.Stop_Hi @>, 10.0)
            let dayData, _ = loadDayData input
            let minDists =
                if lo <= 0.0 then Array.append [| 0.0 |] (logSpaced (n - 1) 0.1 hi)
                else logSpaced n lo hi
            // Build stopMode column: [vwma64; vol-steps...; rangeLo] so we can compare all three in one run.
            let volModes = logSpaced sn slo shi |> Array.map StopAtVol
            let stopModes = Array.concat [| [| StopAtVwma64 |]; volModes; [| StopAtRange |] |]
            runParallelSweep dayData minDists stopModes
        | Benchmark args ->
            let input = args.GetResult <@ BenchmarkArgs.Input @>
            let dayData, totalTrades = loadDayData input
            runBenchmark dayData totalTrades
        0
    with
    | :? ArguParseException as e ->
        eprintfn "%s" e.Message
        1
