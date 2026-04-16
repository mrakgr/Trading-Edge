#r "../TradingEdge.Vwap/bin/Debug/net10.0/TradingEdge.Vwap.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Vwap.TradeBinary

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"

let outBullishCont = "data/cont_day1_after_gapup_highclose.json"
let outBearishCont = "data/cont_day1_after_gapdown_lowclose.json"
let outBullishContHiVol = "data/cont_day1_after_gapup_highclose_rvol3plus.json"
let outBearishContHiVol = "data/cont_day1_after_gapdown_lowclose_rvol3plus.json"

let minRvol = 3.0
let minDay1Rvol = 3.0   // continuation day's own RVOL — null filter for the wide versions
let cirHigh = 0.80   // breakout closed in the top 20% of the daily range
let cirLow = 0.20    // breakout closed in the bottom 20%

// Load everything.
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

// Index by (ticker, date) so we can look up the breakout day from a day-1 row.
let byKey =
    raw
    |> Array.map (fun r -> (r.ticker, r.date), r)
    |> dict

// Day-1 continuations: days_since_max_rvol_day = 1.
let day1 = raw |> Array.filter (fun r -> r.daysSince = 1)
printfn "Day-1 continuations: %d" day1.Length

// For each day-1 row, look up the breakout row (same ticker, date = breakout_date).
let withBreakout =
    day1
    |> Array.choose (fun r ->
        match byKey.TryGetValue ((r.ticker, r.breakoutDate)) with
        | true, b when b.daysSince = 0 && b.rvol >= minRvol -> Some (r, b)
        | _ -> None)
printfn "With RVOL>=%.1f breakout ref: %d" minRvol withBreakout.Length

// Filter by bin + opening-print on the day-1 itself (the day we'll trade).
let tradable =
    withBreakout
    |> Array.filter (fun (r, _) ->
        let path = Path.Combine(binDir, r.ticker, $"{r.date}.bin")
        if not (File.Exists path) then false
        else
            let h = readHeader { Directory = binDir; Ticker = r.ticker; Date = r.date }
            h.OpeningPrintIndex.IsSome)
printfn "Tradable on day 1:  %d" tradable.Length

// Apply the two filters on the breakout day.
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

// Same filters, but also require the continuation day itself to have RVOL >= minDay1Rvol.
let bullishContHiVol = bullishCont |> Array.filter (fun (r, _) -> r.rvol >= minDay1Rvol)
let bearishContHiVol = bearishCont |> Array.filter (fun (r, _) -> r.rvol >= minDay1Rvol)

printfn ""
printfn "Bullish-continuation candidates (breakout gapped up + closed in top 20%% of range): %d" bullishCont.Length
printfn "Bearish-continuation candidates (breakout gapped down + closed in bot 20%% of range): %d" bearishCont.Length
printfn "  of which day-1 RVOL >= %.1f: bullish=%d  bearish=%d" minDay1Rvol bullishContHiVol.Length bearishContHiVol.Length

// Diagnostic: breakout-day gap/CIR distribution for each cohort.
let pct (arr: float[]) q = arr.[int (float arr.Length * q) |> min (arr.Length - 1) |> max 0]

let printCohort name rows =
    if Array.isEmpty rows then printfn "%s: empty" name
    else
        let gaps =
            rows
            |> Array.map (fun (_, (b: {| ticker: string; date: string; breakoutDate: string; daysSince: int; rvol: float; gap: float voption; cvo: float voption; cir: float voption |})) ->
                (b.gap |> ValueOption.defaultValue 0.0) * 100.0)
            |> Array.sort
        let cirs =
            rows
            |> Array.map (fun (_, (b: {| ticker: string; date: string; breakoutDate: string; daysSince: int; rvol: float; gap: float voption; cvo: float voption; cir: float voption |})) ->
                (b.cir |> ValueOption.defaultValue 0.0) * 100.0)
            |> Array.sort
        printfn "%s gap%%  p25=%+6.2f  p50=%+6.2f  p75=%+6.2f" name (pct gaps 0.25) (pct gaps 0.50) (pct gaps 0.75)
        printfn "%s CIR%%  p25=%6.2f  p50=%6.2f  p75=%6.2f" name (pct cirs 0.25) (pct cirs 0.50) (pct cirs 0.75)

printCohort "bullishCont" bullishCont
printCohort "bearishCont" bearishCont

// Write outputs. Extract (ticker, date) tuples first to sidestep anon-record inference.
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
    printfn "Wrote %s (%d entries)" path rows.Length

writeJson outBullishCont (toPairs bullishCont)
writeJson outBearishCont (toPairs bearishCont)
writeJson outBullishContHiVol (toPairs bullishContHiVol)
writeJson outBearishContHiVol (toPairs bearishContHiVol)
