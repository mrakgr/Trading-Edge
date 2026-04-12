module TradingEdge.Profile.Program

open System
open System.IO
open System.Collections.Immutable
open System.Text.RegularExpressions
open System.Diagnostics
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.TradeBinary
// ============================================================================
// VolumeBar — trades + cumulative volume; derived fields computed on demand
// ============================================================================

type VolumeBar(trades: ImmutableArray<Trade>, cumulativeVolume: float) =
    let mutable cachedVwap = ValueNone
    let mutable cachedStdDev = ValueNone
    let mutable cachedVolume = ValueNone

    member _.Trades = trades
    member _.CumulativeVolume = cumulativeVolume
    member _.NumTrades = trades.Length
    member _.StartTime = trades.[0].Timestamp
    member _.EndTime = trades.[trades.Length - 1].Timestamp

    member self.Volume =
        match cachedVolume with
        | ValueSome v -> v
        | ValueNone ->
            let mutable sum = 0.0
            for t in trades do sum <- sum + t.Volume
            cachedVolume <- ValueSome sum
            sum

    member self.VWAP =
        match cachedVwap with
        | ValueSome v -> v
        | ValueNone ->
            let mutable pvSum = 0.0
            let mutable vSum = 0.0
            for t in trades do
                pvSum <- pvSum + t.Price * t.Volume
                vSum <- vSum + t.Volume
            let vwap = pvSum / vSum
            cachedVwap <- ValueSome vwap
            vwap

    member self.StdDev =
        match cachedStdDev with
        | ValueSome v -> v
        | ValueNone ->
            let mutable pvSum = 0.0
            let mutable pv2Sum = 0.0
            let mutable vSum = 0.0
            for t in trades do
                pvSum <- pvSum + t.Price * t.Volume
                pv2Sum <- pv2Sum + t.Price * t.Price * t.Volume
                vSum <- vSum + t.Volume
            let vwap = pvSum / vSum
            let variance = pv2Sum / vSum - vwap * vwap
            let sd = sqrt (max 0.0 variance)
            cachedStdDev <- ValueSome sd
            cachedVwap <- ValueSome vwap  // compute VWAP as a side effect
            sd

// ============================================================================
// Class-based pipeline (separate classes with member inline onNext)
// ============================================================================

type VolumeBarOfTrades() =
    member val CumulativeVolumeSum = 0.0 with get, set

    member inline self.Process(onNext, trades: ImmutableArray<Trade>) =
        let mutable volumeSum = 0.0
        for t in trades do
            volumeSum <- volumeSum + t.Volume
        self.CumulativeVolumeSum <- self.CumulativeVolumeSum + volumeSum
        onNext (VolumeBar(trades, self.CumulativeVolumeSum))

type GroupTrades(barSize: float) =
    member val BarSize = barSize
    member val CurrentTrades = ImmutableArray.CreateBuilder<Trade>()
    member val CurrentVolumeSum = 0.0 with get, set

    member inline self.Process(onNext, trade: Trade) =
        let mutable remaining = trade.Volume
        while remaining > 0.0 do
            let spaceLeft = self.BarSize - self.CurrentVolumeSum
            if remaining <= spaceLeft then
                self.CurrentTrades.Add { trade with Volume = remaining }
                self.CurrentVolumeSum <- self.CurrentVolumeSum + remaining
                remaining <- 0.0
            else
                if spaceLeft > 0.0 then
                    self.CurrentTrades.Add { trade with Volume = spaceLeft }
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
// Trade segregator
// ============================================================================

[<Struct>]
type TradeStage =
    | EarlyPremarket
    | LatePremarket
    | AfterOpeningPrint
    | BeforeClosing

[<Struct>]
type MarketHours = {
    openTime : DateTime
    closeTime : DateTime
}

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
    member val VolPctsOffset = ValueNone with get, set
    member val VwapSystem : VwapSystemArgsBuilder voption = ValueNone with get, set
    member val ClosingPause = -60.0
    member val OpenTime = DateTime.MaxValue with get, set
    member val CloseTime = DateTime.MaxValue with get, set
    member val LastBar = ValueOption<VwapSystemBar>.None with get, set

    member self.TradeStage(trade: Trade) =
        if self.OpenTime <= trade.Timestamp then
            if self.CloseTime.AddSeconds self.ClosingPause <= trade.Timestamp then
                BeforeClosing
            else
                AfterOpeningPrint
        else
            if trade.Session = OpeningPrint then
                AfterOpeningPrint
            elif self.OpenTime.AddHours(-1.0) <= trade.Timestamp then
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

    member self.FeedTrade(stage: TradeStage, trade: Trade) =
        match self.VwapSystem with
        | ValueSome sys ->
            let struct (inclVwma, inclVol) = self.CalculateFlags stage
            sys.Process((fun bar -> self.LastBar <- ValueSome bar), trade, inclVwma, inclVol)
        | ValueNone -> ()

    member self.Process(trades: ReadOnlySpan<Trade>) : TradeStage =
        let trade = trades.[trades.Length - 1]
        let stage = self.TradeStage trade
        self.LastBar <- ValueNone
        match stage with
        | AfterOpeningPrint ->
            let currentOffset = (trade.Timestamp - self.OpenTime).TotalSeconds
            let i = findEstimationOffset currentOffset
            if self.VolPctsOffset <> i then
                self.VolPctsOffset <- i
                match self.VolPctsOffset with
                | ValueSome vpIdx ->
                    let mutable totalVolume = 0.0
                    for j in 0 .. trades.Length - 1 do
                        totalVolume <- totalVolume + trades.[j].Volume
                    let barSize = totalVolume * volPcts.[vpIdx]
                    let newSystem = VwapSystemArgsBuilder(barSize)
                    self.VwapSystem <- ValueSome newSystem
                    for j in 0 .. trades.Length - 1 do
                        let t = trades.[j]
                        self.FeedTrade(self.TradeStage t, t)
                | ValueNone -> ()
            else
                self.FeedTrade(stage, trade)
        | _ ->
            self.FeedTrade(stage, trade)
        stage

// ============================================================================
// Benchmark harness
// ============================================================================

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
            let _, trades = loadDay path
            let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
            let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
            match op, cp with
            | Some o, Some c ->
                yield {| Ticker = ticker; Date = date; Trades = trades
                         Window = struct (o.Timestamp, c.Timestamp) |}
            | _ -> () |]
    swLoad.Stop()
    let totalTrades = dayData |> Array.sumBy (fun d -> int64 d.Trades.Length)
    printfn "Loaded %d days (%s trades) in %.3fs\n" dayData.Length (totalTrades.ToString("N0")) swLoad.Elapsed.TotalSeconds

    let pcts =
        let basePct = 0.005
        let decay = 0.9
        [| -8.69; -1.10; -16.27; -16.73 |] |> Array.map (fun i -> basePct * (decay ** i))

    // Warm up
    printfn "Warming up..."
    for d in dayData.[..2] do
        let struct (openTime, closeTime) = d.Window
        let seg = SegregateTrades(pcts)
        seg.OpenTime <- openTime
        seg.CloseTime <- closeTime
        for i in 0 .. d.Trades.Length - 1 do
            seg.Process(ReadOnlySpan(d.Trades, 0, i + 1)) |> ignore

    // Benchmark
    printfn "=== SegregateTrades (full pipeline) ==="
    let mutable barCount = 0
    let mutable sink = 0.0
    let sw = Stopwatch.StartNew()
    for d in dayData do
        let struct (openTime, closeTime) = d.Window
        let seg = SegregateTrades(pcts)
        seg.OpenTime <- openTime
        seg.CloseTime <- closeTime
        for i in 0 .. d.Trades.Length - 1 do
            seg.Process(ReadOnlySpan(d.Trades, 0, i + 1)) |> ignore
            match seg.LastBar with
            | ValueSome bar -> barCount <- barCount + 1; sink <- sink + bar.Bar.VWAP
            | ValueNone -> ()
    sw.Stop()
    printfn "  Time: %.3fs  Bars: %d  Sink: %.2f  Trades/sec: %s" sw.Elapsed.TotalSeconds barCount sink ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))
    0
