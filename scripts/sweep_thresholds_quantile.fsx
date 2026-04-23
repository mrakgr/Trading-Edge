#r "nuget: Argu, 6.2.5"

// Quantile-based (Tv, Ta) threshold sweep.
//
// Per bucket, per (X, Y) grid cell:
//   Tv = upper-tail-X quantile of vr  (top X fraction by volume ratio)
//   Ta = upper-tail-Y quantile of tr  (top Y fraction by transaction ratio)
// The fire count and hit count are then computed exactly like the MiniZinc
// model: n_fired = #{i : vr_i >= Tv ∧ tr_i >= Ta}, n_hit = n_fired ∩ {rvol_final >= target_rvol}.
//
// Output CSV uses the same schema as the MiniZinc sweeps
// (bucket,et,precision_pct,Tv,Ta,n_fired,n_hit,precision_actual,solve_time_s,n_rows,status)
// so backtest_gapup_thresholds.fsx can read it unchanged. The `precision_pct`
// column encodes the (X, Y) cell as Xbp*1000 + Ybp where
//   Xbp = round(X * 10000)   (1..100, i.e. 1bp..100bp of vol tail)
//   Ybp = round(Y * 1000)    (1..100, i.e. 0.1%..10% of txn tail)
// Examples: X=0.0001,Y=0.01 -> 1010 ; X=0.01,Y=0.10 -> 100100.

open System
open System.IO
open System.Diagnostics
open System.Globalization
open System.Threading.Tasks
open Argu

type CliArgs =
    | [<AltCommandLine("-s")>] Start_Bucket of int
    | [<AltCommandLine("-e")>] End_Bucket of int
    | [<AltCommandLine("-k")>] Step of int
    | X_Grid of string
    | Y_Grid of string
    | [<AltCommandLine("-o")>] Output_File of string
    | Checkpoints_Dir of string
    | Target_Rvol of float
    | [<AltCommandLine("-j")>] Jobs of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Bucket _ -> "First bucket index (inclusive). Default: 366 (09:31 ET)"
            | End_Bucket _ -> "Last bucket index (inclusive). Default: 2688 (15:58:00 ET)"
            | Step _ -> "Bucket step. Default: 1"
            | X_Grid _ -> "Comma-separated volume-tail fractions. Default: 0.0001,0.0002,0.0005,0.001,0.002,0.005,0.01,0.02,0.05"
            | Y_Grid _ -> "Comma-separated txn-tail fractions. Default: 0.01,0.025,0.05,0.10"
            | Output_File _ -> "Results CSV. Default: minizinc/thresholds_10s_quantile.csv"
            | Checkpoints_Dir _ -> "Directory with checkpoint_b{N}.dzn inputs. Default: data/minizinc/10s"
            | Target_Rvol _ -> "Target session RVOL for n_hit. Default: 4.0"
            | Jobs _ -> "Parallel bucket workers. Default: System.Environment.ProcessorCount"

let parser = ArgumentParser.Create<CliArgs>(programName = "sweep_thresholds_quantile.fsx")
let cliArgs = fsi.CommandLineArgs |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let inv = CultureInfo.InvariantCulture

let parseFloatList (s: string) =
    s.Split ',' |> Array.map (fun x -> Double.Parse(x.Trim(), inv))

let checkpointsDir = parsed.GetResult(Checkpoints_Dir, defaultValue = "data/minizinc/10s")
let csvPath = parsed.GetResult(Output_File, defaultValue = "minizinc/thresholds_10s_quantile.csv")
let targetRvol = parsed.GetResult(Target_Rvol, defaultValue = 4.0)
let jobs = parsed.GetResult(Jobs, defaultValue = Environment.ProcessorCount)

let startB = parsed.GetResult(Start_Bucket, defaultValue = 366)
let endB = parsed.GetResult(End_Bucket, defaultValue = 2688)
let step = parsed.GetResult(Step, defaultValue = 1)

let xGrid =
    parsed.TryGetResult X_Grid
    |> Option.map parseFloatList
    |> Option.defaultValue [| 0.0001; 0.0002; 0.0005; 0.001; 0.002; 0.005; 0.01; 0.02; 0.05 |]
let yGrid =
    parsed.TryGetResult Y_Grid
    |> Option.map parseFloatList
    |> Option.defaultValue [| 0.01; 0.025; 0.05; 0.10 |]

let buckets = [| for b in startB .. step .. endB -> b |]

let bucketToEt (bucket: int) =
    let totalSeconds = 30 * 60 + bucket * 10
    let hh = 8 + totalSeconds / 3600
    let mm = totalSeconds % 3600 / 60
    sprintf "%02d:%02d" hh mm

let cellCode (x: float) (y: float) =
    let xbp = int (Math.Round(x * 10000.0))
    let ybp = int (Math.Round(y * 1000.0))
    xbp * 1000 + ybp

// ----- CSV row + resume -----

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

printfn "Quantile sweep: %d buckets [%d..%d step %d] × %d X-cells × %d Y-cells = %d tasks"
    buckets.Length startB endB step xGrid.Length yGrid.Length
    (buckets.Length * xGrid.Length * yGrid.Length)
printfn "X grid: %A" xGrid
printfn "Y grid: %A" yGrid
printfn "target_rvol=%.2f jobs=%d" targetRvol jobs
printfn "Resume: %d existing rows loaded from %s" existing.Length csvPath

// ----- DZN parser -----
// Format: data = [(v,t,r),(v,t,r),...]; n = N;
// File can be >100MB; parse as a single-pass char scan to avoid allocating
// a giant string tokenizer.

let parseDzn (path: string) : struct (float[] * float[] * float[]) =
    let txt = File.ReadAllText path
    // Find start of array body: first '[' after "data".
    let i0 = txt.IndexOf '['
    if i0 < 0 then failwithf "No '[' in %s" path
    let vr = ResizeArray<float>()
    let tr = ResizeArray<float>()
    let rv = ResizeArray<float>()
    let mutable i = i0 + 1
    let len = txt.Length
    while i < len do
        // Skip whitespace and commas between tuples.
        while i < len && (txt.[i] = ' ' || txt.[i] = ',' || txt.[i] = '\n' || txt.[i] = '\r' || txt.[i] = '\t') do
            i <- i + 1
        if i >= len || txt.[i] = ']' then i <- len
        else
            // Expect '('.
            if txt.[i] <> '(' then i <- len
            else
                i <- i + 1
                // Read three comma-separated floats up to ')'.
                let mutable idx = 0
                let vals = [| 0.0; 0.0; 0.0 |]
                while idx < 3 && i < len do
                    let start = i
                    while i < len && txt.[i] <> ',' && txt.[i] <> ')' do
                        i <- i + 1
                    let span = txt.AsSpan(start, i - start)
                    vals.[idx] <- Double.Parse(span, NumberStyles.Float, inv)
                    idx <- idx + 1
                    if i < len && txt.[i] = ',' then i <- i + 1
                // Consume ')'.
                if i < len && txt.[i] = ')' then i <- i + 1
                vr.Add vals.[0]
                tr.Add vals.[1]
                rv.Add vals.[2]
    struct (vr.ToArray(), tr.ToArray(), rv.ToArray())

// ----- Quantile -----
// Tail-X quantile: the threshold T such that (# values >= T) is approximately
// X fraction of n. We take T = sorted_desc[k] where k = floor(X * n), i.e. the
// k-th largest value (0-indexed). When k == 0 and X*n < 1 we clamp to k=0 so
// we still pick the max.

let tailQuantile (sortedDesc: float[]) (x: float) : float =
    let n = sortedDesc.Length
    if n = 0 then Double.NaN
    else
        let k = int (Math.Floor(x * float n))
        let k = max 0 (min (n - 1) k)
        sortedDesc.[k]

// ----- Process a single bucket -----

let processBucket (bucket: int) : Row[] =
    let dznPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket)
    if not (File.Exists dznPath) then [||]
    else
        let sw = Stopwatch.StartNew()
        let struct (vr, tr, rv) = parseDzn dznPath
        let n = vr.Length
        let vrSorted = Array.copy vr
        let trSorted = Array.copy tr
        Array.Sort(vrSorted, fun a b -> compare b a)  // descending
        Array.Sort(trSorted, fun a b -> compare b a)
        let rows = ResizeArray<Row>()
        for x in xGrid do
            for y in yGrid do
                let code = cellCode x y
                if not (seen.Contains (bucket, code)) then
                    let tv = tailQuantile vrSorted x
                    let ta = tailQuantile trSorted y
                    // Count fires and hits in a single pass.
                    let mutable nFired = 0
                    let mutable nHit = 0
                    for i in 0 .. n - 1 do
                        if vr.[i] >= tv && tr.[i] >= ta then
                            nFired <- nFired + 1
                            if rv.[i] >= targetRvol then nHit <- nHit + 1
                    let precAct = if nFired > 0 then 100.0 * float nHit / float nFired else 0.0
                    rows.Add {
                        bucket = bucket
                        et = bucketToEt bucket
                        precision_pct = code
                        Tv = tv
                        Ta = ta
                        n_fired = nFired
                        n_hit = nHit
                        precision_actual = precAct
                        solve_time_s = sw.Elapsed.TotalSeconds
                        n_rows = n
                        status = "OPTIMAL"
                    }
        rows.ToArray()

// ----- Main loop -----

let overallSw = Stopwatch.StartNew()
let writeLock = obj()
let mutable nBucketsDone = 0
let mutable nRowsWritten = 0
let mutable nMissing = 0

let tasksToRun =
    buckets |> Array.filter (fun b ->
        let path = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" b)
        if not (File.Exists path) then
            nMissing <- nMissing + 1
            false
        else
            // Any (X,Y) cell still missing for this bucket?
            xGrid |> Array.exists (fun x ->
                yGrid |> Array.exists (fun y -> not (seen.Contains (b, cellCode x y)))))

printfn "Buckets to process: %d (missing dzn: %d, fully-complete: %d)"
    tasksToRun.Length nMissing (buckets.Length - tasksToRun.Length - nMissing)

let parallelOpts = ParallelOptions(MaxDegreeOfParallelism = jobs)
Parallel.ForEach(tasksToRun, parallelOpts, fun bucket ->
    try
        let rows = processBucket bucket
        lock writeLock (fun () ->
            for r in rows do
                appendCsvRow r
                nRowsWritten <- nRowsWritten + 1
            nBucketsDone <- nBucketsDone + 1
            if nBucketsDone % 100 = 0 || nBucketsDone = tasksToRun.Length then
                let elapsed = overallSw.Elapsed.TotalSeconds
                let rate = float nBucketsDone / max 0.01 elapsed
                let remain = tasksToRun.Length - nBucketsDone
                let etaSec = float remain / max 0.01 rate
                printfn "  [%d/%d] buckets, %d rows, %.1fs elapsed, %.0f buckets/s, eta %.1fs"
                    nBucketsDone tasksToRun.Length nRowsWritten elapsed rate etaSec)
    with ex ->
        lock writeLock (fun () ->
            eprintfn "  [b%d] EXCEPTION: %s" bucket ex.Message)
) |> ignore

csvWriter.Dispose()
csvStream.Dispose()

overallSw.Stop()
printfn ""
printfn "Done. buckets=%d rows=%d missing-dzn=%d in %.1fs"
    nBucketsDone nRowsWritten nMissing overallSw.Elapsed.TotalSeconds
printfn "Results: %s" csvPath
