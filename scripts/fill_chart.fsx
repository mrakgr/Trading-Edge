#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "nuget: T-Digest, 1.0.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.Collections.Immutable
open System.IO
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Configuration -----
let ticker = fsi.CommandLineArgs.[1]
let date = fsi.CommandLineArgs.[2]

#load "config.fsx"
open Config

// ----- Load trades -----
let path = sprintf "data/trades/%s/%s.json" ticker date
printfn "Loading %s..." path
let trades = loadTrades path

let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)

match op, cp with
| None, _ | _, None ->
    eprintfn "Missing opening or closing print for %s %s" ticker date
    exit 1
| _ -> ()

let window = { openTime = op.Value.Timestamp; closeTime = cp.Value.Timestamp }

// ----- Run pipeline to get fills and decisions -----
let addTrade, getDecisionResult, getFillResult =
    createPipeline window pcts positionSize (Some referenceVol) 0.0 (Some lossLimit) None None (Some fillParams)

for tr in trades do addTrade tr

let fillResult = getFillResult()
let decisionResult = getDecisionResult()

printfn "Decisions: %d  Fills: %d" decisionResult.Decisions.Count fillResult.Fills.Length
printfn "Decision PnL: $%.2f  Fill PnL: $%.2f (commission: $%.2f)"
    decisionResult.RealizedPnL fillResult.RealizedPnL fillResult.Commissions

// ----- Compute bar size at 12.5m mark (750s offset) -----
// This mirrors segregateTrades: totalVolume of all trades up to 750s after opening print,
// times volPcts[3] (the last estimation offset).
let openingPrintTime = op.Value.Timestamp
let cutoff = openingPrintTime.AddSeconds(750.0)
let totalVolumeAt750s =
    trades
    |> Array.filter (fun tr -> tr.Timestamp <= cutoff)
    |> Array.sumBy (fun tr -> tr.Volume)
let barSize = totalVolumeAt750s * pcts.[3]
printfn "Bar size at 12.5m: %.0f (totalVol=%.0f * pct=%.5f)" barSize totalVolumeAt750s pcts.[3]

// ----- Rebuild volume bars with consistent bar size -----
let allTrades = trades.ToImmutableArray()
let volumeBars = rebuildVolumeBars barSize allTrades

// ----- Compute VWMA over the rebuilt bars -----
// VWMA = running average of bar VWAPs, only for bars after opening print
let vwmas =
    let mutable vwapSum = 0.0
    let mutable barCount = 0
    [| for b in volumeBars do
        if b.EndTime >= openingPrintTime then
            vwapSum <- vwapSum + b.VWAP
            barCount <- barCount + 1
        yield if barCount > 0 then vwapSum / float barCount else 0.0 |]

printfn "Rebuilt %d volume bars" volumeBars.Length

// ----- Output CSVs -----
let outDir = sprintf "data/charts/fills/%s_%s" ticker date
Directory.CreateDirectory outDir |> ignore

// Bars CSV
let barsPath = Path.Combine(outDir, "bars.csv")
do
    use bw = new StreamWriter(barsPath)
    bw.WriteLine "cumulative_volume,vwap,stddev,vwma,volume,start_time,end_time,num_trades"
    for i in 0 .. volumeBars.Length - 1 do
        let b = volumeBars.[i]
        bw.WriteLine(sprintf "%.2f,%.6f,%.6f,%.6f,%.2f,%s,%s,%d"
            b.CumulativeVolume b.VWAP b.StdDev vwmas.[i]
            b.Volume
            (b.StartTime.ToString("o"))
            (b.EndTime.ToString("o"))
            b.NumTrades)
    printfn "Wrote %s (%d bars)" barsPath volumeBars.Length

// Fills CSV — include cumulative volume at fill time for x-axis placement
let fillsPath = Path.Combine(outDir, "fills.csv")
do
    use fw = new StreamWriter(fillsPath)
    fw.WriteLine "timestamp,price,quantity,side"
    for f in fillResult.Fills do
        let side = if f.Quantity > 0 then "buy" else "sell"
        fw.WriteLine(sprintf "%s,%.6f,%d,%s"
            (f.Timestamp.ToString("o"))
            f.Price
            (abs f.Quantity)
            side)
    printfn "Wrote %s (%d fills)" fillsPath fillResult.Fills.Length

// Decisions CSV
let decisionsPath = Path.Combine(outDir, "decisions.csv")
do
    use dw = new StreamWriter(decisionsPath)
    dw.WriteLine "timestamp,price,shares"
    for d in decisionResult.Decisions do
        dw.WriteLine(sprintf "%s,%.6f,%d"
            (d.Timestamp.ToString("o"))
            d.Price
            d.Shares)
    printfn "Wrote %s (%d decisions)" decisionsPath decisionResult.Decisions.Count
