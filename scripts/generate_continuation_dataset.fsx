#r "../TradingEdge.Orb/bin/Debug/net10.0/TradingEdge.Orb.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Orb.TradeBinary

// "Continuation" = every row in continuation_plays_augmented.json whose `date`
// differs from its `breakout_date`. That keeps:
//   - tail days of each pump (daysSince > 0), and
//   - continuation resets — days that hit a new RVOL high *without* the gap
//     that defines a breakout (daysSince = 0, date != breakout_date).
// It drops day-0 breakouts. Any RVOL.

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"
let output = "data/breakouts_continuations.json"

let raw =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            breakoutDate = el.GetProperty("breakout_date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
        |} |]

printfn "Total entries:                     %d" raw.Length

let continuations =
    raw |> Array.filter (fun r -> r.date <> r.breakoutDate)
printfn "date != breakout_date:             %d" continuations.Length
let resets = continuations |> Array.filter (fun r -> r.daysSince = 0) |> Array.length
let tails  = continuations.Length - resets
printfn "  of which daysSince=0 (resets):   %d" resets
printfn "  of which daysSince>0 (tails):    %d" tails

let tradable =
    continuations
    |> Array.filter (fun r ->
        let path = Path.Combine(binDir, r.ticker, $"{r.date}.bin")
        File.Exists path
        &&
        let h = readHeader { Directory = binDir; Ticker = r.ticker; Date = r.date }
        h.OpeningPrintIndex.IsSome)
printfn "After bin + opening-print filter:  %d" tradable.Length

let sb = StringBuilder()
sb.Append "[\n" |> ignore
tradable
|> Array.iteri (fun i e ->
    let comma = if i = tradable.Length - 1 then "" else ","
    sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", e.ticker, e.date, comma) |> ignore)
sb.Append "]\n" |> ignore
File.WriteAllText(output, sb.ToString())
printfn "Wrote %s (%d)" output tradable.Length
