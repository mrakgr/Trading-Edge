#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open TradingEdge.Parsing.TradeLoader

let path = "data/trades/BYND/2025-10-22.parquet"
printfn "Loading %s..." path
let sw = System.Diagnostics.Stopwatch.StartNew()
let trades = loadTrades path
sw.Stop()
printfn "Loaded %d trades in %.1fs" trades.Length sw.Elapsed.TotalSeconds

let gcInfo = GC.GetGCMemoryInfo()
printfn "Heap after load: %.1f MB" (float gcInfo.HeapSizeBytes / 1048576.0)

let openPrint = trades |> Array.tryFind (fun t -> t.Session = OpeningPrint)
let closePrint = trades |> Array.tryFind (fun t -> t.Session = ClosingPrint)
printfn "Opening print: %A" (openPrint |> Option.map (fun t -> t.Timestamp))
printfn "Closing print: %A" (closePrint |> Option.map (fun t -> t.Timestamp))

let premarket = trades |> Array.filter (fun t -> t.Session = Premarket) |> Array.length
let regular = trades |> Array.filter (fun t -> t.Session = RegularHours) |> Array.length
let postmarket = trades |> Array.filter (fun t -> t.Session = Postmarket) |> Array.length
printfn "Premarket/Regular/Postmarket: %d / %d / %d" premarket regular postmarket
