#r "../TradingEdge.Vwap/bin/Debug/net10.0/TradingEdge.Vwap.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Vwap.TradeBinary

let binDir = "data/trades_bin"
let outputPath = "data/breakdown_2k.json"
let targetCount = 2000

let pairs =
    [| for tickerDir in Directory.EnumerateDirectories binDir do
        let ticker = Path.GetFileName tickerDir
        for binFile in Directory.EnumerateFiles(tickerDir, "*.bin") do
            let date = Path.GetFileNameWithoutExtension binFile
            yield ticker, date |]

printfn "Total bin files: %d" pairs.Length

let withOpening =
    pairs
    |> Array.choose (fun (t, d) ->
        let h = readHeader { Directory = binDir; Ticker = t; Date = d }
        match h.OpeningPrintIndex with
        | ValueSome _ -> Some (t, d)
        | ValueNone -> None)

printfn "With opening print: %d" withOpening.Length

let selected =
    withOpening
    |> Array.sortByDescending snd
    |> Array.truncate targetCount

printfn "Selected: %d (latest by date)" selected.Length
printfn "Date range: %s .. %s" (snd (Array.last selected)) (snd selected.[0])

let sb = StringBuilder()
sb.Append "[\n" |> ignore
selected
|> Array.iteri (fun i (t, d) ->
    let comma = if i = selected.Length - 1 then "" else ","
    sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", t, d, comma) |> ignore)
sb.Append "]\n" |> ignore
File.WriteAllText(outputPath, sb.ToString())
printfn "Wrote %s" outputPath
