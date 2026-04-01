#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "../TradingEdge.Parsing/bin/Debug/net9.0/TradingEdge.Parsing.dll"

open TradingEdge.Parsing.TradeLoader

let trades = loadTrades "data/trades/LW/2025-12-19.json"

printfn "Loaded %d trades" trades.Length
printfn "First trade: %A" trades.[0]
printfn "Last trade: %A" trades.[trades.Length - 1]

let sessionCounts =
    trades
    |> Array.groupBy (fun t -> t.Session)
    |> Array.map (fun (session, trades) -> session, trades.Length)
    |> Array.sortBy fst

printfn "\nSession breakdown:"
for (session, count) in sessionCounts do
    printfn "  %A: %d trades" session count
