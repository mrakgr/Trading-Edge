#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/sweep_vwap_per_offset_greedy.log"
Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
let logWriter = new StreamWriter(logPath, false)
let tee fmt =
    Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

// =============================================================================
// Greedy per-offset bar-size-pct sweep (1d hill-climb only)
// =============================================================================
//
// 4 dimensions = the 4 estimation offsets (5s / 30s / 150s / 750s). Each
// dimension has an integer exponent i_k; the actual volume pct at that offset
// is pct_k = basePct * decay ** i_k. We enumerate single-dimension moves
// (4 dims * 8 deltas = 32 neighbors) around the current best, picking one
// unvisited neighbor at random each iteration. When all neighbors are visited
// without improvement, the search terminates (local optimum found).
//
// The sweep runs until a wall-clock budget expires (default 2 hours) or the
// neighborhood is exhausted. The RNG seed is fixed for reproducibility.

let quickRun =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.exists (fun a -> a = "--smoke" || a = "--quick" || a = "--test")

let budget =
    if quickRun then TimeSpan.FromMinutes 10.0
    else TimeSpan.FromHours 2.0

let rngSeed = 42
let rngSkip = 0
let basePct = 0.005
let decay = 0.9
let maxIterations = if quickRun then 1 else Int32.MaxValue

let minExponent = -50
let deltaChoices = [| -4; -3; -2; -1; 1; 2; 3; 4 |]

// ----- 1. Parse the authoritative stocks-in-play list -----
let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
let entryRegex =
    Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")

let entries =
    File.ReadAllText ps1Path
    |> entryRegex.Matches
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Seq.distinct
    |> List.ofSeq

tee "Parsed %d stocks-in-play entries from ps1" entries.Length

let availableEntries =
    entries
    |> List.filter (fun (t, d) ->
        File.Exists (sprintf "data/trades/%s/%s.json" t d))
tee "Trade files present for %d of them" availableEntries.Length

// ----- 2. Preload every trade file into memory -----
tee ""
tee "Preloading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()
let dataset =
    [| for (ticker, date) in availableEntries do
        let path = sprintf "data/trades/%s/%s.json" ticker date
        let trades =
            try Some (loadTrades path)
            with ex ->
                tee "  skip %s/%s: %s" ticker date ex.Message
                None
        match trades with
        | None -> ()
        | Some trades ->
            let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
            let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
            match op, cp with
            | Some o, Some c ->
                yield ticker, date, { openTime = o.Timestamp; closeTime = c.Timestamp }, trades
            | _ ->
                tee "  skip %s/%s: missing open/close print" ticker date |]
tee "Loaded %d files in %.1fs (%d MB working set)"
    dataset.Length
    swLoad.Elapsed.TotalSeconds
    (int (GC.GetTotalMemory(false) / 1024L / 1024L))

GC.Collect()
GC.WaitForPendingFinalizers()

// ----- 3. Evaluation function -----
let positionSize = 30000.0
let referenceVol = 0.0095
let nDim = defaultEstimationOffsets.Length

/// Full pass over the preloaded dataset for a given exponent vector.
/// Returns (totalPnL, wins, losses, flats, totalDecisions, avgPosDollars, returnEfficiency, elapsedSec).
let evaluate (exponents: int[]) =
    let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable total = 0.0
    let mutable wins = 0
    let mutable losses = 0
    let mutable flats = 0
    let mutable decisionCount = 0
    let allPositionSizes = ResizeArray<float>()
    for (_, _, window, trades) in dataset do
        let sys = createDynamicVwapSystem (defaultEstimationOffsets, pcts)
        let sim = VwapSimulator(window, sys, positionSize, referenceVol = referenceVol)
        for tr in trades do sim.AddTrade tr
        let r = sim.Result
        let pnl = r.RealizedPnL
        total <- total + pnl
        decisionCount <- decisionCount + r.Decisions.Count
        if pnl > 0.01 then wins <- wins + 1
        elif pnl < -0.01 then losses <- losses + 1
        else flats <- flats + 1
        let decs = r.Decisions
        for i in 1 .. decs.Count - 1 do
            let prev = decs.[i - 1]
            allPositionSizes.Add (abs prev.Shares * prev.Price)
    let avgPos = if allPositionSizes.Count > 0 then (allPositionSizes |> Seq.sum) / float allPositionSizes.Count else 0.0
    let efficiency = if avgPos > 0.0 then total / avgPos else 0.0
    total, wins, losses, flats, decisionCount, avgPos, efficiency, sw.Elapsed.TotalSeconds

// ----- 4. Greedy walk (1d only) -----
let rng = Random(rngSeed)
for _ in 1 .. rngSkip do rng.Next() |> ignore

let visited = HashSet<struct (int * int * int * int)>()
let history = ResizeArray<int[] * float * float * int>()  // exponents, total, efficiency, evalIdx

let key (v: int[]) = struct (v.[0], v.[1], v.[2], v.[3])

let start = [| -2; -5; -6; -6 |]
let mutable bestExp : int[] = start
let mutable bestEfficiency = Double.NegativeInfinity

tee ""
tee "Greedy walk (1d): seed=%d, budget=%s, base=%.3f, decay=%.3f" rngSeed (if quickRun then "smoke (1 iter)" else "2h") basePct decay
tee "Position size: $%.0f, referenceVol: %.4f" positionSize referenceVol
tee "Start: [%s]" (String.Join(";", start))
tee ""

let swSweep = System.Diagnostics.Stopwatch.StartNew()

// Evaluate the starting point
let total0, w0, l0, f0, d0, avgPos0, eff0, e0 = evaluate start
visited.Add(key start) |> ignore
history.Add(Array.copy start, total0, eff0, 0)
bestExp <- Array.copy start
bestEfficiency <- eff0
tee "[eval   0] [%s]  pcts=[%s]  total=$%9.2f  eff=%7.4f  avgPos=$%7.0f  W/L/F=%3d/%3d/%3d  dec=%5d  (%.1fs)  <-- start"
    (String.Join(";", start))
    (String.Join(";", start |> Array.map (fun i -> sprintf "%.4f" (basePct * (decay ** float i)))))
    total0 eff0 avgPos0 w0 l0 f0 d0 e0

let nextSingleDimNeighbor () =
    let candidates =
        [| for dim in 0 .. nDim - 1 do
             for d in deltaChoices do
                 let c = Array.copy bestExp
                 c.[dim] <- c.[dim] + d
                 if c.[dim] >= minExponent && not (visited.Contains(key c)) then
                     yield c |]
    if candidates.Length = 0 then None
    else Some candidates.[rng.Next candidates.Length]

let mutable evalIdx = 1
let mutable stopped = false
while not stopped do
    if swSweep.Elapsed >= budget then
        tee "[budget] elapsed %.1fs >= %.0fs, stopping" swSweep.Elapsed.TotalSeconds budget.TotalSeconds
        stopped <- true
    elif evalIdx = maxIterations then
        tee "[iter-cap] %d iterations done, stopping (smoke run)" maxIterations
        stopped <- true
    else
        match nextSingleDimNeighbor () with
        | None ->
            tee "[done] all single-dim neighbors of best exhausted, local optimum found"
            stopped <- true
        | Some cand ->
            visited.Add(key cand) |> ignore
            let total, w, l, f, d, avgPos, eff, elapsed = evaluate cand
            history.Add(Array.copy cand, total, eff, evalIdx)
            let marker =
                if eff > bestEfficiency then
                    bestEfficiency <- eff
                    bestExp <- Array.copy cand
                    " <-- new best"
                else ""
            tee "[eval %3d] [%s]  pcts=[%s]  total=$%9.2f  eff=%7.4f  avgPos=$%7.0f  W/L/F=%3d/%3d/%3d  dec=%5d  (%.1fs)%s"
                evalIdx
                (String.Join(";", cand))
                (String.Join(";", cand |> Array.map (fun i -> sprintf "%.4f" (basePct * (decay ** float i)))))
                total eff avgPos w l f d elapsed marker
            evalIdx <- evalIdx + 1

// ----- 5. Report -----
tee ""
tee "Sweep finished in %.1fs after %d evaluations" swSweep.Elapsed.TotalSeconds evalIdx
tee ""
tee "=== Best ==="
tee "Exponents: [%s]" (String.Join(";", bestExp))
tee "Pcts:      [%s]" (String.Join(";", bestExp |> Array.map (fun i -> sprintf "%.5f" (basePct * (decay ** float i)))))
tee "Efficiency: %.4f" bestEfficiency

tee ""
tee "=== All evaluations sorted by efficiency (top 20) ==="
history
|> Seq.sortByDescending (fun (_, _, e, _) -> e)
|> Seq.truncate 20
|> Seq.iter (fun (exp, total, eff, idx) ->
    tee "  #%3d  [%s]  $%9.2f  eff=%7.4f  pcts=[%s]"
        idx
        (String.Join(";", exp))
        total
        eff
        (String.Join(";", exp |> Array.map (fun i -> sprintf "%.4f" (basePct * (decay ** float i))))))
