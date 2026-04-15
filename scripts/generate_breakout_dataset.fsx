#r "../TradingEdge.Vwap/bin/Debug/net10.0/TradingEdge.Vwap.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Vwap.TradeBinary

let binDir = "data/trades_bin"
let input = "data/continuation_plays.json"

// Edit these to re-target the dataset.
let output = "data/breakouts_last_6w.json"
let dollarVolCap : float voption = ValueNone   // e.g. ValueSome 100_000_000.0
let minDate : string voption = ValueSome "2026-02-27"  // inclusive; ValueNone = no floor

let entries =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            avgDollarVol = el.GetProperty("avg_dollar_volume_4w").GetDouble()
        |} |]

printfn "Total entries:        %d" entries.Length

let breakouts = entries |> Array.filter (fun e -> e.daysSince = 0)
printfn "Breakouts only:       %d" breakouts.Length

let afterDate =
    match minDate with
    | ValueSome d -> breakouts |> Array.filter (fun e -> e.date >= d)
    | ValueNone -> breakouts
printfn "After %-14s %d" (defaultArg (minDate |> ValueOption.toOption) "(none)" + ":") afterDate.Length

let underCap =
    match dollarVolCap with
    | ValueSome cap -> afterDate |> Array.filter (fun e -> e.avgDollarVol < cap)
    | ValueNone -> afterDate
match dollarVolCap with
| ValueSome cap -> printfn "Under $%.0fM avg $vol:  %d" (cap / 1_000_000.0) underCap.Length
| ValueNone -> ()

let withBin =
    underCap
    |> Array.filter (fun e ->
        File.Exists (Path.Combine(binDir, e.ticker, $"{e.date}.bin")))
printfn "With bin file:        %d" withBin.Length

let withOpening =
    withBin
    |> Array.filter (fun e ->
        let h = readHeader { Directory = binDir; Ticker = e.ticker; Date = e.date }
        h.OpeningPrintIndex.IsSome)
printfn "With opening print:   %d" withOpening.Length

let sb = StringBuilder()
sb.Append "[\n" |> ignore
withOpening
|> Array.iteri (fun i e ->
    let comma = if i = withOpening.Length - 1 then "" else ","
    sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", e.ticker, e.date, comma) |> ignore)
sb.Append "]\n" |> ignore
File.WriteAllText(output, sb.ToString())
printfn "Wrote %s" output
