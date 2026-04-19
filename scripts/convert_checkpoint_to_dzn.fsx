#r "nuget: FSharp.SystemTextJson, 1.4.36"

// Reads data/minizinc/checkpoint_b{N}.json (array-of-records) and writes
// data/minizinc/checkpoint_b{N}.dzn with integer-scaled vr_milli, tr_milli,
// and hit arrays ready for stock_in_play_threshold_int.mzn (CP-SAT).
//
// Usage:
//   dotnet fsi scripts/convert_checkpoint_to_dzn.fsx [bucket] [n_sample]
//
// Default: emits all 877k rows for the given bucket. Pass a smaller n_sample
// (e.g. 10000) to generate a subsampled test set — sampling is uniform
// (every k-th row) for determinism.
//
// RVOL values are scaled to "milli-RVOLs" (1000 * rvol). Thresholds in the
// model are quantized to 0.001 precision accordingly.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

type Row = {
    [<JsonPropertyName "ticker">] Ticker: string
    [<JsonPropertyName "date">] Date: string
    [<JsonPropertyName "cum_volume">] CumVolume: int64
    [<JsonPropertyName "cum_transactions">] CumTransactions: int64
    [<JsonPropertyName "avg_session_adj_volume_4w">] AvgSessionAdjVolume4w: double
    [<JsonPropertyName "avg_session_transactions_4w">] AvgSessionTransactions4w: double
    [<JsonPropertyName "session_volume_rvol_final">] SessionVolumeRvolFinal: double
    [<JsonPropertyName "split_factor_today">] SplitFactorToday: double
}

let args = System.Environment.GetCommandLineArgs()
let scriptArgs = args |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
let bucket =
    match scriptArgs with
    | [||] -> 61
    | a -> int a.[0]
let nTakeOpt =
    match scriptArgs with
    | [| _; n |] -> Some (int n)
    | _ -> None

let rvolThreshold = 3.0
let inPath = sprintf "data/minizinc/checkpoint_b%d.json" bucket
let outPath =
    match nTakeOpt with
    | Some n -> sprintf "data/minizinc/checkpoint_b%d_n%d.dzn" bucket n
    | None   -> sprintf "data/minizinc/checkpoint_b%d.dzn" bucket

printfn "Reading %s..." inPath
let fs = File.OpenRead(inPath)
let rows = JsonSerializer.Deserialize<Row array>(fs, JsonSerializerOptions())
fs.Dispose()
printfn "  total rows: %d" rows.Length

let sample =
    match nTakeOpt with
    | None -> rows
    | Some n ->
        let k = rows.Length / n
        rows
        |> Array.mapi (fun i r -> i, r)
        |> Array.filter (fun (i, _) -> i % k = 0)
        |> Array.truncate n
        |> Array.map snd
printfn "  selecting %d rows" sample.Length

// Scale rvol to milli units. Cap at 1e9 to stay inside int32 range; real
// rvol values rarely exceed 1000 so this never clamps in practice.
let scaleMilli (x: double) =
    let v = x * 1000.0
    if v < 0.0 then 0
    elif v > 1e9 then 1_000_000_000
    else int v

let sb = StringBuilder()
sb.Append (sprintf "n = %d;\n" sample.Length) |> ignore

sb.Append "vr_milli = [" |> ignore
for i = 0 to sample.Length - 1 do
    let r = sample.[i]
    let vr =
        if r.AvgSessionAdjVolume4w > 0.0 && r.SplitFactorToday > 0.0 then
            double r.CumVolume / (r.AvgSessionAdjVolume4w / r.SplitFactorToday)
        else 0.0
    sb.Append (string (scaleMilli vr)) |> ignore
    if i < sample.Length - 1 then sb.Append "," |> ignore
sb.Append "];\n" |> ignore

sb.Append "tr_milli = [" |> ignore
for i = 0 to sample.Length - 1 do
    let r = sample.[i]
    let tr =
        if r.AvgSessionTransactions4w > 0.0 then
            double r.CumTransactions / r.AvgSessionTransactions4w
        else 0.0
    sb.Append (string (scaleMilli tr)) |> ignore
    if i < sample.Length - 1 then sb.Append "," |> ignore
sb.Append "];\n" |> ignore

sb.Append "hit = [" |> ignore
for i = 0 to sample.Length - 1 do
    sb.Append (if sample.[i].SessionVolumeRvolFinal >= rvolThreshold then "true" else "false") |> ignore
    if i < sample.Length - 1 then sb.Append "," |> ignore
sb.Append "];\n" |> ignore

File.WriteAllText(outPath, sb.ToString())
printfn "Wrote %s (%.2f MB)" outPath (float (FileInfo(outPath).Length) / 1024.0 / 1024.0)
