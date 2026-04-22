#r "nuget: FSharp.SystemTextJson, 1.4.36"
#r "nuget: Argu, 6.2.5"

// Batch runner for the mean-maximizing (Tv, Ta) threshold sweep.
//
// Sister script to run_minizinc_sweep.fsx. Same CSV schema (Tv, Ta, etc.),
// same resume/parallel logic. Key differences:
//   - Model: minizinc/stock_in_play_mean.mzn (maximize sum_rvol subject to
//     n_fired <= n_ceil, where n_ceil = n * n_ceil_bp / 10000).
//   - The --precisions flag is reinterpreted as --n-ceil-bps: a comma-
//     separated list of ceiling sizes in basis points (1 bp = 0.01%).
//     For example: --n-ceil-bps 10 means "ceiling = 0.1% of the bucket's
//     row count".
//   - The CSV's `precision_pct` column carries the n_ceil_bp value (schema
//     unchanged so backtest_gapup_thresholds.fsx can read it as-is).
//
// Resume-aware: skips (bucket, n_ceil_bp) pairs already present in the
// output file.

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Argu

type CliArgs =
    | [<AltCommandLine("-s")>] Start_Bucket of int
    | [<AltCommandLine("-e")>] End_Bucket of int
    | [<AltCommandLine("-k")>] Step of int
    | [<AltCommandLine("-p")>] N_Ceil_Bps of string
    | [<AltCommandLine("-o")>] Output_File of string
    | Checkpoints_Dir of string
    | Model of string
    | Target_Rvol of float
    | Int_Scale of int
    | Solver of string
    | Solve_Timeout_S of int
    | [<AltCommandLine("-j")>] Jobs of int
    | Cp_Sat_Parallel of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Bucket _ -> "First bucket index (inclusive). Default: 366 (09:31 ET)"
            | End_Bucket _ -> "Last bucket index (inclusive). Default: 2688 (15:58:00 ET)"
            | Step _ -> "Bucket step. Default: 1 (every 10s bucket)"
            | N_Ceil_Bps _ -> "Comma-separated ceiling sizes in basis points (1 bp = 0.01% of n). Default: 10"
            | Output_File _ -> "Results CSV (resume-aware). Default: minizinc/thresholds_10s_mean.csv"
            | Checkpoints_Dir _ -> "Directory with checkpoint_b{N}.dzn inputs. Default: data/minizinc/10s"
            | Model _ -> "MiniZinc model path. Default: minizinc/stock_in_play_mean.mzn"
            | Target_Rvol _ -> "Target session RVOL. Affects scaling clamp only here (no 'hit' concept). Default: 4.0"
            | Int_Scale _ -> "Model int_scale. Default: 256"
            | Solver _ -> "MiniZinc solver id. Default: cp-sat"
            | Solve_Timeout_S _ -> "Per-problem solve timeout (seconds). Default: 600"
            | Jobs _ -> "Parallel MiniZinc processes. Default: 6"
            | Cp_Sat_Parallel _ -> "CP-SAT worker threads per process (-p). Default: 2"

let parser = ArgumentParser.Create<CliArgs>(programName = "run_minizinc_sweep_mean.fsx")
let cliArgs = fsi.CommandLineArgs |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

// ----- Config -----
let checkpointsDir = parsed.GetResult(Checkpoints_Dir, defaultValue = "data/minizinc/10s")
let csvPath = parsed.GetResult(Output_File, defaultValue = "minizinc/thresholds_10s_mean.csv")
let modelPath = parsed.GetResult(Model, defaultValue = "minizinc/stock_in_play_mean.mzn")
let targetRvol = parsed.GetResult(Target_Rvol, defaultValue = 4.0)
let intScale = parsed.GetResult(Int_Scale, defaultValue = 256)
let solver = parsed.GetResult(Solver, defaultValue = "cp-sat")
let solveTimeoutMs = parsed.GetResult(Solve_Timeout_S, defaultValue = 600) * 1000
let jobs = parsed.GetResult(Jobs, defaultValue = 6)
let cpSatParallel = parsed.GetResult(Cp_Sat_Parallel, defaultValue = 2)

let startB = parsed.GetResult(Start_Bucket, defaultValue = 366)
let endB = parsed.GetResult(End_Bucket, defaultValue = 2688)
let step = parsed.GetResult(Step, defaultValue = 1)
let nCeilBps =
    match parsed.TryGetResult N_Ceil_Bps with
    | Some s -> s.Split ',' |> Array.map int
    | None -> [| 10 |]

let buckets = [| for b in startB .. step .. endB -> b |]

let bucketToEt (bucket: int) =
    let totalSeconds = 30 * 60 + bucket * 10
    let hh = 8 + totalSeconds / 3600
    let mm = (totalSeconds % 3600) / 60
    sprintf "%02d:%02d" hh mm

// ----- CSV row + resume (same schema as run_minizinc_sweep.fsx) -----

type Row = {
    bucket: int
    et: string
    precision_pct: int          // carries n_ceil_bp for this model
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

let csvHeader = "bucket,et,precision_pct,Tv,Ta,n_fired,n_hit,precision_actual,solve_time_s,n_rows,status"

let parseCsvRow (line: string) : Row option =
    let parts = line.Split ','
    if parts.Length <> 11 then None
    else
        try
            Some {
                bucket = Int32.Parse(parts.[0], inv)
                et = parts.[1]
                precision_pct = Int32.Parse(parts.[2], inv)
                Tv = Double.Parse(parts.[3], inv)
                Ta = Double.Parse(parts.[4], inv)
                n_fired = Int32.Parse(parts.[5], inv)
                n_hit = Int32.Parse(parts.[6], inv)
                precision_actual = Double.Parse(parts.[7], inv)
                solve_time_s = Double.Parse(parts.[8], inv)
                n_rows = Int32.Parse(parts.[9], inv)
                status = parts.[10]
            }
        with _ -> None

let existing =
    if File.Exists csvPath then
        File.ReadAllLines csvPath
        |> Array.skip 1
        |> Array.choose parseCsvRow
    else [||]

let results = ResizeArray<Row>(existing)
let seen =
    existing
    |> Array.map (fun r -> r.bucket, r.precision_pct)
    |> Set.ofArray

let csvDir = Path.GetDirectoryName csvPath
if not (String.IsNullOrEmpty csvDir) then Directory.CreateDirectory csvDir |> ignore
let csvNew = not (File.Exists csvPath)
let csvStream = new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read)
let csvWriter = new StreamWriter(csvStream)
csvWriter.AutoFlush <- true
if csvNew then csvWriter.WriteLine csvHeader

let appendCsvRow (r: Row) =
    csvWriter.WriteLine(
        String.Format(inv,
            "{0},{1},{2},{3:F6},{4:F6},{5},{6},{7:F2},{8:F2},{9},{10}",
            r.bucket, r.et, r.precision_pct, r.Tv, r.Ta,
            r.n_fired, r.n_hit, r.precision_actual, r.solve_time_s,
            r.n_rows, r.status))

printfn "Mean-sweep %d buckets [%d..%d step %d] × n_ceil_bps %A"
    buckets.Length startB endB step nCeilBps
printfn "Resume: %d existing rows loaded from %s" existing.Length csvPath

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

let dznExists (bucket: int) : bool =
    let dznPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket)
    File.Exists dznPath

let parseSolverJson (text: string) =
    let trimmed = text.Replace("==========", "").Replace("----------", "").Trim()
    let last = trimmed.LastIndexOf '}'
    if last < 0 then None
    else
    let first = trimmed.LastIndexOf('{', last)
    if first < 0 || last <= first then None
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

// ----- Main loop -----

let tasks =
    [| for bucket in buckets do
         if dznExists bucket then
             for bp in nCeilBps do
                 if not (seen.Contains (bucket, bp)) then
                     yield (bucket, bp) |]

let nMissingBuckets =
    buckets |> Array.filter (fun b -> not (dznExists b)) |> Array.length
let nSkipped = (buckets.Length * nCeilBps.Length) - tasks.Length - nMissingBuckets * nCeilBps.Length

printfn "Parallel sweep: %d tasks, jobs=%d, cp-sat-parallel=%d (tasks skipped=%d missing-dzn-buckets=%d)"
    tasks.Length jobs cpSatParallel nSkipped nMissingBuckets

let overallSw = Stopwatch.StartNew()
let resultsLock = obj()
let logLock = obj()
let mutable nDone = 0
let mutable nFailed = 0

let runOne (bucket: int, bp: int) =
  try
    let dznPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket)
    let args =
        sprintf "--solver %s -p %d %s %s -D \"n_ceil_bp=%d\" -D \"int_scale=%d\" -D \"target_rvol=%s\""
            solver cpSatParallel modelPath dznPath bp intScale
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
            precision_pct = bp
            Tv = tv; Ta = ta
            n_fired = nf; n_hit = nh
            precision_actual = precAct
            solve_time_s = elapsed
            n_rows = nr
            status = status
        }
        let doneNow =
            lock resultsLock (fun () ->
                results.Add row
                appendCsvRow row
                nDone <- nDone + 1
                nDone)
        lock logLock (fun () ->
            let totalElapsed = overallSw.Elapsed.TotalSeconds
            let avgWall = totalElapsed / float (max doneNow 1)
            let remain = tasks.Length - doneNow
            let etaMin = avgWall * float remain / 60.0
            printfn "  [%d/%d] b%d bp=%d fired=%d hit=%d hit%%=%.1f Tv=%.3f Ta=%.3f %.1fs %s | elapsed=%.1fm eta=%.1fm"
                doneNow tasks.Length bucket bp nf nh precAct tv ta elapsed status
                (totalElapsed / 60.0) etaMin)
    | _ ->
        lock resultsLock (fun () ->
            nFailed <- nFailed + 1
            nDone <- nDone + 1)
        lock logLock (fun () ->
            eprintfn "  [b%d bp=%d] FAILED (code %d) after %.1fs" bucket bp code elapsed
            if nFailed <= 3 then
                if stderr.Length > 0 then eprintfn "    stderr: %s" (stderr.Substring(0, min 300 stderr.Length))
                if stdout.Length > 0 then eprintfn "    stdout tail: %s" (stdout.Substring(max 0 (stdout.Length - 300))))
  with ex ->
    lock resultsLock (fun () ->
        nFailed <- nFailed + 1
        nDone <- nDone + 1)
    lock logLock (fun () ->
        eprintfn "  [b%d bp=%d] EXCEPTION: %s" bucket bp ex.Message)

let parallelOpts = ParallelOptions(MaxDegreeOfParallelism = jobs)
Parallel.ForEach(tasks, parallelOpts, System.Action<int*int>(runOne)) |> ignore

csvWriter.Dispose()
csvStream.Dispose()

overallSw.Stop()
printfn ""
printfn "Done. ran=%d skipped=%d missing-buckets=%d failed=%d in %.1fs"
    (nDone - nFailed) nSkipped nMissingBuckets nFailed overallSw.Elapsed.TotalSeconds
printfn "Results: %s (%d total rows)" csvPath results.Count
