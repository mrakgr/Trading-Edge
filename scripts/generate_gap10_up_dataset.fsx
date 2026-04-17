#r "../TradingEdge.Orb/bin/Debug/net10.0/TradingEdge.Orb.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Orb.TradeBinary

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"
let output = "data/breakouts_rvol3plus_gapup10.json"

// Day-0 rows (breakouts, RVOL>=3 by construction of continuation_plays) whose
// opening gap is >= +10%. Used to compare ORB behavior on strong gap-ups vs
// the full RVOL>=3 universe.
let minGap = 0.10

let raw =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        let gap =
            let p = el.GetProperty "gap_pct"
            if p.ValueKind = JsonValueKind.Null then ValueNone
            else ValueSome (p.GetDouble())
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            gap = gap
        |} |]

printfn "Total entries:                     %d" raw.Length

let gapUp10 =
    raw
    |> Array.filter (fun r ->
        r.daysSince = 0
        && match r.gap with ValueSome g -> g >= minGap | _ -> false)
printfn "daysSince=0 + gap>=+%.0f%%:             %d" (minGap * 100.0) gapUp10.Length

let tradable =
    gapUp10
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
