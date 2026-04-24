#r "../TradingEdge.Orb/bin/Debug/net10.0/TradingEdge.Orb.dll"

open System
open System.IO
open System.Text
open System.Text.Json
open TradingEdge.Orb.TradeBinary

let input = "data/continuation_plays_augmented.json"
let binDir = "data/trades_bin"

// Low-RVOL band: day-0 rows with RVOL below 3.0. These are the ORB system's
// would-be "false alarms" — days that look like a continuation candidate only
// in hindsight once RVOL drops out. A good predicted-RVOL gate should reject
// most entries here.
let minRvol = 0.0
let maxRvol = 3.0
let suffix = "rvol_under3"

let output = $"data/breakouts_{suffix}.json"
let outputGapUp = $"data/breakouts_{suffix}_gapup.json"
let outputGapDown = $"data/breakouts_{suffix}_gapdown.json"

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
            daysSince = el.GetProperty("days_since_max_rvol_day").GetInt32()
            rvol = el.GetProperty("rvol").GetDouble()
            gap = getD "gap_pct"
        |} |]

printfn "Total entries:        %d" raw.Length

// Unlike the high-RVOL band script, we don't filter on daysSince=0 — the
// continuation_plays dataset only contains day-0 rows with RVOL >= 3 (that's
// the breakout definition). Low-RVOL rows live in the *continuation tail*
// (daysSince > 0, RVOL faded). Keep all such rows.
let band =
    raw
    |> Array.filter (fun r -> r.rvol >= minRvol && r.rvol < maxRvol)
printfn "RVOL in [%.1f, %.1f): %d" minRvol maxRvol band.Length

let tradable =
    band
    |> Array.filter (fun r ->
        let path = Path.Combine(binDir, r.ticker, $"{r.date}.bin")
        File.Exists path
        &&
        let h = readHeader { Directory = binDir; Ticker = r.ticker; Date = r.date }
        h.OpeningPrintIndex.IsSome)
printfn "After bin+opening filter: %d" tradable.Length

let gapUp = tradable |> Array.filter (fun r -> match r.gap with ValueSome g -> g > 0.0 | _ -> false)
let gapDown = tradable |> Array.filter (fun r -> match r.gap with ValueSome g -> g < 0.0 | _ -> false)
printfn "Gap up: %d  Gap down: %d" gapUp.Length gapDown.Length

let writeJson (path: string) (rows: {| ticker: string; date: string; daysSince: int; rvol: float; gap: float voption |}[]) =
    let sb = StringBuilder()
    sb.Append "[\n" |> ignore
    rows
    |> Array.iteri (fun i e ->
        let comma = if i = rows.Length - 1 then "" else ","
        sb.AppendFormat("    {{\"ticker\": \"{0}\", \"date\": \"{1}\"}}{2}\n", e.ticker, e.date, comma) |> ignore)
    sb.Append "]\n" |> ignore
    File.WriteAllText(path, sb.ToString())
    printfn "Wrote %s (%d)" path rows.Length

writeJson output tradable
writeJson outputGapUp gapUp
writeJson outputGapDown gapDown
