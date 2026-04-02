#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

let tradesPath = "data/trades/LW/2025-12-19.json"
let desiredBarSize = 8000.0
let positionSize = 1000.0

printfn "Loading trades..."
let trades = loadTrades tradesPath
printfn "Loaded %d trades" trades.Length

// Find closing print time to set flatten time (1 minute before close)
let closingPrintTime =
    trades
    |> Array.tryFind (fun t -> t.Session = ClosingPrint)
    |> Option.map (fun t -> t.Timestamp)
    |> Option.defaultValue (DateTime(2025, 12, 19, 21, 0, 0, DateTimeKind.Utc))

let flattenTime = closingPrintTime.AddMinutes -1.0
printfn "Closing print at: %s UTC" (closingPrintTime.ToString "HH:mm:ss")
printfn "Flatten time: %s UTC" (flattenTime.ToString "HH:mm:ss")

let system = createVwapSystem()
let simulator = TradingSimulator(system, desiredBarSize, positionSize, flattenTime)

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
    let side = match d.Side with Long -> "LONG" | Short -> "SHORT"
    printfn "  %s | %-5s | %.2f x %.1f shares" (d.Timestamp.ToString "HH:mm:ss.fff") side d.Price d.Shares
