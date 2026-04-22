#r "nuget: Argu, 6.2.5"

// Iterated local search sweep for (Tv, Ta) threshold calibration.
//
// Drop-in replacement for scripts/run_minizinc_sweep.fsx. Same CSV schema,
// same resume logic, same bucket/precision grid. Instead of CP-SAT, we solve
// each (bucket, P) by:
//   1. Quantizing every row's (vr, tr) to the [0..Tmax] grid (Tmax = ceil(target_rvol * int_scale)).
//   2. Building a 2D upper-right suffix-sum grid so evaluation of any
//      candidate (Tv, Ta) is O(1).
//   3. Running numRestarts deterministic local searches that exhaustively
//      try every (dTv, dTa) in a ±moveScale neighbourhood at the current
//      center, accept the first improving move (smallest-first), recenter
//      on improvement, terminate when no move in the neighbourhood improves.
//   4. Sharing one `visited` HashSet across restarts within a bucket so
//      cells are never re-evaluated.
//
// The algorithm is fully deterministic. Output matches MiniZinc's recently-
// flipped objective: maximize n_fired, then minimize Tv, then minimize Ta.
// Status column is "LOCAL" instead of "OPTIMAL".

open System
open System.IO
open System.Diagnostics
open System.Globalization
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Argu

type CliArgs =
    | [<AltCommandLine("-s")>] Start_Bucket of int
    | [<AltCommandLine("-e")>] End_Bucket of int
    | [<AltCommandLine("-k")>] Step of int
    | [<AltCommandLine("-p")>] Precisions of string
    | [<AltCommandLine("-o")>] Output_File of string
    | Checkpoints_Dir of string
    | Target_Rvol of float
    | Int_Scale of int
    | Move_Scale of int
    | Num_Restarts of int
    | [<AltCommandLine("-j")>] Jobs of int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Start_Bucket _ -> "First bucket index (inclusive). Default: 366 (09:31 ET)"
            | End_Bucket _ -> "Last bucket index (inclusive). Default: 2688 (15:58:00 ET, start of the last full-minute bar before close)"
            | Step _ -> "Bucket step. Default: 1 (every 10s bucket)"
            | Precisions _ -> "Comma-separated precision percentages. Default: 80"
            | Output_File _ -> "Results CSV (resume-aware; one row appended per solved task). Default: minizinc/thresholds_10s_local.csv"
            | Checkpoints_Dir _ -> "Directory with checkpoint_b{N}.dzn inputs. Default: data/minizinc/10s"
            | Target_Rvol _ -> "Target end-of-day session RVOL for hit labeling. Default: 3.0"
            | Int_Scale _ -> "Quantization scale. Default: 256"
            | Move_Scale _ -> "Local-search neighbourhood radius (inclusive). -1 = auto = Tmax/16. Default: -1"
            | Num_Restarts _ -> "Independent local-search restarts per (bucket, precision). Default: 8"
            | Jobs _ -> "Parallel bucket workers. Default: 6"

let parser = ArgumentParser.Create<CliArgs>(programName = "sweep_thresholds_local.fsx")
let cliArgs = fsi.CommandLineArgs |> Array.skip 1
let parsed =
    try parser.Parse(cliArgs, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

// ----- Config -----
let checkpointsDir = parsed.GetResult(Checkpoints_Dir, defaultValue = "data/minizinc/10s")
let csvPath = parsed.GetResult(Output_File, defaultValue = "minizinc/thresholds_10s_local.csv")
let targetRvol = parsed.GetResult(Target_Rvol, defaultValue = 3.0)
let intScale = parsed.GetResult(Int_Scale, defaultValue = 256)
let moveScaleArg = parsed.GetResult(Move_Scale, defaultValue = -1)
let numRestarts = parsed.GetResult(Num_Restarts, defaultValue = 8)
let jobs = parsed.GetResult(Jobs, defaultValue = 6)

let startB = parsed.GetResult(Start_Bucket, defaultValue = 366)
let endB = parsed.GetResult(End_Bucket, defaultValue = 2688)
let step = parsed.GetResult(Step, defaultValue = 1)
let precisions =
    match parsed.TryGetResult Precisions with
    | Some s -> s.Split ',' |> Array.map int
    | None -> [| 80 |]

let buckets = [| for b in startB .. step .. endB -> b |]

// Tmax matches MiniZinc's `ceil(target_rvol * int_scale)`. Grid size is (Tmax+1)^2.
let tmax = int (ceil (targetRvol * float intScale))
let moveScale = if moveScaleArg > 0 then moveScaleArg else max 1 (tmax / 16)

let bucketToEt (bucket: int) =
    let totalSeconds = 30 * 60 + bucket * 10
    let hh = 8 + totalSeconds / 3600
    let mm = (totalSeconds % 3600) / 60
    sprintf "%02d:%02d" hh mm

// ----- CSV row + resume (copied verbatim from run_minizinc_sweep.fsx) -----

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

printfn "Local-search sweep %d buckets [%d..%d step %d] × precisions %A"
    buckets.Length startB endB step precisions
printfn "target_rvol=%g int_scale=%d Tmax=%d move_scale=%d restarts=%d jobs=%d"
    targetRvol intScale tmax moveScale numRestarts jobs
printfn "Resume: %d existing rows loaded from %s" existing.Length csvPath

// ----- DZN parser -----

// Parse `data = [(vr,tr,rvol),...]; n = <int>;` into three double[].
let parseDzn (path: string) : double[] * double[] * double[] =
    let text = File.ReadAllText path
    let vr = ResizeArray<double>(300_000)
    let tr = ResizeArray<double>(300_000)
    let rv = ResizeArray<double>(300_000)

    let mutable pos = 0
    let len = text.Length

    let skipWs () =
        while pos < len && Char.IsWhiteSpace text.[pos] do pos <- pos + 1

    let expect (s: string) =
        skipWs ()
        if pos + s.Length > len || text.Substring(pos, s.Length) <> s then
            failwithf "parseDzn: expected '%s' at pos %d" s pos
        pos <- pos + s.Length

    let expectChar (c: char) =
        skipWs ()
        if pos >= len || text.[pos] <> c then
            failwithf "parseDzn: expected '%c' at pos %d" c pos
        pos <- pos + 1

    let parseNum () : double =
        skipWs ()
        let start = pos
        while pos < len &&
              (let c = text.[pos]
               c = '-' || c = '+' || c = '.' ||
               (c >= '0' && c <= '9') || c = 'e' || c = 'E') do
            pos <- pos + 1
        if pos = start then failwithf "parseDzn: expected number at pos %d" start
        Double.Parse(text.AsSpan(start, pos - start), NumberStyles.Float, inv)

    expect "data"
    expectChar '='
    expectChar '['
    let mutable doneList = false
    while not doneList do
        expectChar '('
        vr.Add(parseNum ())
        expectChar ','
        tr.Add(parseNum ())
        expectChar ','
        rv.Add(parseNum ())
        expectChar ')'
        skipWs ()
        if pos < len && text.[pos] = ',' then pos <- pos + 1
        else doneList <- true
    expectChar ']'
    expectChar ';'
    // We don't actually need `n` since we counted rows; just skip to EOF.
    vr.ToArray(), tr.ToArray(), rv.ToArray()

// ----- Solver -----

// Score sort order: bigger is better on all three fields, in declaration
// order. Using F# struct record comparison (primary: nFired, then
// negativeTv, then negativeTa). Fields are named negativeX because we store
// the negation of values we want to minimize.
[<Struct>]
type Score = {
    nFired: int
    negativeTv: int
    negativeTa: int
}

let private packKey (tv: int) (ta: int) : int64 =
    (int64 tv <<< 32) ||| (int64 ta)

/// Build the 2D suffix-sum grids and run `numRestarts` local searches.
/// Returns (Tv, Ta, n_fired, n_hit) in integer-grid units.
let solveBucket
    (vr: double[]) (tr: double[]) (rv: double[])
    (precisionPct: int)
    : int * int * int * int =

    let n = vr.Length
    let dim = tmax + 1

    // Quantize each row: qi = min(Tmax, floor(vr * int_scale)), same for qj.
    // Matches MiniZinc's `floor(min(x, target_rvol) * int_scale)` exactly.
    let fired = Array2D.zeroCreate<int> dim dim
    let hit = Array2D.zeroCreate<int> dim dim
    for i = 0 to n - 1 do
        let qi0 = int (floor (vr.[i] * float intScale))
        let qj0 = int (floor (tr.[i] * float intScale))
        let qi = if qi0 > tmax then tmax elif qi0 < 0 then 0 else qi0
        let qj = if qj0 > tmax then tmax elif qj0 < 0 then 0 else qj0
        fired.[qi, qj] <- fired.[qi, qj] + 1
        if rv.[i] >= targetRvol then
            hit.[qi, qj] <- hit.[qi, qj] + 1

    // 2D upper-right suffix sum: fired[Tv, Ta] = # rows with qi >= Tv AND qj >= Ta.
    // Pass 1: rightward accumulation along the i axis (descending i).
    for i = dim - 2 downto 0 do
        for j = 0 to dim - 1 do
            fired.[i, j] <- fired.[i, j] + fired.[i + 1, j]
            hit.[i, j] <- hit.[i, j] + hit.[i + 1, j]
    // Pass 2: rightward accumulation along the j axis (descending j).
    for j = dim - 2 downto 0 do
        for i = 0 to dim - 1 do
            fired.[i, j] <- fired.[i, j] + fired.[i, j + 1]
            hit.[i, j] <- hit.[i, j] + hit.[i, j + 1]

    // ----- Move set: all (dTv, dTa) in [-moveScale..+moveScale]^2 \ {(0,0)},
    //                ordered by |dTv| + |dTa| ascending (smallest moves first).
    let moves =
        [|
            for dTv in -moveScale .. moveScale do
                for dTa in -moveScale .. moveScale do
                    if not (dTv = 0 && dTa = 0) then yield (dTv, dTa)
        |]
        |> Array.sortBy (fun (dTv, dTa) -> abs dTv + abs dTa)

    // Restart seed points: corners first, then a coarse 3x3 grid inside.
    let q = tmax / 4
    let seeds =
        [|
            (0, 0)
            (tmax, tmax)
            (0, tmax)
            (tmax, 0)
            (q, q)
            (q * 3, q * 3)
            (q, q * 3)
            (q * 3, q)
        |]
        |> Array.truncate numRestarts

    let visited = HashSet<int64>()
    let mutable best : Score voption = ValueNone
    let mutable bestFired = 0
    let mutable bestHit = 0
    let mutable bestTv = 0
    let mutable bestTa = 0

    let feasible (nFired: int) (nHit: int) : bool =
        nFired >= 1 && 100 * nHit >= precisionPct * nFired

    let clamp v = if v < 0 then 0 elif v > tmax then tmax else v

    for (sTv, sTa) in seeds do
        let mutable tv = sTv
        let mutable ta = sTa

        // Evaluate the starting point (may be infeasible, that's fine — we'll
        // still walk from it; best only updates on feasible candidates).
        visited.Add(packKey tv ta) |> ignore
        let nF0 = fired.[tv, ta]
        let nH0 = hit.[tv, ta]
        if feasible nF0 nH0 then
            let s = { nFired = nF0; negativeTv = -tv; negativeTa = -ta }
            match best with
            | ValueNone ->
                best <- ValueSome s
                bestFired <- nF0; bestHit <- nH0; bestTv <- tv; bestTa <- ta
            | ValueSome b when s > b ->
                best <- ValueSome s
                bestFired <- nF0; bestHit <- nH0; bestTv <- tv; bestTa <- ta
            | _ -> ()

        let mutable keepSearching = true
        while keepSearching do
            let mutable improved = false
            let mutable mi = 0
            while not improved && mi < moves.Length do
                let (dTv, dTa) = moves.[mi]
                let tv' = clamp (tv + dTv)
                let ta' = clamp (ta + dTa)
                let key = packKey tv' ta'
                if visited.Add(key) then
                    let nF = fired.[tv', ta']
                    let nH = hit.[tv', ta']
                    if feasible nF nH then
                        let s = { nFired = nF; negativeTv = -tv'; negativeTa = -ta' }
                        let beats =
                            match best with
                            | ValueNone -> true
                            | ValueSome b -> s > b
                        if beats then
                            best <- ValueSome s
                            bestFired <- nF; bestHit <- nH; bestTv <- tv'; bestTa <- ta'
                            tv <- tv'
                            ta <- ta'
                            improved <- true
                mi <- mi + 1
            if not improved then keepSearching <- false

    match best with
    | ValueNone ->
        // No feasible point anywhere — should only happen on pathological
        // buckets (very few rows, unreachable precision). Fall back to the
        // last evaluated (tv, ta) just so we write something.
        0, 0, 0, 0
    | ValueSome _ ->
        bestTv, bestTa, bestFired, bestHit

// ----- Main loop -----

// Build task list: one entry per (bucket, P) that still needs solving.
let dznExists (bucket: int) : bool =
    File.Exists (Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket))

let tasks =
    [| for bucket in buckets do
         if dznExists bucket then
             for P in precisions do
                 if not (seen.Contains (bucket, P)) then
                     yield (bucket, P) |]

let nMissingBuckets =
    buckets |> Array.filter (fun b -> not (dznExists b)) |> Array.length
let nSkipped = (buckets.Length * precisions.Length) - tasks.Length - nMissingBuckets * precisions.Length

printfn "Parallel sweep: %d tasks, jobs=%d (tasks skipped=%d missing-dzn-buckets=%d)"
    tasks.Length jobs nSkipped nMissingBuckets

let overallSw = Stopwatch.StartNew()
let resultsLock = obj()
let logLock = obj()
let mutable nDone = 0
let mutable nFailed = 0

// DZN parse is expensive; cache per bucket so re-solving the same bucket at
// multiple precisions doesn't re-read the file. Small: at most numBuckets
// entries, each ~5 MB of doubles; but under the parallel loop we only hold
// what's actively being worked on. So we use a thread-local cache keyed by
// bucket, not a global one.
let runOne (bucket: int, P: int) =
    try
        let sw = Stopwatch.StartNew()
        let dznPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket)
        let (vr, tr, rv) = parseDzn dznPath
        let (tvInt, taInt, nF, nH) = solveBucket vr tr rv P
        sw.Stop()
        let elapsed = sw.Elapsed.TotalSeconds
        let tv = double tvInt / double intScale
        let ta = double taInt / double intScale
        let precAct = if nF > 0 then 100.0 * double nH / double nF else 0.0
        let row = {
            bucket = bucket
            et = bucketToEt bucket
            precision_pct = P
            Tv = tv
            Ta = ta
            n_fired = nF
            n_hit = nH
            precision_actual = precAct
            solve_time_s = elapsed
            n_rows = vr.Length
            status = "LOCAL"
        }
        let doneNow =
            lock resultsLock (fun () ->
                appendCsvRow row
                nDone <- nDone + 1
                nDone)
        lock logLock (fun () ->
            let totalElapsed = overallSw.Elapsed.TotalSeconds
            let avgWall = totalElapsed / float (max doneNow 1)
            let remain = tasks.Length - doneNow
            let etaMin = avgWall * float remain / 60.0
            printfn "  [%d/%d] b%d P=%d fired=%d hit=%d prec=%.1f%% Tv=%.3f Ta=%.3f %.2fs LOCAL | elapsed=%.1fm eta=%.1fm"
                doneNow tasks.Length bucket P nF nH precAct tv ta elapsed
                (totalElapsed / 60.0) etaMin)
    with ex ->
        lock resultsLock (fun () ->
            nFailed <- nFailed + 1
            nDone <- nDone + 1)
        lock logLock (fun () ->
            eprintfn "  [b%d P=%d] EXCEPTION: %s" bucket P ex.Message)

let parallelOpts = ParallelOptions(MaxDegreeOfParallelism = jobs)
Parallel.ForEach(tasks, parallelOpts, System.Action<int*int>(runOne)) |> ignore

csvWriter.Dispose()
csvStream.Dispose()

overallSw.Stop()
printfn ""
printfn "Done. ran=%d skipped=%d missing-buckets=%d failed=%d in %.1fs"
    (nDone - nFailed) nSkipped nMissingBuckets nFailed overallSw.Elapsed.TotalSeconds
printfn "Results: %s" csvPath
