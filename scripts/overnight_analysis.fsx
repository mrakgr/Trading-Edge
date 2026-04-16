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

let rvolMin = 3.0

let bins = [|
    0.0,   0.2
    0.2,   0.4
    0.4,   0.6
    0.6,   0.8
    0.8,   1.01
|]

let cohort (lo, hi) (gapPred: float -> bool) =
    raw
    |> Array.filter (fun r ->
        r.daysSince = 0
        && r.rvol >= rvolMin
        && (match r.cir with ValueSome c -> c >= lo && c < hi | _ -> false)
        && (match r.gap with ValueSome g -> gapPred g | _ -> false))

let stats (vals: float[]) =
    if vals.Length = 0 then nan, 0.0, 0
    else
        let mean = vals |> Array.average
        let wins = vals |> Array.filter (fun v -> v > 0.0) |> Array.length
        let losses = vals |> Array.filter (fun v -> v < 0.0) |> Array.length
        let hit =
            if wins + losses = 0 then 0.0
            else 100.0 * float wins / float (wins + losses)
        mean * 100.0, hit, vals.Length

let extractNovc (rows: Row[]) =
    rows |> Array.choose (fun (r: Row) -> match r.novc with ValueSome v -> Some v | _ -> None)
let extractNcvc (rows: Row[]) =
    rows |> Array.choose (fun (r: Row) -> match r.ncvc with ValueSome v -> Some v | _ -> None)
let extractIntraNextDay (rows: Row[]) =
    rows
    |> Array.choose (fun r ->
        match r.novc, r.ncvc with
        | ValueSome o, ValueSome c -> Some ((1.0 + c) / (1.0 + o) - 1.0)
        | _ -> None)

let printTable label (gapPred: float -> bool) =
    printfn "============================================================"
    printfn "%s    (RVOL >= %.1f, day 0)" label rvolMin
    printfn "============================================================"
    printfn "%-13s %5s  %-22s  %-22s  %-22s"
        "CIR bin" "n" "close->next open" "next open->next close" "close->next close"
    printfn "%-13s %5s  %-22s  %-22s  %-22s"
        "" "" "(overnight)" "(next-day intraday)" "(full next day)"
    for (lo, hi) in bins do
        let co = cohort (lo, hi) gapPred
        let onMean, onHit, onN = stats (extractNovc co)
        let idMean, idHit, idN = stats (extractIntraNextDay co)
        let ncMean, ncHit, ncN = stats (extractNcvc co)
        let hiDisp = if hi > 1.0 then 1.0 else hi
        let fmt (m, h, n) =
            if n = 0 then sprintf "%-22s" "(empty)"
            else sprintf "%+6.2f%%  %5.1f%% hit     " m h
        printfn "[%.2f, %.2f)   %5d  %-22s  %-22s  %-22s"
            lo hiDisp co.Length (fmt (onMean, onHit, onN)) (fmt (idMean, idHit, idN)) (fmt (ncMean, ncHit, ncN))
    printfn ""

printTable "GAP UP  (gap_pct > 0)" (fun g -> g > 0.0)
printTable "GAP DOWN  (gap_pct < 0)" (fun g -> g < 0.0)
printTable "COMBINED (any gap)" (fun _ -> true)
