#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.5.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/sweep_vwap_loss_limit.log"
Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
let logWriter = new StreamWriter(logPath, false)
let tee fmt =
    Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

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
        File.Exists (sprintf "data/trades/%s/%s.parquet" t d))
tee "Trade files present for %d of them" availableEntries.Length

// ----- 2. Configuration -----
let positionSize = 30000.0
let referenceVol = 0.0095
let basePct = 0.005
let decay = 0.9
let exponents = [| -13; -5; -6; -6 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))

tee ""
tee "Bar exponents: [%s]" (String.Join(";", exponents))
tee "Bar pcts:      [%s]" (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f, referenceVol: %.4f" positionSize referenceVol

// Daily loss limit ladder: 12000 * 0.9^i for i = 0..20, plus None
let lossLimitBase = 12000.0
let lossLimitDecay = 0.9
let lossLimits : float option[] =
    [| None
       for i in 0 .. 20 do
           Some (lossLimitBase * (lossLimitDecay ** float i)) |]

tee "Loss limits: None + %d values from $%.0f down to $%.0f"
    21 lossLimitBase (lossLimitBase * (lossLimitDecay ** 20.0))

// ----- 3. Preload trade files -----
tee ""
tee "Preloading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()
let dataset =
    [| for (ticker, date) in availableEntries do
        let path = sprintf "data/trades/%s/%s.parquet" ticker date
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

// ----- 4. Evaluation function -----
let evaluate (lossLimit: float option) =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable totalPnL = 0.0
    let mutable wins = 0
    let mutable losses = 0
    let mutable decisionCount = 0
    let allPositionSizes = ResizeArray<float>()
    for (_, _, window, trades) in dataset do
        let sys = createDynamicVwapSystem (defaultEstimationOffsets, pcts)
        let sim =
            match lossLimit with
            | Some limit -> VwapSimulator(window, sys, positionSize, referenceVol = referenceVol, lossLimit = limit)
            | None -> VwapSimulator(window, sys, positionSize, referenceVol = referenceVol)
        for tr in trades do sim.AddTrade tr
        let r = sim.Result
        totalPnL <- totalPnL + r.RealizedPnL
        decisionCount <- decisionCount + r.Decisions.Count
        if r.RealizedPnL > 0.01 then wins <- wins + 1
        elif r.RealizedPnL < -0.01 then losses <- losses + 1
        let decs = r.Decisions
        for i in 1 .. decs.Count - 1 do
            let prev = decs.[i - 1]
            allPositionSizes.Add (abs prev.Shares * prev.Price)
    let avgPos = if allPositionSizes.Count > 0 then (allPositionSizes |> Seq.sum) / float allPositionSizes.Count else 0.0
    let efficiency = if avgPos > 0.0 then totalPnL / avgPos else 0.0
    totalPnL, wins, losses, decisionCount, avgPos, efficiency, sw.Elapsed.TotalSeconds

// ----- 5. Sweep -----
tee ""
tee "%-14s %10s %8s %10s %6s %6s %6s %6s"
    "lossLimit" "totalP&L" "eff" "avgPos$" "W" "L" "dec" "sec"
tee "%s" (String.replicate 80 "-")

let results = ResizeArray<float option * float * float>()

for ll in lossLimits do
    let total, w, l, dec, avgPos, eff, elapsed = evaluate ll
    results.Add(ll, total, eff)
    let label = match ll with None -> "None" | Some v -> sprintf "%.0f" v
    tee "%-14s %10.2f %8.4f %10.2f %6d %6d %6d %6.1f"
        label total eff avgPos w l dec elapsed

// ----- 6. Summary -----
tee ""
tee "=== Best by total P&L ==="
let bestPnL = results |> Seq.maxBy (fun (_, t, _) -> t)
let rv1, t1, e1 = bestPnL
tee "  lossLimit=%-10s  P&L=$%.2f  eff=%.4f"
    (match rv1 with None -> "None" | Some v -> sprintf "%.0f" v) t1 e1

tee ""
tee "=== Best by efficiency (P&L / avgPos) ==="
let bestEff = results |> Seq.filter (fun (_, t, _) -> t > 0.0) |> Seq.maxBy (fun (_, _, e) -> e)
let rv2, t2, e2 = bestEff
tee "  lossLimit=%-10s  P&L=$%.2f  eff=%.4f"
    (match rv2 with None -> "None" | Some v -> sprintf "%.0f" v) t2 e2
