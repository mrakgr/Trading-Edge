#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// =============================================================================
// Greedy per-offset bar-size-pct sweep
// =============================================================================
//
// 4 dimensions = the 4 estimation offsets (5s / 30s / 150s / 750s). Each
// dimension has an integer exponent i_k; the actual volume pct at that offset
// is pct_k = basePct * decay ** i_k. We start from i = (0,0,0,0) — i.e. all
// 0.5 — and random-walk one dimension at a time by ±1, evaluating each
// proposal as a full 106-file pass. Hill-climb: only accept proposals that
// improve total P&L. Visited vectors are remembered so we don't redo work.
//
// The sweep runs until a wall-clock budget expires (default 2 hours). The
// RNG seed is fixed so that re-running gives the same sequence; a future
// resume can skip the first N draws to pick up where we left off.
//
// Control knobs: quickRun = true forces a 1-iteration smoke test so we can
// verify the plumbing without burning the full budget.

let quickRun =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.exists (fun a -> a = "--smoke" || a = "--quick" || a = "--test")

let budget =
    if quickRun then TimeSpan.FromMinutes 10.0  // upper bound for 1 eval; won't actually be hit
    else TimeSpan.FromHours 2.0

let rngSeed = 42
let rngSkip = 0            // bump this if resuming from a prior run
let basePct = 0.005
let decay = 0.9
let maxIterations = if quickRun then 1 else Int32.MaxValue

// Reject proposals whose pct exceeds 1.0 (i.e. a bar larger than the day's
// total volume, which would never fill). 0.005 * 0.9^i <= 1.0 ⇔ i >= -50.
let minExponent = -50

// Per-step delta sampled from [-4..4] \ {0}. One dimension at a time, so
// each iteration has 4 * 8 = 32 possible neighbors to explore.
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

printfn "Parsed %d stocks-in-play entries from ps1" entries.Length

let availableEntries =
    entries
    |> List.filter (fun (t, d) ->
        File.Exists (sprintf "data/trades/%s/%s.json" t d))
printfn "Trade files present for %d of them" availableEntries.Length

// ----- 2. Preload every trade file into memory -----
// With the sweep doing hundreds of single-candidate passes, reloading JSON
// each time dominates the runtime. Pay the ~1min load cost once up front.
printfn ""
printfn "Preloading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()
let dataset =
    [| for (ticker, date) in availableEntries do
        let path = sprintf "data/trades/%s/%s.json" ticker date
        let trades =
            try Some (loadTrades path)
            with ex ->
                printfn "  skip %s/%s: %s" ticker date ex.Message
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
                printfn "  skip %s/%s: missing open/close print" ticker date |]
printfn "Loaded %d files in %.1fs (%d MB working set)"
    dataset.Length
    swLoad.Elapsed.TotalSeconds
    (int (GC.GetTotalMemory(false) / 1024L / 1024L))

GC.Collect()
GC.WaitForPendingFinalizers()

// ----- 3. Evaluation function -----
let positionSize = 1000.0
let nDim = defaultEstimationOffsets.Length

/// Full pass over the preloaded dataset for a given exponent vector.
/// Returns (totalPnL, wins, losses, flats, totalDecisions, elapsedSec).
let evaluate (exponents: int[]) =
    let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable total = 0.0
    let mutable wins = 0
    let mutable losses = 0
    let mutable flats = 0
    let mutable decisionCount = 0
    for (_, _, window, trades) in dataset do
        let sys = createDynamicVwapSystem (defaultEstimationOffsets, pcts)
        let sim = VwapSimulator(window, sys, positionSize)
        for tr in trades do sim.AddTrade tr
        let r = sim.Result
        let pnl = r.RealizedPnL
        total <- total + pnl
        decisionCount <- decisionCount + r.Decisions.Count
        if pnl > 0.01 then wins <- wins + 1
        elif pnl < -0.01 then losses <- losses + 1
        else flats <- flats + 1
    total, wins, losses, flats, decisionCount, sw.Elapsed.TotalSeconds

// ----- 4. Greedy walk -----
let rng = Random(rngSeed)
for _ in 1 .. rngSkip do rng.Next() |> ignore

let visited = HashSet<struct (int * int * int * int)>()
let history = ResizeArray<int[] * float * int>()  // exponents, total, evalIdx

let key (v: int[]) = struct (v.[0], v.[1], v.[2], v.[3])

let start = Array.zeroCreate<int> nDim
let mutable bestExp : int[] = start
let mutable bestTotal = Double.NegativeInfinity

printfn ""
printfn "Greedy walk: seed=%d, budget=%s, base=%.3f, decay=%.3f"
    rngSeed (if quickRun then "smoke (1 iter)" else "2h") basePct decay
printfn "Start: [%s]" (String.Join(";", start))
printfn ""

let swSweep = System.Diagnostics.Stopwatch.StartNew()

// Evaluate the starting point first
let total0, w0, l0, f0, d0, e0 = evaluate start
visited.Add(key start) |> ignore
history.Add(Array.copy start, total0, 0)
bestExp <- Array.copy start
bestTotal <- total0
printfn "[eval   0] [%s]  pcts=[%s]  total=$%9.2f  W/L/F=%3d/%3d/%3d  dec=%5d  (%.1fs)  <-- start"
    (String.Join(";", start))
    (String.Join(";", start |> Array.map (fun i -> sprintf "%.4f" (basePct * (decay ** float i)))))
    total0 w0 l0 f0 d0 e0

let mutable evalIdx = 1
let mutable stopped = false
while not stopped do
    if swSweep.Elapsed >= budget then
        printfn "[budget] elapsed %.1fs >= %.0fs, stopping"
            swSweep.Elapsed.TotalSeconds budget.TotalSeconds
        stopped <- true
    elif evalIdx >= maxIterations + 1 then
        printfn "[iter-cap] %d iterations done, stopping (smoke run)" maxIterations
        stopped <- true
    else
        // Sample a fresh neighbor of bestExp. Retry up to 128 times if the
        // proposal was already visited or out of bounds.
        let mutable proposal : int[] option = None
        let mutable attempts = 0
        while proposal.IsNone && attempts < 128 do
            let dim = rng.Next nDim
            let delta = deltaChoices.[rng.Next deltaChoices.Length]
            let cand = Array.copy bestExp
            cand.[dim] <- cand.[dim] + delta
            if cand.[dim] >= minExponent && not (visited.Contains(key cand)) then
                proposal <- Some cand
            attempts <- attempts + 1
        match proposal with
        | None ->
            printfn "[stuck] no fresh neighbor of best after 128 tries, stopping"
            stopped <- true
        | Some cand ->
            visited.Add(key cand) |> ignore
            let total, w, l, f, d, elapsed = evaluate cand
            history.Add(Array.copy cand, total, evalIdx)
            let marker =
                if total > bestTotal then
                    bestTotal <- total
                    bestExp <- Array.copy cand
                    " <-- new best"
                else ""
            printfn "[eval %3d] [%s]  pcts=[%s]  total=$%9.2f  W/L/F=%3d/%3d/%3d  dec=%5d  (%.1fs)%s"
                evalIdx
                (String.Join(";", cand))
                (String.Join(";", cand |> Array.map (fun i -> sprintf "%.4f" (basePct * (decay ** float i)))))
                total w l f d elapsed marker
            evalIdx <- evalIdx + 1

// ----- 5. Report -----
printfn ""
printfn "Sweep finished in %.1fs after %d evaluations"
    swSweep.Elapsed.TotalSeconds evalIdx
printfn ""
printfn "=== Best ==="
printfn "Exponents: [%s]" (String.Join(";", bestExp))
printfn "Pcts:      [%s]"
    (String.Join(";", bestExp |> Array.map (fun i -> sprintf "%.5f" (basePct * (decay ** float i)))))
printfn "Total P&L: $%.2f" bestTotal

printfn ""
printfn "=== All evaluations sorted by total P&L (top 20) ==="
history
|> Seq.sortByDescending (fun (_, t, _) -> t)
|> Seq.truncate 20
|> Seq.iter (fun (exp, total, idx) ->
    printfn "  #%3d  [%s]  $%9.2f  pcts=[%s]"
        idx
        (String.Join(";", exp))
        total
        (String.Join(";", exp |> Array.map (fun i -> sprintf "%.4f" (basePct * (decay ** float i))))))
