module TradingEdge.Profile.Program

open System
open System.IO
open System.Collections.Immutable
open System.Text.RegularExpressions
open System.Diagnostics
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.TradeBinary
open TradingEdge.Parsing.VolumeBars

// ============================================================================
// Local copy of segregateTrades and dependencies for profiling/optimization.
// Changes here can be benchmarked before porting back to VwapSystem.fs.
// ============================================================================

let inline volumeBarOfTrades () =
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

let inline groupTrades barSize =
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

let inline volumeBarBuilder barSize =
    let tradesGrouper = groupTrades barSize
    let barBuilder = volumeBarOfTrades ()
    fun onNext trade -> tradesGrouper (barBuilder onNext) trade

let pairwiseRealizedVariance (a: VolumeBar) (b: VolumeBar) =
    assert (a.Volume = b.Volume)
    log (a.VWAP / b.VWAP) ** 2

type VwapSystemBar = {
    Bar : VolumeBar
    Vwma : float
    VolFactor : float
}

type VwapSystemArgsBuilder(barSize: float) =
    let inner = volumeBarBuilder barSize
    let mutable logVwapSum = 0.0
    let mutable barCount = 0
    let mutable varianceSum = 0.0
    let mutable pairCount = 0
    let mutable prevBar = ValueOption<VolumeBar>.None
    let mutable lastBar = ValueOption<VwapSystemBar>.None

    member _.Process(trade: Trade, includeInVwma: bool, includeInVol: bool) : VwapSystemBar voption =
        lastBar <- ValueNone
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
            lastBar <- ValueSome {
                Bar = bar
                Vwma = vwma
                VolFactor = volFactor
            }
        ) trade
        lastBar

type TradeStage =
    | EarlyPremarket
    | LatePremarket
    | AfterOpeningPrint
    | BeforeClosing

type MarketHours = {
    openTime : DateTime
    closeTime : DateTime
}

let defaultEstimationOffsets = [| 5.0; 30.0; 150.0; 750.0 |]

type SegregateTrades(volPcts: float[]) =
    let mutable vwapSystemArgs = None
    let mutable volPctsOffset = ValueNone
    let closing_pause = -60.0

    member val OpenTime = DateTime.MaxValue with get, set
    member val CloseTime = DateTime.MaxValue with get, set

    member self.TradeStage(trade : Trade) = 
        if self.OpenTime <= trade.Timestamp then
            if self.CloseTime.AddSeconds closing_pause <= trade.Timestamp then
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

    member self.CalculateFlags(stage : TradeStage) =
        let includeInVwma =
            match stage with
            | AfterOpeningPrint | BeforeClosing -> true
            | _ -> false
        let includeInVol =
            match stage with
            | LatePremarket | AfterOpeningPrint | BeforeClosing -> true
            | _ -> false
        struct (includeInVwma, includeInVol)

    member self.Process(trades: ReadOnlySpan<Trade>) : TradeStage =
        let trade = trades.[trades.Length - 1]
        let stage = self.TradeStage trade
        match stage with
        | AfterOpeningPrint ->
            let currentOffset = (trade.Timestamp - self.OpenTime).TotalSeconds
            let mutable i = ValueNone
            let mutable k = defaultEstimationOffsets.Length - 1
            while k >= 0 && i.IsNone do
                if defaultEstimationOffsets.[k] <= currentOffset then
                    i <- ValueSome k
                k <- k - 1
            if volPctsOffset <> i then
                volPctsOffset <- i
                match volPctsOffset with
                | ValueSome vpIdx ->
                    let mutable totalVolume = 0.0
                    for j in 0 .. trades.Length - 1 do
                        totalVolume <- totalVolume + trades.[j].Volume
                    // TODO: replay through vwapSystemArgsBuilder
                    let _barSize = totalVolume * volPcts.[vpIdx]
                    ()
                | ValueNone -> ()
        | _ -> 
            ()
        stage

// ============================================================================
// Benchmark harness
// ============================================================================

let pcts =
    let basePct = 0.005
    let decay = 0.9
    [| -8.69; -1.10; -16.27; -16.73 |] |> Array.map (fun i -> basePct * (decay ** i))

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
                         Window = { openTime = o.Timestamp; closeTime = c.Timestamp } |}
            | _ -> () |]
    swLoad.Stop()
    let totalTrades = dayData |> Array.sumBy (fun d -> int64 d.Trades.Length)
    printfn "Loaded %d days (%s trades) in %.3fs\n" dayData.Length (totalTrades.ToString("N0")) swLoad.Elapsed.TotalSeconds

    // Warm up
    printfn "Warming up..."
    for d in dayData.[..2] do
        let segregate = SegregateTrades(pcts)
        segregate.OpenTime <- d.Window.openTime
        segregate.CloseTime <- d.Window.closeTime
        for i in 0 .. d.Trades.Length - 1 do
            segregate.Process(ReadOnlySpan(d.Trades, 0, i + 1)) |> ignore

    // Benchmark
    printfn "=== segregateTrades over all %d days ===" dayData.Length
    let mutable sink = 0.0
    let sw = Stopwatch.StartNew()
    for d in dayData do
        let segregate = SegregateTrades(pcts)
        segregate.OpenTime <- d.Window.openTime
        segregate.CloseTime <- d.Window.closeTime
        for i in 0 .. d.Trades.Length - 1 do
            let stage = segregate.Process(ReadOnlySpan(d.Trades, 0, i + 1))
            let mult = match stage with AfterOpeningPrint -> 2.0 | _ -> 1.0
            sink <- sink + d.Trades.[i].Price * mult
    sw.Stop()
    printfn "  Time: %.3fs  Sink: %.2f  Trades/sec: %s" sw.Elapsed.TotalSeconds sink ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))
    0
