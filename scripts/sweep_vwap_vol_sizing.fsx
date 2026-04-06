#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/sweep_vwap_vol_sizing.log"
Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
let logWriter = new StreamWriter(logPath, false)
let tee fmt =
    Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

// =============================================================================
// Sweep over referenceVol for volatility-adjusted position sizing
// =============================================================================
//
// Fixed bar-size exponents at the known optimum [-2;-5;-6;-6].
// Sweeps referenceVol from None (baseline) through a range of values.
// Reports: total P&L, per-trade variance, avg position size,
//          and the risk-adjusted metric (variance / avg position size).

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

// ----- 3. Configuration -----
let positionSize = 30000.0
let basePct = 0.005
let decay = 0.9
let exponents = [| -2; -5; -6; -6 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))

tee ""
tee "Bar exponents: [%s]" (String.Join(";", exponents))
tee "Bar pcts:      [%s]" (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f" positionSize

// ----- 4. Evaluation function -----

/// Runs the full dataset for a given referenceVol (None = no vol adjustment).
/// Returns: totalPnL, perTradePnLVariance, avgPositionSize, riskMetric, totalDecisions, elapsed
let evaluate (refVol: float option) =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable totalPnL = 0.0
    let mutable wins = 0
    let mutable losses = 0
    let allTradePnLs = ResizeArray<float>()
    let allPositionSizes = ResizeArray<float>()
    for (_, _, window, trades) in dataset do
        let sys = createDynamicVwapSystem (defaultEstimationOffsets, pcts)
        let sim = VwapSimulator(window, sys, positionSize, ?referenceVol = refVol)
        for tr in trades do sim.AddTrade tr
        let r = sim.Result
        totalPnL <- totalPnL + r.RealizedPnL
        if r.RealizedPnL > 0.01 then wins <- wins + 1
        elif r.RealizedPnL < -0.01 then losses <- losses + 1
        // Collect per-trade P&L and position sizes
        let decs = r.Decisions
        for i in 1 .. decs.Count - 1 do
            let prev = decs.[i - 1]
            let curr = decs.[i]
            let tradePnL = (curr.Price - prev.Price) * prev.Shares
            allTradePnLs.Add tradePnL
            allPositionSizes.Add (abs prev.Shares * prev.Price)
    // Compute variance of per-trade P&L
    let n = float allTradePnLs.Count
    let meanPnL = if n > 0.0 then (allTradePnLs |> Seq.sum) / n else 0.0
    let variancePnL =
        if n > 1.0 then
            let mutable s = 0.0
            for p in allTradePnLs do
                let d = p - meanPnL
                s <- s + d * d
            s / (n - 1.0)
        else 0.0
    let avgPosSizeDollars =
        if allPositionSizes.Count > 0 then (allPositionSizes |> Seq.sum) / float allPositionSizes.Count
        else 0.0
    let riskMetric = if avgPosSizeDollars > 0.0 then totalPnL / avgPosSizeDollars else 0.0
    totalPnL, variancePnL, avgPosSizeDollars, riskMetric, wins, losses, allTradePnLs.Count, sw.Elapsed.TotalSeconds

// ----- 5. Sweep -----
// Test None (baseline) plus a range of referenceVol values
let refVolBase = 0.012
let refVolDecay = 1.005
let refVolCandidates : float option list =
    [ None
      for i in 0 .. -1 .. -80 do
          Some (refVolBase * (refVolDecay ** float i)) ]

tee ""
tee "%-12s %10s %12s %10s %10s %6s %6s %6s %6s"
    "refVol" "totalP&L" "variance" "avgPos$" "risk" "W" "L" "trades" "sec"
tee "%s" (String.replicate 90 "-")

let results = ResizeArray<float option * float * float * float * float>()

for rv in refVolCandidates do
    let total, var, avgPos, risk, w, l, trades, elapsed = evaluate rv
    results.Add(rv, total, var, avgPos, risk)
    let label = match rv with None -> "None" | Some v -> sprintf "%.6f" v
    tee "%-12s %10.2f %12.4f %10.2f %10.6f %6d %6d %6d %6.1f"
        label total var avgPos risk w l trades elapsed

// ----- 6. Summary -----
tee ""
tee "=== Best by total P&L ==="
let bestPnL = results |> Seq.maxBy (fun (_, t, _, _, _) -> t)
let rv1, t1, v1, a1, r1 = bestPnL
tee "  refVol=%-10s  P&L=$%.2f  var=%.4f  avgPos=$%.2f  risk=%.6f"
    (match rv1 with None -> "None" | Some v -> sprintf "%.4f" v) t1 v1 a1 r1

tee ""
tee "=== Best by return efficiency (totalP&L / avgPos, higher is better) ==="
let bestRisk = results |> Seq.filter (fun (_, t, _, _, _) -> t > 0.0) |> Seq.maxBy (fun (_, _, _, _, r) -> r)
let rv2, t2, v2, a2, r2 = bestRisk
tee "  refVol=%-10s  P&L=$%.2f  var=%.4f  avgPos=$%.2f  risk=%.6f"
    (match rv2 with None -> "None" | Some v -> sprintf "%.4f" v) t2 v2 a2 r2
