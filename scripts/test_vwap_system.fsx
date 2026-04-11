#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.5.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

let tradesPath = "data/trades/LW/2025-12-19.parquet"
let desiredBarSize = 8000.0
let positionSize = 1000.0

printfn "Loading trades..."
let trades = loadTrades tradesPath
printfn "Loaded %d trades" trades.Length

// Derive market hours from the dataset
let openingPrintTime =
    trades
    |> Array.find (fun t -> t.Session = OpeningPrint)
    |> fun t -> t.Timestamp

let closingPrintTime =
    trades
    |> Array.find (fun t -> t.Session = ClosingPrint)
    |> fun t -> t.Timestamp

let window : MarketHours = {
    openTime = openingPrintTime
    closeTime = closingPrintTime
}

printfn "Opening print at: %s UTC" (window.openTime.ToString "HH:mm:ss.fff")
printfn "Closing print at: %s UTC" (window.closeTime.ToString "HH:mm:ss.fff")

let vwapSystem = createStaticVwapSystem desiredBarSize
let simulator = VwapSimulator(window, vwapSystem, positionSize)

for trade in trades do
    simulator.AddTrade trade

let result = simulator.Result
printfn ""
printfn "=== Trading Results ==="
printfn "Total decisions: %d" result.Decisions.Count
printfn "Realized P&L: $%.2f" result.RealizedPnL
printfn ""

printfn "=== Decisions ==="
for d in result.Decisions do
    let pos = if d.Shares > 0.0 then sprintf "LONG  %.0f" d.Shares
              elif d.Shares < 0.0 then sprintf "SHORT %.0f" (abs d.Shares)
              else "FLAT"
    printfn "  %s | %s @ %.2f" (d.Timestamp.ToString "HH:mm:ss.fff") pos d.Price
