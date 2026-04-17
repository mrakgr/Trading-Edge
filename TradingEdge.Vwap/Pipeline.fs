module TradingEdge.Vwap.Pipeline

open System
open System.Collections.Immutable
open System.Runtime.InteropServices
open TradeLoader
open TradeBinary

// ============================================================================
// Bar type + aggregation pipeline
// ============================================================================

type Bar =
    {
        VWAP: float
        StdDev: float
        Volume: float
        Trades: ImmutableArray<struct (float * float)>
    }

/// Reduces a group of trades into a single Bar (VWAP, stddev, total volume).
type TradesToBarBuilder() =
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

// ============================================================================
// Time bars
// ============================================================================

/// Groups trades into fixed time buckets (bucket index = trade_ticks / bucketTicks).
/// Flushes the current bucket (emitting a Bar) when a trade lands in a later bucket.
/// Empty buckets are skipped — no bar is emitted for a window with zero trades.
type TimeBarBuilder(bucketSpan: TimeSpan) =
    member val BucketTicks = bucketSpan.Ticks
    member val BarBuilder = TradesToBarBuilder()
    member val CurrentBucket = Int64.MinValue with get, set
    member val CurrentTrades = ImmutableArray.CreateBuilder<struct (float * float)>()

    member inline self.Flush(onNext) =
        if self.CurrentTrades.Count > 0 then
            self.BarBuilder.Process(onNext, self.CurrentTrades.ToImmutableArray())
            self.CurrentTrades.Clear()

    member inline self.Process(onNext, trade: Trade) =
        let tradeTicks = trade.TicksFromBase
        let bucket = tradeTicks / self.BucketTicks
        if bucket > self.CurrentBucket then
            self.Flush onNext
            self.CurrentBucket <- bucket
        self.CurrentTrades.Add (struct (trade.Price, float trade.Volume))

// ============================================================================
// Pairwise bar variance
// ============================================================================

/// Log-return squared between consecutive bar VWAPs.
let pairwiseRealizedVariance (a: Bar) (b: Bar) =
    log (a.VWAP / b.VWAP) ** 2

// ============================================================================
// ORB system bar + args builder (realized-vol + opening range)
// ============================================================================

type OrbSystemBar = {
    Bar : Bar
    VolFactor : float
    RangeHigh : float
    RangeLow : float
}

type OrbSystemArgsBuilder(bucketSpan: TimeSpan) =
    member val BarBuilder = TimeBarBuilder(bucketSpan)
    member val VarianceSum = 0.0 with get, set
    member val PairCount = 0 with get, set
    member val PrevBar = ValueOption<Bar>.None with get, set
    member val RangeHigh = Double.NegativeInfinity with get, set
    member val RangeLow = Double.PositiveInfinity with get, set

    member inline self.OnBar(onNext, bar: Bar, includeInVol: bool, includeInRange: bool) =
        match self.PrevBar with
        | ValueSome prev ->
            if includeInVol then
                self.VarianceSum <- self.VarianceSum + pairwiseRealizedVariance prev bar
                self.PairCount <- self.PairCount + 1
        | ValueNone -> ()
        self.PrevBar <- ValueSome bar
        let volFactor = if self.PairCount > 0 then exp (sqrt (self.VarianceSum / float self.PairCount)) - 1.0 else 0.0
        // Emit the bar with the range as it stood BEFORE this bar contributed,
        // so the entry check `price > RangeHigh` can fire on a fresh breakout.
        // Then update the range to include this bar for future bars.
        let emittedHigh = self.RangeHigh
        let emittedLow = self.RangeLow
        if includeInRange then
            if bar.VWAP > self.RangeHigh then self.RangeHigh <- bar.VWAP
            if bar.VWAP < self.RangeLow then self.RangeLow <- bar.VWAP
        onNext {
            Bar = bar
            VolFactor = volFactor
            RangeHigh = emittedHigh
            RangeLow = emittedLow
        }

    member inline self.Process(onNext, trade: Trade, includeInVol: bool, includeInRange: bool) =
        self.BarBuilder.Process((fun bar -> self.OnBar(onNext, bar, includeInVol, includeInRange)), trade)

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

type SegregateTrades(bucketSpan: TimeSpan, baseTime) =
    member val ArgsBuilder = OrbSystemArgsBuilder(bucketSpan)
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
        let includeInVol =
            match stage with
            | LatePremarket | AfterOpeningPrint | BeforeClosing -> true
            | _ -> false
        // Track the session range from 8:30 ET all the way through the close.
        // Entries fire when price breaks above/below the running session high/low.
        let includeInRange =
            match stage with
            | LatePremarket | AfterOpeningPrint | BeforeClosing -> true
            | _ -> false
        struct (includeInVol, includeInRange)

    member inline self.Process(onNext, trade: Trade, index: int) =
        let stage = self.TradeStage(trade, index)
        let mutable lastBar = ValueNone
        let struct (inclVol, inclRange) = self.CalculateFlags stage
        self.ArgsBuilder.Process((fun bar -> lastBar <- ValueSome bar), trade, inclVol, inclRange)
        onNext (lastBar, stage, trade)

// ============================================================================
// ORB trading system (decisions from bars)
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
    /// Stop `stopVol * price * VolFactor` below/above entry price.
    | StopAtVol of stopVol: float
    /// Stop at the opposite end of the opening range (b.RangeLow for longs, b.RangeHigh for shorts).
    | StopAtRange
    /// Use range stop unless it's further than `capVol` vol units; then cap at the vol distance.
    | StopAtRangeVolCapped of capVol: float
    /// No stop — only the BeforeClosing flatten exits the position.
    | StopNever

[<Struct>]
type EntryMode =
    /// Enter when price > RangeHigh (long) or price < RangeLow (short).
    | RangeBreakout
    /// Enter on the first AfterOpeningPrint bar (no range gate).
    /// For measuring directional bias of the dataset.
    | BuyAtOpen

[<Struct>]
type Direction =
    | Long
    | Short

type OrbSystem(positionSize: float, referenceVol: float voption, stopMode: StopMode) =
    member val PositionSize = positionSize
    member val ReferenceVol = referenceVol
    member val StopMode = stopMode
    member val EntryMode = RangeBreakout with get, set
    member val Direction = Long with get, set
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
                    let shouldEnter =
                        match self.EntryMode with
                        | RangeBreakout ->
                            match self.Direction with
                            | Long -> price > b.RangeHigh
                            | Short -> price < b.RangeLow
                        | BuyAtOpen -> true
                    if position = 0 then
                        if targetShares > 0 && shouldEnter then
                            // Stop levels mirror by direction. For longs: stop below; for shorts: stop above.
                            let stopLevel =
                                match self.Direction, self.StopMode with
                                | Long, StopAtVol stopVol -> price - stopVol * price * b.VolFactor
                                | Short, StopAtVol stopVol -> price + stopVol * price * b.VolFactor
                                | Long, StopAtRange -> b.RangeLow
                                | Short, StopAtRange -> b.RangeHigh
                                | Long, StopAtRangeVolCapped capVol ->
                                    let rangeDist = price - b.RangeLow
                                    let volDist = capVol * price * b.VolFactor
                                    price - min rangeDist volDist
                                | Short, StopAtRangeVolCapped capVol ->
                                    let rangeDist = b.RangeHigh - price
                                    let volDist = capVol * price * b.VolFactor
                                    price + min rangeDist volDist
                                | Long, StopNever -> System.Double.NegativeInfinity
                                | Short, StopNever -> System.Double.PositiveInfinity
                            let signedShares =
                                match self.Direction with
                                | Long -> targetShares
                                | Short -> -targetShares
                            self.State <- Active(price, signedShares, stopLevel)
                            decision <- ValueSome { Timestamp = tradeTs; Price = price; Shares = signedShares; BarSize = barSize }
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
