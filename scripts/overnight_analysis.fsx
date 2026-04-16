open System
open System.IO
open System.Text.Json

let input = "data/continuation_plays_augmented.json"

type Row = {| ticker: string; date: string; daysSince: int; rvol: float
              gap: float voption; cir: float voption
              novc: float voption; ncvc: float voption |}

let raw : Row[] =
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
            cir = getD "close_in_range_pct"
            novc = getD "next_open_vs_close_pct"
            ncvc = getD "next_close_vs_close_pct"
        |} |]

let pct q (arr: float[]) =
    arr.[int (float arr.Length * q) |> min (arr.Length - 1) |> max 0]

let summarize label (rows: float[]) =
    if rows.Length = 0 then printfn "%-52s  (empty)" label
    else
        let sorted = Array.sort rows
        let mean = rows |> Array.average
        let wins = rows |> Array.filter (fun v -> v > 0.0) |> Array.length
        let losses = rows |> Array.filter (fun v -> v < 0.0) |> Array.length
        let hitRate = 100.0 * float wins / float (wins + losses)
        printfn "%-52s n=%4d  mean=%+7.3f%%  p25=%+6.2f%%  p50=%+6.2f%%  p75=%+6.2f%%  hit=%.1f%%"
            label rows.Length (mean * 100.0) (pct 0.25 sorted * 100.0) (pct 0.50 sorted * 100.0) (pct 0.75 sorted * 100.0) hitRate

let extractNovc (rows: Row[]) =
    rows |> Array.choose (fun (r: Row) -> match r.novc with ValueSome v -> Some v | _ -> None)
let extractNcvc (rows: Row[]) =
    rows |> Array.choose (fun (r: Row) -> match r.ncvc with ValueSome v -> Some v | _ -> None)

let rvolMin = 3.0

// 5 disjoint CIR bins: [0.0, 0.2), [0.2, 0.4), [0.4, 0.6), [0.6, 0.8), [0.8, 1.01).
// Use 1.01 as the upper bound on the top bin to include CIR = 1.0 (closed exactly at high).
let bins = [|
    0.0,   0.2
    0.2,   0.4
    0.4,   0.6
    0.6,   0.8
    0.8,   1.01
|]

printfn "=========================================================================="
printfn "Overnight + next-day returns by DISJOINT CIR bin   (RVOL>=%.1f, day 0)" rvolMin
printfn "=========================================================================="
printfn ""
printfn "Returns are as-is (positive = stock went up)."
printfn "  LONG holder wants positive; SHORT holder wants negative."
printfn ""

for (lo, hi) in bins do
    let co =
        raw
        |> Array.filter (fun r ->
            r.daysSince = 0
            && r.rvol >= rvolMin
            && (match r.cir with
                | ValueSome c -> c >= lo && c < hi
                | _ -> false))
    let novc = extractNovc co
    let ncvc = extractNcvc co
    let hiDisp = if hi > 1.0 then 1.0 else hi
    printfn "--- CIR in [%.2f, %.2f)  (n=%d) ---" lo hiDisp co.Length
    summarize "  overnight (close -> next open)" novc
    summarize "  next day  (close -> next close)" ncvc
    printfn ""

// Also: decompose next-day total into overnight-gap component and intraday component (open -> close).
// Just reuse same data, but show the intraday piece as (next_close / next_open - 1) approximated
// from ncvc and novc: (1+ncvc)/(1+novc) - 1.
printfn "=========================================================================="
printfn "Decomposition: next-day = overnight-gap + next-day-intraday"
printfn "=========================================================================="
printfn ""
for (lo, hi) in bins do
    let co =
        raw
        |> Array.filter (fun r ->
            r.daysSince = 0
            && r.rvol >= rvolMin
            && (match r.cir with
                | ValueSome c -> c >= lo && c < hi
                | _ -> false))
    // Only keep rows with both novc and ncvc.
    let pairs =
        co
        |> Array.choose (fun r ->
            match r.novc, r.ncvc with
            | ValueSome o, ValueSome c -> Some (o, c)
            | _ -> None)
    if pairs.Length = 0 then ()
    else
        let overnight = pairs |> Array.map fst
        let intraday = pairs |> Array.map (fun (o, c) -> (1.0 + c) / (1.0 + o) - 1.0)
        let hiDisp = if hi > 1.0 then 1.0 else hi
        printfn "--- CIR in [%.2f, %.2f)  (n=%d) ---" lo hiDisp pairs.Length
        summarize "  overnight-only    (close -> next open)" overnight
        summarize "  next-day-intraday (next open -> next close)" intraday
        printfn ""
