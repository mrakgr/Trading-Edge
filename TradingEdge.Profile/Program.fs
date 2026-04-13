module TradingEdge.Profile.Program

open System
open System.IO
open System.Collections.Immutable
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Diagnostics
open TradingEdge.Parsing.TradeBinary

// ============================================================================
// Local VolumeBar over TradeRecord (Profile-scoped; replaces the shared one)
// ============================================================================

type VolumeBar =
    {
        CumulativeVolume: float
        VWAP: float
        StdDev: float
        Volume: float
        Trades: ImmutableArray<TradeRecord>
    }

// ============================================================================
// Class-based pipeline (separate classes with member inline onNext)
// ============================================================================

type VolumeBarOfTrades() =
    member val CumulativeVolumeSum = 0.0 with get, set

    member inline self.Process(onNext, trades: ImmutableArray<TradeRecord>) =
        let mutable priceVolumeSum = 0.0
        let mutable priceSquaredVolumeSum = 0.0
        let mutable volumeSum = 0.0
        for t in trades do
            let v = float t.Size
            priceVolumeSum <- priceVolumeSum + t.Price * v
            priceSquaredVolumeSum <- priceSquaredVolumeSum + t.Price * t.Price * v
            volumeSum <- volumeSum + v
        let vwap = priceVolumeSum / volumeSum
        let variance = priceSquaredVolumeSum / volumeSum - vwap * vwap
        self.CumulativeVolumeSum <- self.CumulativeVolumeSum + volumeSum
        onNext {
            CumulativeVolume = self.CumulativeVolumeSum
            VWAP = vwap
            StdDev = sqrt (max 0.0 variance)
            Volume = volumeSum
            Trades = trades
        }

type GroupTrades(barSize: float) =
    member val BarSize = barSize
    member val CurrentTrades = ImmutableArray.CreateBuilder<TradeRecord>()
    member val CurrentVolumeSum = 0.0 with get, set

    member inline self.Process(onNext, trade: TradeRecord) =
        let mutable remaining = float trade.Size
        while remaining > 0.0 do
            let spaceLeft = self.BarSize - self.CurrentVolumeSum
            if remaining <= spaceLeft then
                self.CurrentTrades.Add { trade with Size = int32 remaining }
                self.CurrentVolumeSum <- self.CurrentVolumeSum + remaining
                remaining <- 0.0
            else
                if spaceLeft > 0.0 then
                    self.CurrentTrades.Add { trade with Size = int32 spaceLeft }
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

    member inline self.Process(onNext, trade: TradeRecord) =
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

    member inline self.Process(onNext, trade: TradeRecord, includeInVwma: bool, includeInVol: bool) =
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
// Trade segregator
// ============================================================================

[<Struct>]
type TradeStage =
    | EarlyPremarket
    | LatePremarket
    | AfterOpeningPrint
    | BeforeClosing

let defaultEstimationOffsets = [| 5.0; 30.0; 150.0; 750.0 |]

let findEstimationOffset (currentOffset: float) : int voption =
    let mutable result = ValueNone
    let mutable k = defaultEstimationOffsets.Length - 1
    while k >= 0 && result.IsNone do
        if defaultEstimationOffsets.[k] <= currentOffset then
            result <- ValueSome k
        k <- k - 1
    result

type SegregateTrades(volPcts: float[]) =
    member val VolPcts = volPcts
    member val VolPctsOffset = ValueNone with get, set
    member val ArgsBuilder : VwapSystemArgsBuilder voption = ValueNone with get, set
    member val ClosingPause = -60.0
    member val BaseTicks = 0L with get, set
    member val OpeningPrintIdx = int AbsentIndex : int with get, set
    member val ClosingPrintIdx = int AbsentIndex : int with get, set
    member val OpenTime = DateTime.MaxValue with get, set
    member val CloseTime = DateTime.MaxValue with get, set

    member inline self.Timestamp(trade: TradeRecord) =
        DateTime(self.BaseTicks + int64 trade.TimeDeci * 1000L)

    member self.TradeStage(trade: TradeRecord, index: int) =
        let ts = self.Timestamp trade
        if self.OpenTime <= ts then
            if self.CloseTime.AddSeconds self.ClosingPause <= ts then
                BeforeClosing
            else
                AfterOpeningPrint
        else
            if self.OpeningPrintIdx = index then
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
        struct (includeInVwma, includeInVol)


    member inline self.Process(onNext, trades: ReadOnlySpan<TradeRecord>) : TradeStage =
        let lastIdx = trades.Length - 1
        let trade = trades.[lastIdx]
        let stage = self.TradeStage(trade, lastIdx)
        let mutable lastBar = ValueNone
        let feedTrade(stage: TradeStage, trade: TradeRecord) =
            match self.ArgsBuilder with
            | ValueSome sys ->
                let struct (inclVwma, inclVol) = self.CalculateFlags stage
                sys.Process((fun bar -> lastBar <- ValueSome bar), trade, inclVwma, inclVol)
            | ValueNone -> ()
        match stage with
        | AfterOpeningPrint ->
            let currentOffset = (self.Timestamp trade - self.OpenTime).TotalSeconds
            let i = findEstimationOffset currentOffset
            if self.VolPctsOffset <> i then
                self.VolPctsOffset <- i
                match self.VolPctsOffset with
                | ValueSome vpIdx ->
                    let mutable totalVolume = 0.0
                    for j in 0 .. trades.Length - 1 do
                        totalVolume <- totalVolume + float trades.[j].Size
                    let barSize = totalVolume * self.VolPcts.[vpIdx]
                    let newSystem = VwapSystemArgsBuilder(barSize)
                    self.ArgsBuilder <- ValueSome newSystem
                    for j in 0 .. trades.Length - 1 do
                        let t = trades.[j]
                        feedTrade(self.TradeStage(t, j), t)
                | ValueNone -> ()
            else
                feedTrade(stage, trade)
        | _ ->
            feedTrade(stage, trade)
        onNext (lastBar, stage, trade)

// ============================================================================
// VWAP trading system (decisions from bars)
// ============================================================================

[<Struct>]
type TradingDecision = {
    Timestamp: DateTime
    Price: float
    Shares: int
}

[<Struct>]
type SimulatorState =
    | Active of price: float * position: int
    | Done

type VwapSystem(positionSize: float, referenceVol: float voption, bandVol: float) =
    member val PositionSize = positionSize
    member val ReferenceVol = referenceVol
    member val BandVol = bandVol
    member val State = Active(0.0, 0) with get, set

    member inline self.EffectiveSize(vf: float) =
        match self.ReferenceVol with
        | ValueNone -> self.PositionSize
        | ValueSome refVol -> min self.PositionSize (self.PositionSize * refVol / vf)

    member inline self.Process(onNext, bar: VwapSystemBar voption, stage: TradeStage, trade: TradeRecord, tradeTs: DateTime) =
        let mutable decision = ValueNone
        match stage with
        | EarlyPremarket | LatePremarket -> ()
        | AfterOpeningPrint ->
            match bar with
            | ValueSome b ->
                match self.State with
                | Active(_, position) ->
                    let lastBar = b.Bar
                    let targetShares = round (self.EffectiveSize b.VolFactor / trade.Price) |> int
                    let band = self.BandVol * b.VolFactor * lastBar.VWAP
                    if targetShares > 0 then
                        if lastBar.VWAP + band >= b.Vwma && position <= 0 then
                            self.State <- Active(lastBar.VWAP, targetShares)
                            decision <- ValueSome { Timestamp = tradeTs; Price = lastBar.VWAP; Shares = targetShares }
                        elif lastBar.VWAP - band < b.Vwma && position >= 0 then
                            self.State <- Active(lastBar.VWAP, -targetShares)
                            decision <- ValueSome { Timestamp = tradeTs; Price = lastBar.VWAP; Shares = -targetShares }
                        elif lastBar.VWAP >= b.Vwma && position <= 0 then
                            self.State <- Active(lastBar.VWAP, targetShares)
                            decision <- ValueSome { Timestamp = tradeTs; Price = lastBar.VWAP; Shares = 0 }
                        elif lastBar.VWAP < b.Vwma && position >= 0 then
                            self.State <- Active(lastBar.VWAP, -targetShares)
                            decision <- ValueSome { Timestamp = tradeTs; Price = lastBar.VWAP; Shares = 0 }
                    else
                        if lastBar.VWAP >= b.Vwma && position < 0 then
                            self.State <- Active(lastBar.VWAP, 0)
                            decision <- ValueSome { Timestamp = tradeTs; Price = lastBar.VWAP; Shares = 0 }
                        elif lastBar.VWAP < b.Vwma && position > 0 then
                            self.State <- Active(lastBar.VWAP, 0)
                            decision <- ValueSome { Timestamp = tradeTs; Price = lastBar.VWAP; Shares = 0 }
                | Done -> ()
            | ValueNone -> ()
        | BeforeClosing ->
            match self.State with
            | Active(_, position) when position <> 0 ->
                self.State <- Done
                decision <- ValueSome { Timestamp = tradeTs; Price = trade.Price; Shares = 0 }
            | _ -> ()
        onNext (decision, bar, stage, trade)

// ============================================================================
// Decision tracker (running realized PnL)
// ============================================================================

type TrackDecisions() =
    member val Decisions = ResizeArray<TradingDecision>(32)
    member val RealizedPnL = 0.0 with get, set

    member inline self.Process(onNext, decision: TradingDecision voption, bar: VwapSystemBar voption, stage: TradeStage, trade: TradeRecord) =
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

    member inline self.Process(onNext, decision: TradingDecision voption, bar: VwapSystemBar voption, stage: TradeStage, trade: TradeRecord) =
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
let inline bandPrice (prices_and_volumes: struct (float * float)[]) (q_lo: float) (q_hi: float) : float =
    let n = prices_and_volumes.Length
    let inline structFst struct (x, _) = x
    if n = 0 then nan
    elif n = 1 then prices_and_volumes.[0] |> structFst
    else
        let prices_and_volumes = Array.sort prices_and_volumes
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

type FillSimulator(percentile: float, delayMs: float, rejectionRate: float, rngOpt: Random voption) =
    let compression = 100.0

    member val Percentile = percentile
    member val Delay = TimeSpan.FromMilliseconds delayMs
    member val RejectionRate = rejectionRate
    member val Rng =
        match rngOpt with
        | ValueSome r -> r
        | ValueNone -> Random(12345)

    member val BaseTicks = 0L with get, set
    member val Fills = ResizeArray<Fill>(256)
    member val BuyOrder : LimitOrder voption = ValueNone with get, set
    member val SellOrder : LimitOrder voption = ValueNone with get, set
    member val TargetPosition = 0 with get, set
    member val FilledPosition = 0 with get, set
    member val LastPricesAndVolumes : struct (float * float)[] = Array.empty with get, set
    member val PendingTarget : int voption = ValueNone with get, set

    member inline self.Timestamp(trade: TradeRecord) =
        DateTime(self.BaseTicks + int64 trade.TimeDeci * 1000L)

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

    member self.TryFillBuy(trade: TradeRecord, now: DateTime) =
        match self.BuyOrder with
        | ValueSome order when not (self.IsCancelled(now, order)) ->
            let priceOpt = order.Prices |> tryFindV (fun struct (_, at) -> now >= at)
            let qtyOpt = order.Quantities |> tryFindV (fun struct (_, at) -> now >= at)
            match priceOpt, qtyOpt with
            | ValueSome (struct (price, _)), ValueSome (struct (qty, _)) when trade.Price <= price ->
                let remaining = qty - order.FilledQuantity
                if remaining > 0 && (self.RejectionRate = 0.0 || self.Rng.NextDouble() >= self.RejectionRate) then
                    let fillQty = min remaining trade.Size
                    if fillQty > 0 then
                        self.Fills.Add { Timestamp = now; Price = price; Quantity = fillQty }
                        self.FilledPosition <- self.FilledPosition + fillQty
                        self.BuyOrder <- ValueSome { order with FilledQuantity = order.FilledQuantity + fillQty }
            | _ -> ()
        | _ -> ()

    member self.TryFillSell(trade: TradeRecord, now: DateTime) =
        match self.SellOrder with
        | ValueSome order when not (self.IsCancelled(now, order)) ->
            let priceOpt = order.Prices |> tryFindV (fun struct (_, at) -> now >= at)
            let qtyOpt = order.Quantities |> tryFindV (fun struct (_, at) -> now >= at)
            match priceOpt, qtyOpt with
            | ValueSome (struct (price, _)), ValueSome (struct (qty, _)) when trade.Price >= price ->
                let remaining = qty - order.FilledQuantity
                if remaining > 0 && (self.RejectionRate = 0.0 || self.Rng.NextDouble() >= self.RejectionRate) then
                    let fillQty = min remaining trade.Size
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

    member inline self.Process(onNext, decision: TradingDecision voption, bar: VwapSystemBar voption, stage: TradeStage, trade: TradeRecord) =
        let now = self.Timestamp trade

        // 1. Process cancellations
        self.ProcessCancellations now

        // 2. New bar: build price/volume array and reprice active orders
        match bar with
        | ValueSome b ->
            let trades = b.Bar.Trades
            let pv = Array.init trades.Length (fun i ->
                let t = trades.[i]
                struct (t.Price, float t.Size))
            self.LastPricesAndVolumes <- pv
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
// Binary loading (TradeRecord-native, no Trade conversion)
// ============================================================================

let loadDayRecords (path: string) : DayHeader * TradeRecord[] =
    let bytes = File.ReadAllBytes(path)
    let header = MemoryMarshal.Read<DayHeader>(ReadOnlySpan(bytes, 0, HeaderSize))
    let tradeCount = int header.TradeCount
    let records = MemoryMarshal.Cast<byte, TradeRecord>(ReadOnlySpan(bytes, HeaderSize, tradeCount * RecordSize)).ToArray()
    header, records

// ============================================================================
// Benchmark harness
// ============================================================================

type SinkContext = {
    mutable BarCount: int
    mutable DecisionCount: int
    mutable FillCount: int
    mutable Sink : float
}

[<EntryPoint>]
let main argv =
    let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
    let entryRegex = Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")
    let entries =
        File.ReadAllText ps1Path
        |> entryRegex.Matches
        |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
        |> Seq.distinct
        |> Seq.filter (fun (t, d) -> File.Exists (sprintf "data/trades_bin/%s/%s.bin" t d))
        |> Seq.toArray

    printfn "Loading %d days from binary files..." entries.Length
    let swLoad = Stopwatch.StartNew()
    let dayData =
        [| for (ticker, date) in entries do
            let path = sprintf "data/trades_bin/%s/%s.bin" ticker date
            let header, trades = loadDayRecords path
            if header.OpeningPrintIndex <> AbsentIndex && header.ClosingPrintIndex <> AbsentIndex then
                yield {| Ticker = ticker; Date = date
                         Header = header; Trades = trades |} |]
    swLoad.Stop()
    let totalTrades = dayData |> Array.sumBy (fun d -> int64 d.Trades.Length)
    printfn "Loaded %d days (%s trades) in %.3fs\n" dayData.Length (totalTrades.ToString("N0")) swLoad.Elapsed.TotalSeconds

    let pcts =
        let basePct = 0.005
        let decay = 0.9
        [| -8.69; -1.10; -16.27; -16.73 |] |> Array.map (fun i -> basePct * (decay ** i))

    let positionSize = 30000.0
    let referenceVol = ValueSome 0.0095
    let bandVol = 0.0

    let configure (header: DayHeader) =
        let seg = SegregateTrades(pcts)
        seg.BaseTicks <- header.BaseTicks
        seg.OpeningPrintIdx <- int header.OpeningPrintIndex
        seg.ClosingPrintIdx <- int header.ClosingPrintIndex
        seg.OpenTime <- DateTime(header.SessionOpenTicks)
        seg.CloseTime <- DateTime(header.SessionCloseTicks)
        let vs = VwapSystem(positionSize, referenceVol, bandVol)
        let td = TrackDecisions()
        let tf = TrackFills(0.0035)
        let ell = EnforceLossLimit((fun () -> tf.NetPnL), 0.085 * positionSize)
        let fs = FillSimulator(0.5, 100.0, 0.0, ValueNone)
        fs.BaseTicks <- header.BaseTicks
        seg, vs, td, ell, fs, tf


    let bench(d : {| Date: string; Header: DayHeader; Ticker: string; Trades: array<TradeRecord> |}, ctx : SinkContext) =
        let seg, vs, td, ell, fs, tf = configure d.Header
        let inline onFillSink (fill: Fill) =
            ctx.FillCount <- ctx.FillCount + 1
            ctx.Sink <- ctx.Sink + fill.Price
        let inline onFill (fill: Fill) = tf.Process(onFillSink, fill)
        let inline onTracked (decision: TradingDecision voption, bar: VwapSystemBar voption, stage: TradeStage, trade: TradeRecord) =
            match bar with
            | ValueSome b -> ctx.BarCount <- ctx.BarCount + 1; ctx.Sink <- ctx.Sink + b.Bar.VWAP
            | ValueNone -> ()
            match decision with
            | ValueSome dd -> ctx.DecisionCount <- ctx.DecisionCount + 1; ctx.Sink <- ctx.Sink + dd.Price
            | ValueNone -> ()
            fs.Process(onFill, decision, bar, stage, trade)
            stage
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
                ReadOnlySpan(d.Trades, 0, i + 1)) |> ignore
        ctx.Sink <- ctx.Sink + td.RealizedPnL + tf.GrossPnL - tf.Commissions

    // Warm up
    printfn "Warming up..."
    let warmup_ctx = {BarCount = 0; DecisionCount = 0; FillCount = 0; Sink = 0.0}
    for d in dayData.[..2] do
        bench(d, warmup_ctx)

    // Benchmark
    printfn "=== VwapSystem (full pipeline) ==="
    let sw = Stopwatch.StartNew()
    let ctx = {BarCount = 0; DecisionCount = 0; FillCount = 0; Sink = 0.0}
    for d in dayData do
        bench(d, ctx)
    sw.Stop()
    printfn "  Time: %.3fs  Bars: %d  Decisions: %d  Fills: %d  Sink: %.2f  Trades/sec: %s" sw.Elapsed.TotalSeconds ctx.BarCount ctx.DecisionCount ctx.FillCount ctx.Sink ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))
    0
