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
        seg, vs


    let bench(d : {| Date: string; Header: DayHeader; Ticker: string; Trades: array<TradeRecord> |}, ctx : SinkContext) =
        let seg, vs = configure d.Header
        let onDecision (decision: TradingDecision voption, bar: VwapSystemBar voption, stage: TradeStage, _: TradeRecord) =
            match bar with
            | ValueSome b -> ctx.BarCount <- ctx.BarCount + 1; ctx.Sink <- ctx.Sink + b.Bar.VWAP
            | ValueNone -> ()
            match decision with
            | ValueSome dd -> ctx.DecisionCount <- ctx.DecisionCount + 1; ctx.Sink <- ctx.Sink + dd.Price
            | ValueNone -> ()
            stage
        for i in 0 .. d.Trades.Length - 1 do
            seg.Process(
                (fun (bar, stage, trade) ->
                    vs.Process(onDecision, bar, stage, trade, seg.Timestamp trade)),
                ReadOnlySpan(d.Trades, 0, i + 1)) |> ignore

    // Warm up
    printfn "Warming up..."
    let warmup_ctx = {BarCount = 0; DecisionCount = 0; Sink = 0.0}
    for d in dayData.[..2] do
        bench(d, warmup_ctx)

    // Benchmark
    printfn "=== VwapSystem (full pipeline) ==="
    let sw = Stopwatch.StartNew()
    let ctx = {BarCount = 0; DecisionCount = 0; Sink = 0.0}
    for d in dayData do
        bench(d, ctx)
    sw.Stop()
    printfn "  Time: %.3fs  Bars: %d  Decisions: %d  Sink: %.2f  Trades/sec: %s" sw.Elapsed.TotalSeconds ctx.BarCount ctx.DecisionCount ctx.Sink ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))
    0
