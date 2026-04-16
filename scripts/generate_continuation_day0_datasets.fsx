#r "../TradingEdge.Vwap/bin/Debug/net10.0/TradingEdge.Vwap.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Vwap.TradeBinary

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"

let outInitial = "data/breakouts_rvol3plus_initial.json"
let outContReset = "data/breakouts_rvol3plus_continuation.json"

let minRvol = 3.0

let raw =
    let bytes = File.ReadAllBytes input
    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
    [| for el in doc.RootElement.EnumerateArray() ->
        {|
            ticker = el.GetProperty("ticker").GetString()
            date = el.GetProperty("date").GetString()
            breakoutDate = el.GetProperty("breakout_date").GetString()
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            rvol = el.GetProperty("rvol").GetDouble()
        |} |]

printfn "Total entries:        %d" raw.Length

// daysSince = 0 AND rvol >= minRvol (same filter as the master breakouts file).
let dayZero = raw |> Array.filter (fun r -> r.daysSince = 0 && r.rvol >= minRvol)
printfn "All daysSince=0 + RVOL>=%.1f: %d" minRvol dayZero.Length

// Split: initial breakouts (date == breakout_date) vs continuation resets (new max later in chain).
let initial = dayZero |> Array.filter (fun r -> r.date = r.breakoutDate)
let contReset = dayZero |> Array.filter (fun r -> r.date <> r.breakoutDate)
printfn "Initial SIP breakouts: %d" initial.Length
printfn "Continuation resets:   %d (same-chain later days that set new RVOL high)" contReset.Length

// Filter by bin + opening-print.
let filterTradable rows =
    rows
    |> Array.filter (fun (r: {| ticker: string; date: string; breakoutDate: string; daysSince: int; rvol: float |}) ->
        let path = Path.Combine(binDir, r.ticker, $"{r.date}.bin")
        if not (File.Exists path) then false
        else
            let h = readHeader { Directory = binDir; Ticker = r.ticker; Date = r.date }
            h.OpeningPrintIndex.IsSome)

let initialTr = filterTradable initial
let contResetTr = filterTradable contReset
printfn "After bin+opening filter: initial=%d  continuation=%d" initialTr.Length contResetTr.Length

let writeJson (path: string) (rows: {| ticker: string; date: string; breakoutDate: string; daysSince: int; rvol: float |}[]) =
    let sb = StringBuilder()
    sb.Append "[\n" |> ignore
    rows
    |> Array.iteri (fun i e ->
        let comma = if i = rows.Length - 1 then "" else ","
        sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", e.ticker, e.date, comma) |> ignore)
    sb.Append "]\n" |> ignore
    File.WriteAllText(path, sb.ToString())
    printfn "Wrote %s (%d)" path rows.Length

writeJson outInitial initialTr
writeJson outContReset contResetTr
