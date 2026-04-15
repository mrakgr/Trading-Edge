#r "../TradingEdge.Vwap/bin/Debug/net10.0/TradingEdge.Vwap.dll"

open System
open System.IO
open System.Text.Json
open TradingEdge.Vwap
open TradingEdge.Vwap.TradeBinary

let jsonPath = "data/continuation_plays.json"
let binDir = "data/trades_bin"

let pairs =
    let bytes = File.ReadAllBytes jsonPath
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        el.GetProperty("ticker").GetString(), el.GetProperty("date").GetString() |]
    |> Array.distinct

printfn "Expected pairs from JSON: %d" pairs.Length

let present, missing =
    pairs |> Array.partition (fun (t, d) ->
        File.Exists (Path.Combine(binDir, t, $"{d}.bin")))

printfn "Binary files present:     %d" present.Length
printfn "Binary files missing:     %d" missing.Length
if missing.Length > 0 && missing.Length <= 20 then
    for t, d in missing do printfn "  MISSING %s/%s" t d

let mutable totalTrades = 0L
let mutable withOpening = 0
let mutable withoutOpening = 0
let mutable totalBytes = 0L

for t, d in present do
    let info = { Directory = binDir; Ticker = t; Date = d }
    let h = readHeader info
    totalTrades <- totalTrades + int64 h.TradeCount
    match h.OpeningPrintIndex with
    | ValueSome _ -> withOpening <- withOpening + 1
    | ValueNone -> withoutOpening <- withoutOpening + 1
    totalBytes <- totalBytes + FileInfo(infoPath info).Length

printfn ""
printfn "Total trades:             %s" (totalTrades.ToString("N0"))
printfn "With openingPrint:        %d (%.2f%%)" withOpening (100.0 * float withOpening / float present.Length)
printfn "Without openingPrint:     %d (%.2f%%)" withoutOpening (100.0 * float withoutOpening / float present.Length)
printfn "Total size on disk:       %.1f MB" (float totalBytes / 1024.0 / 1024.0)
printfn "Avg trades/day:           %s" ((totalTrades / int64 present.Length).ToString("N0"))
