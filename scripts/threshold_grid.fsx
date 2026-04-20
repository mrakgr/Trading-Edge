#r "nuget: Argu, 6.2.5"
#r "nuget: FSharp.SystemTextJson, 1.4.36"

// Grid sweep over (precision, target_rvol) for a single bucket. For each cell
// we (1) solve the threshold MiniZinc model with that target and P, (2) parse
// Tv, Ta, (3) evaluate mean/median session_volume_rvol_final over the fired
// rows in the checkpoint JSON. Output: a tidy JSON array with all columns,
// and a human-readable grid printed to stdout with mean-RVOL as the cell value.

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Globalization
open Argu

type CliArgs =
    | [<Mandatory; AltCommandLine("-b")>] Bucket of int
    | [<AltCommandLine("-p")>] Precisions of string
    | [<AltCommandLine("-t")>] Targets of string
    | Checkpoints_Dir of string
    | Model of string
    | Var_Scale of int
    | Threshold_Scale of int
    | Output_File of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Bucket _ -> "Bucket to sweep (required)."
            | Precisions _ -> "Comma-separated P percentages. Default: 70,80,90."
            | Targets _ -> "Comma-separated target RVOLs. Default: 3.0,3.1,...,4.0."
            | Checkpoints_Dir _ -> "Default: data/minizinc/10s."
            | Model _ -> "Default: minizinc/stock_in_play_threshold_int.mzn."
            | Var_Scale _ -> "Default: 1024."
            | Threshold_Scale _ -> "Default: 1024."
            | Output_File _ -> "Default: minizinc/threshold_grid_b{N}.json."

let parser = ArgumentParser.Create<CliArgs>(programName = "threshold_grid.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let bucket = parsed.GetResult Bucket
let precisions =
    match parsed.TryGetResult Precisions with
    | Some s -> s.Split ',' |> Array.map int
    | None -> [| 70; 80; 90 |]
let targets =
    match parsed.TryGetResult Targets with
    | Some s -> s.Split ',' |> Array.map (fun x -> Double.Parse(x, CultureInfo.InvariantCulture))
    | None -> [| for i in 0 .. 10 -> 3.0 + float i * 0.1 |]
let checkpointsDir = parsed.GetResult(Checkpoints_Dir, defaultValue = "data/minizinc/10s")
let modelPath = parsed.GetResult(Model, defaultValue = "minizinc/stock_in_play_threshold_int.mzn")
let varScale = parsed.GetResult(Var_Scale, defaultValue = 1024)
let thresholdScale = parsed.GetResult(Threshold_Scale, defaultValue = 1024)
let outPath = parsed.GetResult(Output_File, defaultValue = sprintf "minizinc/threshold_grid_b%d.json" bucket)

let inv = CultureInfo.InvariantCulture

// ----- Load checkpoint rows once -----
let checkpointPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.json" bucket)
if not (File.Exists checkpointPath) then
    eprintfn "Checkpoint JSON not found: %s — run export_checkpoint_cumulatives_10s.fsx first." checkpointPath
    exit 1

type Row = {
    cum_volume: double
    cum_transactions: double
    avg_vol_raw_4w: double        // = avg_session_adj_volume_4w / split_factor_today
    avg_txn_4w: double
    rvol_final: double
}

let rows =
    use fs = File.OpenRead checkpointPath
    let doc = JsonDocument.Parse(fs)
    [|
        for el in doc.RootElement.EnumerateArray() do
            let cv = el.GetProperty("cum_volume").GetDouble()
            let ct = el.GetProperty("cum_transactions").GetDouble()
            let avgAdj = el.GetProperty("avg_session_adj_volume_4w").GetDouble()
            let avgTxn = el.GetProperty("avg_session_transactions_4w").GetDouble()
            let split = el.GetProperty("split_factor_today").GetDouble()
            let rvolFinal = el.GetProperty("session_volume_rvol_final").GetDouble()
            if avgAdj > 0.0 && split > 0.0 && avgTxn > 0.0 then
                yield {
                    cum_volume = cv
                    cum_transactions = ct
                    avg_vol_raw_4w = avgAdj / split
                    avg_txn_4w = avgTxn
                    rvol_final = rvolFinal
                }
    |]

printfn "Loaded %d valid rows from %s" rows.Length checkpointPath

// ----- Convert to DZN file (one-shot for this bucket) -----
let dznPath = Path.Combine(checkpointsDir, sprintf "checkpoint_b%d.dzn" bucket)
if not (File.Exists dznPath) then
    let psi = ProcessStartInfo("dotnet",
                sprintf "fsi scripts/convert_checkpoint_to_dzn.fsx -- --bucket %d --dir %s" bucket checkpointsDir)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use p = Process.Start(psi)
    p.WaitForExit()
    if p.ExitCode <> 0 then
        eprintfn "DZN conversion failed: %s" (p.StandardError.ReadToEnd())
        exit 1

// ----- Solver driver -----
let runMinizinc (precisionPct: int) (targetRvol: double) =
    let args =
        sprintf "--solver cp-sat %s %s -D \"precision_pct=%d\" -D \"var_scale=%d\" -D \"threshold_scale=%d\" -D \"target_rvol=%s\""
            modelPath dznPath precisionPct varScale thresholdScale
            (targetRvol.ToString("F1", inv))
    let psi = ProcessStartInfo("minizinc", args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    let sw = Stopwatch.StartNew()
    use p = Process.Start(psi)
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()
    sw.Stop()
    let parseJson (text: string) =
        let trimmed = text.Replace("==========", "").Replace("----------", "").Trim()
        let lastClose = trimmed.LastIndexOf '}'
        if lastClose < 0 then None
        else
            let firstOpen = trimmed.LastIndexOf('{', lastClose)
            if firstOpen < 0 then None
            else
                try
                    let doc = JsonDocument.Parse(trimmed.Substring(firstOpen, lastClose - firstOpen + 1))
                    let root = doc.RootElement
                    Some (root.GetProperty("Tv").GetDouble(),
                          root.GetProperty("Ta").GetDouble(),
                          root.GetProperty("n_fired").GetInt32(),
                          root.GetProperty("n_hit").GetInt32())
                with _ -> None
    match p.ExitCode, parseJson stdout with
    | 0, Some (tv, ta, nf, nh) when nf > 0 ->
        let isOptimal = stdout.Contains "=========="
        Ok (tv, ta, nf, nh, isOptimal, sw.Elapsed.TotalSeconds)
    | code, _ ->
        Error (sprintf "solver exit=%d stderr=%s stdout_tail=%s"
                code (stderr.Substring(0, min 200 stderr.Length))
                (stdout.Substring(max 0 (stdout.Length - 200))))

// ----- Grid loop -----
type Cell = {
    precision_pct: int
    target_rvol: double
    Tv: double
    Ta: double
    n_fired_solver: int
    n_hit_solver: int
    precision_actual: double
    n_fired_eval: int
    mean_rvol: double
    median_rvol: double
    status: string
    solve_time_s: double
}

let eval (tv: double) (ta: double) =
    let fired =
        rows
        |> Array.filter (fun r ->
            r.cum_volume / r.avg_vol_raw_4w >= tv
            && r.cum_transactions / r.avg_txn_4w >= ta)
        |> Array.map (fun r -> r.rvol_final)
    let n = fired.Length
    if n = 0 then 0, 0.0, 0.0
    else
        let mean = Array.average fired
        Array.sortInPlace fired
        let median = fired.[n / 2]
        n, mean, median

let cells = ResizeArray<Cell>()
printfn ""
printfn "Sweeping %d precisions x %d targets = %d cells" precisions.Length targets.Length (precisions.Length * targets.Length)

for p in precisions do
    for t in targets do
        match runMinizinc p t with
        | Ok (tv, ta, nfSolver, nhSolver, opt, secs) ->
            let nfEval, meanR, medR = eval tv ta
            let cell =
                { precision_pct = p
                  target_rvol = t
                  Tv = tv; Ta = ta
                  n_fired_solver = nfSolver
                  n_hit_solver = nhSolver
                  precision_actual = if nfSolver > 0 then 100.0 * double nhSolver / double nfSolver else 0.0
                  n_fired_eval = nfEval
                  mean_rvol = meanR
                  median_rvol = medR
                  status = if opt then "OPTIMAL" else "FEASIBLE"
                  solve_time_s = secs }
            cells.Add cell
            printfn "  P=%d target=%.1f Tv=%.3f Ta=%.3f fired=%d meanR=%.2f medR=%.2f %.1fs %s"
                p t tv ta nfEval meanR medR secs cell.status
        | Error msg ->
            eprintfn "  P=%d target=%.1f FAILED: %s" p t msg

// ----- Write results -----
let sb = System.Text.StringBuilder()
sb.Append "[\n" |> ignore
for i = 0 to cells.Count - 1 do
    if i > 0 then sb.Append ",\n" |> ignore
    let c = cells.[i]
    sb.AppendFormat(inv,
        """  {{"precision_pct": {0}, "target_rvol": {1:F1}, "Tv": {2:F6}, "Ta": {3:F6}, "n_fired_solver": {4}, "n_hit_solver": {5}, "precision_actual": {6:F2}, "n_fired_eval": {7}, "mean_rvol": {8:F4}, "median_rvol": {9:F4}, "status": "{10}", "solve_time_s": {11:F2}}}""",
        c.precision_pct, c.target_rvol, c.Tv, c.Ta, c.n_fired_solver, c.n_hit_solver,
        c.precision_actual, c.n_fired_eval, c.mean_rvol, c.median_rvol, c.status, c.solve_time_s)
    |> ignore
sb.Append "\n]\n" |> ignore
Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
File.WriteAllText(outPath, sb.ToString())
printfn ""
printfn "Wrote %s (%d cells)" outPath cells.Count

// ----- Human-readable grids: mean_rvol and (Tv,Ta) by (P, target) -----
let findCell p t =
    cells |> Seq.tryFind (fun c -> c.precision_pct = p && c.target_rvol = t)

printfn ""
printfn "mean_rvol grid (rows=P%%, cols=target_rvol)"
printf   "  P \\ T "
for t in targets do printf "   %5.1f" t
printfn ""
for p in precisions do
    printf "  %4d  " p
    for t in targets do
        match findCell p t with
        | Some c -> printf "   %5.2f" c.mean_rvol
        | None   -> printf "       -"
    printfn ""

printfn ""
printfn "Tv threshold grid"
printf   "  P \\ T "
for t in targets do printf "   %5.1f" t
printfn ""
for p in precisions do
    printf "  %4d  " p
    for t in targets do
        match findCell p t with
        | Some c -> printf "   %5.3f" c.Tv
        | None   -> printf "       -"
    printfn ""

printfn ""
printfn "Ta threshold grid"
printf   "  P \\ T "
for t in targets do printf "   %5.1f" t
printfn ""
for p in precisions do
    printf "  %4d  " p
    for t in targets do
        match findCell p t with
        | Some c -> printf "   %5.3f" c.Ta
        | None   -> printf "       -"
    printfn ""

printfn ""
printfn "n_fired grid (solver)"
printf   "  P \\ T "
for t in targets do printf "   %5.1f" t
printfn ""
for p in precisions do
    printf "  %4d  " p
    for t in targets do
        match findCell p t with
        | Some c -> printf "   %5d" c.n_fired_solver
        | None   -> printf "       -"
    printfn ""
