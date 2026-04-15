#r "../TradingEdge.Vwap/bin/Debug/net10.0/TradingEdge.Vwap.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Vwap.TradeBinary

let binDir = "data/trades_bin"
let input = "data/continuation_plays.json"

// Edit these to re-target the dataset.
let output = "data/breakouts_rvol3plus.json"
let minRvol : float voption = ValueSome 3.0
let dollarVolCap : float voption = ValueNone   // e.g. ValueSome 100_000_000.0
let minDate : string voption = ValueNone

let entries =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            rvol = el.GetProperty("rvol").GetDouble()
            avgDollarVol = el.GetProperty("avg_dollar_volume_4w").GetDouble()
        |} |]

printfn "Total entries:        %d" entries.Length

let breakouts = entries |> Array.filter (fun e -> e.daysSince = 0)
printfn "Breakouts only:       %d" breakouts.Length

let afterRvol =
    match minRvol with
    | ValueSome r -> breakouts |> Array.filter (fun e -> e.rvol >= r)
    | ValueNone -> breakouts
match minRvol with
| ValueSome r -> printfn "RVOL >= %.1f:          %d" r afterRvol.Length
| ValueNone -> ()

let afterDate =
    match minDate with
    | ValueSome d -> afterRvol |> Array.filter (fun e -> e.date >= d)
    | ValueNone -> afterRvol
match minDate with
| ValueSome d -> printfn "After %s:      %d" d afterDate.Length
| ValueNone -> ()

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
