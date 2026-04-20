#r "nuget: FSharp.SystemTextJson, 1.4.36"
#r "nuget: Argu, 6.2.5"

// Reads data/minizinc/checkpoint_b{N}.json (array-of-records) and writes
// data/minizinc/checkpoint_b{N}.dzn with float vr, tr, and
// session_volume_rvol_final arrays ready for stock_in_play_threshold_int.mzn.
// The model handles the scaling to integer RVOL units internally.
//
// Default: emits all rows for the given bucket from data/minizinc/. Pass
// --n-sample N to generate a subsampled test set — sampling is uniform
// (every k-th row) for determinism. Pass --dir PATH to read from a different
// input directory (e.g. the 10s sweep output).

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Globalization
open Argu

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

type CliArgs =
    | [<AltCommandLine("-b")>] Bucket of int
    | [<AltCommandLine("-d")>] Dir of string
    | [<AltCommandLine("-n")>] N_Sample of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Bucket _ -> "Bucket index to convert. Default: 61"
            | Dir _ -> "Input/output directory. Default: data/minizinc"
            | N_Sample _ -> "Optional uniform subsample size for testing. Omit for full dataset"

let parser = ArgumentParser.Create<CliArgs>(programName = "convert_checkpoint_to_dzn.fsx")
let cliArgs =
    Environment.GetCommandLineArgs()
    |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx"))
    |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let dirArg = parsed.GetResult(Dir, defaultValue = "data/minizinc")
let bucket = parsed.GetResult(Bucket, defaultValue = 61)
let nTakeOpt = parsed.TryGetResult N_Sample

let inPath = Path.Combine(dirArg, sprintf "checkpoint_b%d.json" bucket)
let outPath =
    match nTakeOpt with
    | Some n -> Path.Combine(dirArg, sprintf "checkpoint_b%d_n%d.dzn" bucket n)
    | None   -> Path.Combine(dirArg, sprintf "checkpoint_b%d.dzn" bucket)

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

let inv = CultureInfo.InvariantCulture
let sb = StringBuilder()
sb.Append (sprintf "n = %d;\n" sample.Length) |> ignore

let appendFloatArray (name: string) (proj: Row -> double) =
    sb.Append (name + " = [") |> ignore
    for i = 0 to sample.Length - 1 do
        sb.AppendFormat(inv, "{0:F6}", proj sample.[i]) |> ignore
        if i < sample.Length - 1 then sb.Append "," |> ignore
    sb.Append "];\n" |> ignore

// Split-safe raw-basis volume RVOL: cum_volume / (avg_session_adj_volume_4w / split_factor_today).
appendFloatArray "vr" (fun r ->
    if r.AvgSessionAdjVolume4w > 0.0 && r.SplitFactorToday > 0.0 then
        double r.CumVolume / (r.AvgSessionAdjVolume4w / r.SplitFactorToday)
    else 0.0)

// Transaction RVOL: counts are split-invariant, so direct ratio.
appendFloatArray "tr" (fun r ->
    if r.AvgSessionTransactions4w > 0.0 then
        double r.CumTransactions / r.AvgSessionTransactions4w
    else 0.0)

// End-of-day session volume RVOL (ground truth). The model derives `hit` from
// this via `session_volume_rvol_final >= target_rvol`.
appendFloatArray "session_volume_rvol_final" (fun r -> r.SessionVolumeRvolFinal)

File.WriteAllText(outPath, sb.ToString())
printfn "Wrote %s (%.2f MB)" outPath (float (FileInfo(outPath).Length) / 1024.0 / 1024.0)
