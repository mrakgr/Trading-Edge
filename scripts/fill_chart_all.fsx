#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "nuget: T-Digest, 1.0.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.Collections.Immutable
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Configuration -----
let positionSize = 30000.0
let referenceVol = 1.125e-6
let lossLimit = positionSize * 0.085
let basePct = 0.005
let decay = 0.9
let exponents = [| -13; -5; -6; -6 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
let fillParams = { Percentile = 0.099; DelayMs = 100.0; CommissionPerShare = 0.0035 }

// ----- Parse stocks-in-play list -----
let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
let entryRegex =
    Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")

let entries =
    File.ReadAllText ps1Path
    |> entryRegex.Matches
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Seq.distinct
    |> List.ofSeq

let availableEntries =
    entries
    |> List.filter (fun (t, d) ->
        File.Exists (sprintf "data/trades/%s/%s.json" t d))

printfn "Found %d available entries out of %d total" availableEntries.Length entries.Length

// ----- Process each entry -----
for (ticker, date) in availableEntries do
    let path = sprintf "data/trades/%s/%s.json" ticker date
    printfn "Processing %s %s..." ticker date
    try
        let trades = loadTrades path

        let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
        let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)

        match op, cp with
        | Some o, Some c ->
            let window = { openTime = o.Timestamp; closeTime = c.Timestamp }

            // Run pipeline
            let addTrade, getDecisionResult, getFillResult =
                createPipeline window pcts positionSize (Some referenceVol) 0.0 (Some lossLimit) None None (Some fillParams)
            for tr in trades do addTrade tr
            let fillResult = getFillResult()
            let decisionResult = getDecisionResult()

            // Compute bar size at 12.5m mark
            let openingPrintTime = o.Timestamp
            let cutoff = openingPrintTime.AddSeconds(750.0)
            let totalVolumeAt750s =
                trades
                |> Array.filter (fun tr -> tr.Timestamp <= cutoff)
                |> Array.sumBy (fun tr -> tr.Volume)
            let barSize = totalVolumeAt750s * pcts.[3]

            // Rebuild volume bars
            let allTrades = trades.ToImmutableArray()
            let volumeBars = rebuildVolumeBars barSize allTrades

            // Compute VWMA
            let vwmas =
                let mutable vwapSum = 0.0
                let mutable barCount = 0
                [| for b in volumeBars do
                    if b.EndTime >= openingPrintTime then
                        vwapSum <- vwapSum + b.VWAP
                        barCount <- barCount + 1
                    yield if barCount > 0 then vwapSum / float barCount else 0.0 |]

            // Output CSVs
            let outDir = sprintf "data/charts/fills/%s_%s" ticker date
            Directory.CreateDirectory outDir |> ignore

            do
                use bw = new StreamWriter(Path.Combine(outDir, "bars.csv"))
                bw.WriteLine "cumulative_volume,vwap,stddev,vwma,volume,start_time,end_time,num_trades"
                for i in 0 .. volumeBars.Length - 1 do
                    let b = volumeBars.[i]
                    bw.WriteLine(sprintf "%.2f,%.6f,%.6f,%.6f,%.2f,%s,%s,%d"
                        b.CumulativeVolume b.VWAP b.StdDev vwmas.[i]
                        b.Volume
                        (b.StartTime.ToString("o"))
                        (b.EndTime.ToString("o"))
                        b.NumTrades)

            do
                use fw = new StreamWriter(Path.Combine(outDir, "fills.csv"))
                fw.WriteLine "timestamp,price,quantity,side"
                for f in fillResult.Fills do
                    let side = if f.Quantity > 0 then "buy" else "sell"
                    fw.WriteLine(sprintf "%s,%.6f,%d,%s"
                        (f.Timestamp.ToString("o"))
                        f.Price
                        (abs f.Quantity)
                        side)

            do
                use dw = new StreamWriter(Path.Combine(outDir, "decisions.csv"))
                dw.WriteLine "timestamp,price,shares"
                for d in decisionResult.Decisions do
                    dw.WriteLine(sprintf "%s,%.6f,%d"
                        (d.Timestamp.ToString("o"))
                        d.Price
                        d.Shares)

            printfn "  %d bars, %d fills, %d decisions, fill PnL=$%.2f"
                volumeBars.Length fillResult.Fills.Length decisionResult.Decisions.Count fillResult.RealizedPnL
        | _ ->
            printfn "  Skipped (missing opening/closing print)"
    with ex ->
        printfn "  ERROR: %s" ex.Message

printfn "Done."
