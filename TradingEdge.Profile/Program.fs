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
// Class-based pipeline (separate classes with member inline onNext)
// ============================================================================

type VolumeBarOfTrades() =
    member val CumulativeVolumeSum = 0.0 with get, set

    member inline self.Process(onNext, trades: ImmutableArray<Trade>) =
        let mutable priceVolumeSum = 0.0
        let mutable priceSquaredVolumeSum = 0.0
        let mutable volumeSum = 0.0
        for t in trades do
            priceVolumeSum <- priceVolumeSum + t.Price * t.Volume
            priceSquaredVolumeSum <- priceSquaredVolumeSum + t.Price * t.Price * t.Volume
            volumeSum <- volumeSum + t.Volume
        let vwap = priceVolumeSum / volumeSum
        let variance = priceSquaredVolumeSum / volumeSum - vwap * vwap
        self.CumulativeVolumeSum <- self.CumulativeVolumeSum + volumeSum
        onNext {
            CumulativeVolume = self.CumulativeVolumeSum
            VWAP = vwap
            StdDev = sqrt (max 0.0 variance)
            Volume = volumeSum
            StartTime = trades.[0].Timestamp
            EndTime = trades.[trades.Length - 1].Timestamp
            NumTrades = trades.Length
            Trades = trades
        }

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

    let barSize = 5000.0

    // Warm up
    printfn "Warming up..."
    for d in dayData.[..2] do
        let builder = VolumeBarBuilder(barSize)
        for tr in d.Trades do builder.Process((fun _ -> ()), tr)

    // Benchmark
    printfn "=== VolumeBarBuilder (member inline) ==="
    let mutable barCount = 0
    let mutable sink = 0.0
    let sw = Stopwatch.StartNew()
    for d in dayData do
        let builder = VolumeBarBuilder(barSize)
        for tr in d.Trades do
            builder.Process((fun bar -> barCount <- barCount + 1; sink <- sink + bar.VWAP), tr)
    sw.Stop()
    printfn "  Time: %.3fs  Bars: %d  Sink: %.2f  Trades/sec: %s" sw.Elapsed.TotalSeconds barCount sink ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))
    0
