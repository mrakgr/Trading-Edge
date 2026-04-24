#r "../TradingEdge.Orb/bin/Debug/net10.0/TradingEdge.Orb.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Orb.TradeBinary

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"
let outDir = "data"

let minRvol = 3.0          // breakout day's RVOL
let minContRvol = 3.0      // continuation day's RVOL (volume-confirmation filter)
let cirHigh = 0.80
let cirLow = 0.20
let days = [| 1; 2; 3; 4; 5 |]

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
            cvo = getD "close_vs_open_pct"
            cir = getD "close_in_range_pct"
        |} |]

printfn "Total entries:     %d" raw.Length
let byKey = raw |> Array.map (fun r -> (r.ticker, r.date), r) |> dict

let toPairs rows =
    rows
    |> Array.map (fun ((r: {| ticker: string; date: string; breakoutDate: string; daysSince: int; rvol: float; gap: float voption; cvo: float voption; cir: float voption |}), _) ->
        r.ticker, r.date)

let writeJson (path: string) (rows: (string * string) array) =
    let sb = StringBuilder()
    sb.Append "[\n" |> ignore
    rows
    |> Array.iteri (fun i (ticker, date) ->
        let comma = if i = rows.Length - 1 then "" else ","
        sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", ticker, date, comma) |> ignore)
    sb.Append "]\n" |> ignore
    File.WriteAllText(path, sb.ToString())
    printfn "  Wrote %s (%d)" path rows.Length

for n in days do
    printfn ""
    printfn "=== Day %d continuations ===" n
    let dayN = raw |> Array.filter (fun r -> r.daysSince = n)
    let withBreakout =
        dayN
        |> Array.choose (fun r ->
            match byKey.TryGetValue ((r.ticker, r.breakoutDate)) with
            | true, b when b.daysSince = 0 && b.rvol >= minRvol -> Some (r, b)
            | _ -> None)
    let tradable =
        withBreakout
        |> Array.filter (fun (r, _) ->
            let path = Path.Combine(binDir, r.ticker, $"{r.date}.bin")
            if not (File.Exists path) then false
            else
                let h = readHeader { Directory = binDir; Ticker = r.ticker; Date = r.date }
                h.OpeningPrintIndex.IsSome)
    let bullishCont =
        tradable
        |> Array.filter (fun (_, b) ->
            match b.gap, b.cir with
            | ValueSome g, ValueSome c -> g > 0.0 && c >= cirHigh
            | _ -> false)
    let bearishCont =
        tradable
        |> Array.filter (fun (_, b) ->
            match b.gap, b.cir with
            | ValueSome g, ValueSome c -> g < 0.0 && c <= cirLow
            | _ -> false)
    let bullishHi = bullishCont |> Array.filter (fun (r, _) -> r.rvol >= minContRvol)
    let bearishHi = bearishCont |> Array.filter (fun (r, _) -> r.rvol >= minContRvol)
    printfn "  dayN raw:    %d   tradable: %d" dayN.Length tradable.Length
    printfn "  bullish:     %d (after breakout filter) -> %d (day-%d RVOL>=%.1f)" bullishCont.Length bullishHi.Length n minContRvol
    printfn "  bearish:     %d (after breakout filter) -> %d (day-%d RVOL>=%.1f)" bearishCont.Length bearishHi.Length n minContRvol
    writeJson (Path.Combine(outDir, sprintf "cont_day%d_after_gapup_highclose_rvol3plus.json" n)) (toPairs bullishHi)
    writeJson (Path.Combine(outDir, sprintf "cont_day%d_after_gapdown_lowclose_rvol3plus.json" n)) (toPairs bearishHi)
