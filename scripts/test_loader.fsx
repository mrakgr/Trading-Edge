#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "../TradingEdge.Parsing/bin/Debug/net9.0/TradingEdge.Parsing.dll"

open TradingEdge.Parsing.TradeLoader

let trades = loadTrades "data/trades/LW/2025-12-19.parquet"

printfn "Loaded %d trades" trades.Length

let sessionCounts =
    trades
    |> Array.groupBy (fun t -> t.Session)
    |> Array.map (fun (session, trades) -> session, trades.Length)
    |> Array.sortBy fst

printfn "\nSession breakdown:"
for (session, count) in sessionCounts do
    printfn "  %A: %d trades" session count

let openingPrints = trades |> Array.filter (fun t -> t.Session = OpeningPrint)
let closingPrints = trades |> Array.filter (fun t -> t.Session = ClosingPrint)

printfn "\nOpening prints:"
for t in openingPrints do
    printfn "  %A at $%.2f" t.Timestamp t.Price

printfn "\nClosing prints:"
for t in closingPrints do
    printfn "  %A at $%.2f" t.Timestamp t.Price
