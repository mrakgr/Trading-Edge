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

let inline vwapSystemArgsBuilder barSize =
    let inner = volumeBarBuilder barSize
    let mutable logVwapSum = 0.0
    let mutable barCount = 0
    let mutable varianceSum = 0.0
    let mutable pairCount = 0
    let mutable prevBar = ValueOption<VolumeBar>.None
    fun onNext struct (trade, includeInVwma, includeInVol) ->
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

let defaultEstimationOffsets = [| 5.0; 30.0; 150.0; 750.0 |]

let inline segregateTrades (window : MarketHours) (volPcts : float []) =
    let mutable trades_and_flags : struct (Trade * bool * bool) ResizeArray = ResizeArray()
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
                        let totalVolume = trades_and_flags |> Seq.sumBy (fun struct (t, _, _) -> t.Volume)
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
            let trade_and_flag = struct (trade, includeInVwma, includeInVol)
            trades_and_flags.Add trade_and_flag
            match vwapSystemArgs with
            | Some vwapSystem -> vwapSystem (fun bar -> finalBar <- Some bar) trade_and_flag
            | None -> ()
            onNext (finalBar, stage, trade)

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
        let segregate = segregateTrades d.Window pcts
        for tr in d.Trades do segregate (fun _ -> ()) tr

    // Benchmark
    printfn "=== segregateTrades over all %d days ===" dayData.Length
    let mutable barCount = 0
    let sw = Stopwatch.StartNew()
    for d in dayData do
        let segregate = segregateTrades d.Window pcts
        for tr in d.Trades do
            segregate (fun (bar, _, _) ->
                if bar.IsSome then barCount <- barCount + 1
            ) tr
    sw.Stop()
    printfn "  Time: %.3fs  Bars: %d  Trades/sec: %s" sw.Elapsed.TotalSeconds barCount ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))
    0
