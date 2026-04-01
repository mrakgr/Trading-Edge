#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "../TradingEdge.Parsing/bin/Debug/net9.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open TradingEdge.Parsing.TradeLoader


let loadTrades (filePath: string) =
    let json = File.ReadAllText(filePath)
    let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    let rawTrades = JsonSerializer.Deserialize<RawTrade[]>(json, options)
    rawTrades

let trades = loadTrades "data/trades/LW/2025-12-19.json"

for x in trades do
    let c = conditions x
    let timestamp = DateTime.UnixEpoch.AddTicks(timestamp x / 100L)
    if Array.contains 17 c || Array.contains 25 c then
        printfn "Opening print: %A" timestamp
        printfn "%A" c
    if Array.contains 19 c || Array.contains 8 c then
        printfn "Closing prints: %A" timestamp
        printfn "%A" c
        
