#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net9.0/TradingEdge.Parsing.dll"

open System
open System.IO
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VolumeBars

let ticker = "LW"
let date = DateTime(2025, 12, 19)
let dbPath = "data/trading.db"
let tradesPath = "data/trades/LW/2025-12-19.json"

printfn "Loading trades..."
let trades = loadTrades tradesPath

let openingPrints = trades |> Array.filter (fun t -> t.Session = OpeningPrint)
printfn "Found %d opening print trades" openingPrints.Length
if openingPrints.Length > 0 then
    printfn "First opening print at: %s" (openingPrints.[0].Timestamp.ToString("HH:mm:ss"))

printfn "Getting average volume..."
let avgVolume = getAvgVolume ticker (Some date) dbPath

match avgVolume with
| Some avg ->
    printfn "Average volume: %.0f" avg
    let barSize = calculateBarSize avg
    printfn "Bar size: %.0f" barSize

    printfn "Creating volume bars..."
    let bars = createVolumeBars trades barSize
    printfn "Created %d bars" bars.Length

    // Find opening print bar
    let openingPrintBar = bars |> Array.tryFindIndex (fun b -> b.VWMA > 0.0)
    match openingPrintBar with
    | Some idx -> printfn "Opening print starts at bar %d (time: %s)" idx (bars.[idx].StartTime.ToString("HH:mm:ss"))
    | None -> printfn "No opening print found"

    // Output to CSV
    let outputPath = "data/volume_bars_LW_2025-12-19.csv"
    use writer = new StreamWriter(outputPath)
    writer.WriteLine("cumulative_volume,vwap,stddev,volume,start_time,end_time,num_trades,vwma")

    for bar in bars do
        writer.WriteLine(sprintf "%.0f,%.4f,%.6f,%.0f,%s,%s,%d,%.4f"
            bar.CumulativeVolume
            bar.VWAP
            bar.StdDev
            bar.Volume
            (bar.StartTime.ToString("o"))
            (bar.EndTime.ToString("o"))
            bar.NumTrades
            bar.VWMA)

    printfn "Saved to %s" outputPath
| None ->
    printfn "Could not get average volume"
