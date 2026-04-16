#r "../TradingEdge.Vwap/bin/Debug/net10.0/TradingEdge.Vwap.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Vwap.TradeBinary

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"

let raw =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        let getD (name: string) =
            let p = el.GetProperty name
            if p.ValueKind = JsonValueKind.Null then ValueNone
            else ValueSome (p.GetDouble())
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            breakoutDate = el.GetProperty("breakout_date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            rvol = el.GetProperty("rvol").GetDouble()
            gap = getD "gap_pct"
        |} |]

// daysSince = 0, RVOL >= 3, date != breakout_date = continuation resets.
let dayZero = raw |> Array.filter (fun r -> r.daysSince = 0 && r.rvol >= 3.0 && r.date <> r.breakoutDate)
let filterTradable rows =
    rows
    |> Array.filter (fun (r: {| ticker: string; date: string; breakoutDate: string; daysSince: int; rvol: float; gap: float voption |}) ->
        let path = Path.Combine(binDir, r.ticker, $"{r.date}.bin")
        File.Exists path
        &&
        let h = readHeader { Directory = binDir; Ticker = r.ticker; Date = r.date }
        h.OpeningPrintIndex.IsSome)
let tr = filterTradable dayZero
printfn "Continuation resets with bin+opening: %d" tr.Length

let gapUp = tr |> Array.filter (fun r -> match r.gap with ValueSome g -> g > 0.0 | _ -> false)
let gapDown = tr |> Array.filter (fun r -> match r.gap with ValueSome g -> g < 0.0 | _ -> false)
printfn "Gap up: %d  Gap down: %d" gapUp.Length gapDown.Length

let writeJson (path: string) (rows: {| ticker: string; date: string; breakoutDate: string; daysSince: int; rvol: float; gap: float voption |}[]) =
    let sb = StringBuilder()
    sb.Append "[\n" |> ignore
    rows
    |> Array.iteri (fun i e ->
        let comma = if i = rows.Length - 1 then "" else ","
        sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", e.ticker, e.date, comma) |> ignore)
    sb.Append "]\n" |> ignore
    File.WriteAllText(path, sb.ToString())
    printfn "Wrote %s (%d)" path rows.Length

writeJson "data/breakouts_rvol3plus_continuation_gapup.json" gapUp
writeJson "data/breakouts_rvol3plus_continuation_gapdown.json" gapDown
