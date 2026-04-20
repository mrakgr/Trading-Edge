#r "nuget: FSharp.SystemTextJson, 1.4.36"
#r "nuget: Argu, 6.2.5"

// Batch runner for the 10s-bar (Tv, Ta) threshold sweep.
//
// For each (bucket, precision) in the requested ranges:
//   1. Ensure data/minizinc/10s/checkpoint_b{N}.json exists (produced by
//      scripts/export_checkpoint_cumulatives_10s.fsx). If not, skip with a note.
//   2. Convert to .dzn via scripts/convert_checkpoint_to_dzn.fsx (idempotent).
//   3. Invoke `minizinc --solver cp-sat minizinc/stock_in_play_threshold_int.mzn`
//      with var_scale=threshold_scale=1024, target_rvol=3.0, precision_pct=P.
//   4. Parse the JSON output, append to minizinc/thresholds_10s.json.
//
// Resume-aware: skips (bucket, P) pairs already present in the output file.

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Globalization
open Argu

type CliArgs =
    | [<AltCommandLine("-s")>] Start_Bucket of int
    | [<AltCommandLine("-e")>] End_Bucket of int
    | [<AltCommandLine("-k")>] Step of int
    | [<AltCommandLine("-p")>] Precisions of string
    | [<AltCommandLine("-o")>] Output_File of string
    | Checkpoints_Dir of string
    | Model of string
    | Target_Rvol of float
    | Var_Scale of int
    | Threshold_Scale of int
    | Solver of string
    | Solve_Timeout_S of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Bucket _ -> "First bucket index (inclusive). Default: 366 (09:31 ET)"
            | End_Bucket _ -> "Last bucket index (inclusive). Default: 2748 (15:58 ET)"
            | Step _ -> "Bucket step. Default: 6 (one minute of 10s buckets)"
            | Precisions _ -> "Comma-separated precision percentages. Default: 70,80,90"
            | Output_File _ -> "Results file (resume-aware). Default: minizinc/thresholds_10s.json"
            | Checkpoints_Dir _ -> "Directory with checkpoint_b{N}.json inputs. Default: data/minizinc/10s"
            | Model _ -> "MiniZinc model path. Default: minizinc/stock_in_play_threshold_int.mzn"
            | Target_Rvol _ -> "Target end-of-day session RVOL for hit labeling. Default: 3.0"
            | Var_Scale _ -> "Model var_scale. Default: 1024"
            | Threshold_Scale _ -> "Model threshold_scale. Default: 1024"
            | Solver _ -> "MiniZinc solver id. Default: cp-sat"
            | Solve_Timeout_S _ -> "Per-problem solve timeout (seconds). Default: 600"

let parser = ArgumentParser.Create<CliArgs>(programName = "run_minizinc_sweep.fsx")
let cliArgs = fsi.CommandLineArgs |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

// ----- Config -----
let checkpointsDir = parsed.GetResult(Checkpoints_Dir, defaultValue = "data/minizinc/10s")
let outPath = parsed.GetResult(Output_File, defaultValue = "minizinc/thresholds_10s.json")
let modelPath = parsed.GetResult(Model, defaultValue = "minizinc/stock_in_play_threshold_int.mzn")
let targetRvol = parsed.GetResult(Target_Rvol, defaultValue = 3.0)
let varScale = parsed.GetResult(Var_Scale, defaultValue = 1024)
let thresholdScale = parsed.GetResult(Threshold_Scale, defaultValue = 1024)
let solver = parsed.GetResult(Solver, defaultValue = "cp-sat")
let solveTimeoutMs = parsed.GetResult(Solve_Timeout_S, defaultValue = 600) * 1000

let startB = parsed.GetResult(Start_Bucket, defaultValue = 366)
let endB = parsed.GetResult(End_Bucket, defaultValue = 2748)
let step = parsed.GetResult(Step, defaultValue = 6)
let precisions =
    match parsed.TryGetResult Precisions with
    | Some s -> s.Split ',' |> Array.map int
    | None -> [| 70; 80; 90 |]

let buckets = [| for b in startB .. step .. endB -> b |]

let bucketToEt (bucket: int) =
    let totalSeconds = 30 * 60 + bucket * 10
    let hh = 8 + totalSeconds / 3600
    let mm = (totalSeconds % 3600) / 60
    sprintf "%02d:%02d" hh mm

// ----- Load existing results for resume -----

type Row = {
    bucket: int
    et: string
    precision_pct: int
    Tv: double
    Ta: double
    n_fired: int
    n_hit: int
    precision_actual: double
    solve_time_s: double
    n_rows: int
    status: string
}

let inv = CultureInfo.InvariantCulture

let existing =
    if File.Exists outPath then
        try
            use fs = File.OpenRead outPath
            let doc = JsonDocument.Parse(fs)
            [| for el in doc.RootElement.EnumerateArray() ->
                let getInt (name: string) = el.GetProperty(name).GetInt32()
                let getDbl (name: string) = el.GetProperty(name).GetDouble()
                let getStr (name: string) = el.GetProperty(name).GetString()
                { bucket = getInt "bucket"
                  et = getStr "et"
                  precision_pct = getInt "precision_pct"
                  Tv = getDbl "Tv"
                  Ta = getDbl "Ta"
                  n_fired = getInt "n_fired"
                  n_hit = getInt "n_hit"
                  precision_actual = getDbl "precision_actual"
                  solve_time_s = getDbl "solve_time_s"
                  n_rows = (try getInt "n_rows" with _ -> 0)
                  status = (try getStr "status" with _ -> "OPTIMAL") } |]
        with ex ->
            eprintfn "Could not parse existing %s: %s — starting fresh" outPath ex.Message
            [||]
    else [||]

let results = ResizeArray<Row>(existing)
let seen =
    existing
    |> Array.map (fun r -> r.bucket, r.precision_pct)
    |> Set.ofArray

printfn "Sweep %d buckets [%d..%d step %d] × precisions %A" buckets.Length startB endB step precisions
printfn "Resume: %d existing results loaded" existing.Length

// ----- Helpers -----

let runProcess (exe: string) (args: string) (timeoutMs: int) =
    let psi = ProcessStartInfo(exe, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use p = Process.Start(psi)
    let stdoutTask = p.StandardOutput.ReadToEndAsync()
    let stderrTask = p.StandardError.ReadToEndAsync()
    let finished = p.WaitForExit(timeoutMs)
    if not finished then
        try p.Kill() with _ -> ()
        "", "", -1
    else
        p.WaitForExit()
        stdoutTask.Result, stderrTask.Result, p.ExitCode

let ensureDzn (bucket: int) : bool =
    let jsonPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.json" bucket)
    let dznPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket)
    if not (File.Exists jsonPath) then
        false
    elif File.Exists dznPath &&
         File.GetLastWriteTimeUtc(dznPath) >= File.GetLastWriteTimeUtc(jsonPath) then
        true
    else
        let stdout, stderr, code =
            runProcess "dotnet"
                (sprintf "fsi scripts/convert_checkpoint_to_dzn.fsx -- --bucket %d --dir %s"
                    bucket checkpointsDir)
                120_000
        if code <> 0 then
            eprintfn "  [b%d] dzn conversion failed (exit %d): %s" bucket code stderr
            false
        else true

let parseSolverJson (text: string) =
    // MiniZinc prints the output block between solution separators. The last
    // balanced {...} in stdout (before ---------- / ==========) is our JSON.
    let trimmed = text.Replace("==========", "").Replace("----------", "").Trim()
    let last = trimmed.LastIndexOf '}'
    let first = trimmed.LastIndexOf('{', last)
    if first < 0 || last < 0 || last <= first then None
    else
        let json = trimmed.Substring(first, last - first + 1)
        try
            let doc = JsonDocument.Parse json
            let root = doc.RootElement
            let tv = root.GetProperty("Tv").GetDouble()
            let ta = root.GetProperty("Ta").GetDouble()
            let nf = root.GetProperty("n_fired").GetInt32()
            let nh = root.GetProperty("n_hit").GetInt32()
            let nr = root.GetProperty("n_rows").GetInt32()
            Some (tv, ta, nf, nh, nr)
        with _ -> None

let writeResults () =
    let sb = System.Text.StringBuilder()
    sb.Append "[\n" |> ignore
    let sorted =
        results.ToArray()
        |> Array.sortBy (fun r -> r.bucket, r.precision_pct)
    for i = 0 to sorted.Length - 1 do
        let r = sorted.[i]
        if i > 0 then sb.Append ",\n" |> ignore
        sb.AppendFormat(inv,
            """  {{"bucket": {0}, "et": "{1}", "precision_pct": {2}, "Tv": {3:F6}, "Ta": {4:F6}, "n_fired": {5}, "n_hit": {6}, "precision_actual": {7:F2}, "solve_time_s": {8:F2}, "n_rows": {9}, "status": "{10}"}}""",
            r.bucket, r.et, r.precision_pct, r.Tv, r.Ta, r.n_fired, r.n_hit, r.precision_actual, r.solve_time_s, r.n_rows, r.status)
        |> ignore
    sb.Append "\n]\n" |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
    File.WriteAllText(outPath, sb.ToString())

// ----- Main loop -----

let overallSw = Stopwatch.StartNew()
let mutable nRan = 0
let mutable nSkipped = 0
let mutable nMissing = 0
let mutable nFailed = 0

for bucket in buckets do
    let dznOk = ensureDzn bucket
    if not dznOk then
        nMissing <- nMissing + 1
        if nMissing <= 5 then
            printfn "  [b%d] skip — no checkpoint JSON" bucket
    else
        let dznPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket)
        for P in precisions do
            if seen.Contains (bucket, P) then
                nSkipped <- nSkipped + 1
            else
                let args =
                    sprintf "--solver %s %s %s -D \"precision_pct=%d\" -D \"var_scale=%d\" -D \"threshold_scale=%d\" -D \"target_rvol=%s\""
                        solver modelPath dznPath P varScale thresholdScale
                        (targetRvol.ToString("F1", inv))
                let sw = Stopwatch.StartNew()
                let stdout, stderr, code = runProcess "minizinc" args solveTimeoutMs
                sw.Stop()
                let elapsed = sw.Elapsed.TotalSeconds
                match code, parseSolverJson stdout with
                | 0, Some (tv, ta, nf, nh, nr) when nf > 0 ->
                    let isOptimal = stdout.Contains "=========="
                    let status = if isOptimal then "OPTIMAL" else "FEASIBLE"
                    let precAct = if nf = 0 then 0.0 else 100.0 * double nh / double nf
                    let row = {
                        bucket = bucket
                        et = bucketToEt bucket
                        precision_pct = P
                        Tv = tv; Ta = ta
                        n_fired = nf; n_hit = nh
                        precision_actual = precAct
                        solve_time_s = elapsed
                        n_rows = nr
                        status = status
                    }
                    results.Add row
                    nRan <- nRan + 1
                    printfn "  [b%d P=%d] fired=%d hit=%d prec=%.1f%% Tv=%.3f Ta=%.3f %.1fs %s"
                        bucket P nf nh precAct tv ta elapsed status
                    if nRan % 20 = 0 then writeResults ()
                | _ ->
                    nFailed <- nFailed + 1
                    eprintfn "  [b%d P=%d] FAILED (code %d) after %.1fs" bucket P code elapsed
                    if nFailed <= 3 then
                        if stderr.Length > 0 then eprintfn "    stderr: %s" (stderr.Substring(0, min 300 stderr.Length))
                        if stdout.Length > 0 then eprintfn "    stdout tail: %s" (stdout.Substring(max 0 (stdout.Length - 300)))

writeResults ()

overallSw.Stop()
printfn ""
printfn "Done. ran=%d skipped=%d missing=%d failed=%d in %.1fs" nRan nSkipped nMissing nFailed overallSw.Elapsed.TotalSeconds
printfn "Results: %s (%d total rows)" outPath results.Count
