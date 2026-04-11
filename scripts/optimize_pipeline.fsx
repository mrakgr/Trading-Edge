#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.5.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TDigest.dll"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/optimize_pipeline.log"
Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
let logWriter = new StreamWriter(logPath, false)
let tee fmt =
    Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

// ----- 1. Parse stocks-in-play list -----
let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
let entryRegex =
    Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")

let entries =
    File.ReadAllText ps1Path
    |> entryRegex.Matches
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Seq.distinct
    |> List.ofSeq

let availableEntries =
    entries
    |> List.filter (fun (t, d) ->
        File.Exists (sprintf "data/trades/%s/%s.parquet" t d))

// ----- 2. Preload trade data -----
tee "Preloading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()

type DayData = {
    Ticker: string
    Date: string
    Trades: Trade[]
    Window: MarketHours
}

let dayData =
    [| for (ticker, date) in availableEntries do
        let path = sprintf "data/trades/%s/%s.parquet" ticker date
        let trades =
            try Some (loadTrades path)
            with _ -> None
        match trades with
        | None -> ()
        | Some trades ->
            let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
            let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
            match op, cp with
            | Some o, Some c ->
                yield {
                    Ticker = ticker
                    Date = date
                    Trades = trades
                    Window = { openTime = o.Timestamp; closeTime = c.Timestamp }
                }
            | _ -> () |]

tee "Loaded %d days in %.1fs" dayData.Length swLoad.Elapsed.TotalSeconds
tee ""

// ----- 3. Fixed configuration -----
#load "config.fsx"
open Config

// ----- 4. Round-trip extraction -----
let extractRoundTrips (fills: Fill array) (commPerShare: float) =
    let trips = ResizeArray<float>()
    let mutable position = 0
    let mutable avgCost = 0.0
    let mutable entryCommission = 0.0

    for f in fills do
        let qty = f.Quantity
        let commission = float (abs qty) * commPerShare

        if position = 0 then
            avgCost <- f.Price
            position <- qty
            entryCommission <- commission
        elif sign qty = sign position then
            let totalQty = position + qty
            avgCost <- (avgCost * float (abs position) + f.Price * float (abs qty)) / float (abs totalQty)
            position <- totalQty
            entryCommission <- entryCommission + commission
        else
            let closingQty = min (abs qty) (abs position)
            let pnl = float (sign position) * (f.Price - avgCost) * float closingQty
            let totalCommission = entryCommission * (float closingQty / float (abs position)) + commission
            trips.Add(pnl - totalCommission)
            let remaining = position + qty
            if remaining = 0 then
                position <- 0
                avgCost <- 0.0
                entryCommission <- 0.0
            elif sign remaining <> sign position then
                entryCommission <- float (abs remaining) * commPerShare
                avgCost <- f.Price
                position <- remaining
            else
                entryCommission <- entryCommission * (1.0 - float closingQty / float (abs position))
                position <- remaining

    trips.ToArray()

// ----- 5. Profit factor from decision-based results (no fill sim) -----
let profitFactorFromDecisions (pcts: float[]) (bandVol: float) =
    let allTradePnLs = ResizeArray<float>()
    for d in dayData do
        let addTrade, getResult, _ =
            createPipeline d.Window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None None
        for tr in d.Trades do addTrade tr
        let r = getResult()
        let decs = r.Decisions
        for i in 1 .. decs.Count - 1 do
            let prev = decs.[i - 1]
            let curr = decs.[i]
            allTradePnLs.Add((curr.Price - prev.Price) * float prev.Shares)
    let wins = allTradePnLs |> Seq.filter (fun p -> p > 0.01) |> Seq.sum
    let losses = allTradePnLs |> Seq.filter (fun p -> p < -0.01) |> Seq.sum |> abs
    if losses > 0.0 then wins / losses else infinity

// ----- 6. Profit factor from fill-simulated results -----
let profitFactorFromFills (pcts: float[]) (bandVol: float) (percentile: float) =
    let fp = { Percentile = percentile; DelayMs = delayMs; CommissionPerShare = commissionPerShare; RejectionRate = rejectionRate; Rng = None }
    let allTripPnLs = ResizeArray<float>()
    for d in dayData do
        let addTrade, _, getFillResult =
            createPipeline d.Window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None (Some fp)
        for tr in d.Trades do addTrade tr
        let fr = getFillResult()
        let trips = extractRoundTrips (fr.Fills |> Seq.toArray) commissionPerShare
        allTripPnLs.AddRange trips
    let wins = allTripPnLs |> Seq.filter (fun p -> p > 0.01) |> Seq.sum
    let losses = allTripPnLs |> Seq.filter (fun p -> p < -0.01) |> Seq.sum |> abs
    if losses > 0.0 then wins / losses else infinity

// ----- 7. Greedy volPcts optimizer (1d hill-climb) -----
let optimizeVolPcts (bandVol: float) (evaluate: float[] -> float) (startExponents: int[]) (label: string) =
    let nDim = startExponents.Length
    let deltaChoices = [| -4; -3; -2; -1; 1; 2; 3; 4 |]
    let minExponent = -50
    let rng = Random(42)
    let visited = HashSet<struct (int * int * int * int)>()
    let key (v: int[]) = struct (v.[0], v.[1], v.[2], v.[3])

    let mutable bestExp = Array.copy startExponents
    let mutable bestPF = evaluate (startExponents |> Array.map (fun i -> basePct * (decay ** float i)))
    visited.Add(key startExponents) |> ignore

    tee "  [%s] start: [%s]  PF=%.4f" label (String.Join(";", startExponents)) bestPF

    let mutable evalIdx = 1
    let mutable stopped = false
    while not stopped do
        let candidates =
            [| for dim in 0 .. nDim - 1 do
                 for d in deltaChoices do
                     let c = Array.copy bestExp
                     c.[dim] <- c.[dim] + d
                     if c.[dim] >= minExponent && not (visited.Contains(key c)) then
                         yield c |]
        if candidates.Length = 0 then
            stopped <- true
        else
            let cand = candidates.[rng.Next candidates.Length]
            visited.Add(key cand) |> ignore
            let pcts = cand |> Array.map (fun i -> basePct * (decay ** float i))
            let pf = evaluate pcts
            let marker =
                if pf > bestPF then
                    bestPF <- pf
                    bestExp <- Array.copy cand
                    " <-- new best"
                else ""
            if evalIdx % 10 = 0 || marker <> "" then
                tee "  [%s] eval %3d: [%s]  PF=%.4f%s" label evalIdx (String.Join(";", cand)) pf marker
            evalIdx <- evalIdx + 1

    tee "  [%s] done after %d evals. Best: [%s]  PF=%.4f" label evalIdx (String.Join(";", bestExp)) bestPF
    bestExp, bestPF

// =============================================================================
// PIPELINE
// =============================================================================

let swTotal = System.Diagnostics.Stopwatch.StartNew()

tee "============================================================"
tee "STAGE 1: Sweep bandVol in [0.0, 2.0] step 0.1 (no fill sim)"
tee "============================================================"
tee ""

let startExponents = exponents
let startPcts = pcts

let bandVolCandidates = [| for i in 0 .. 20 -> float i * 0.1 |]

tee "%-10s %10s" "bandVol" "ProfFactor"
tee "%s" (String.replicate 25 "-")

let bandVolResults =
    [| for bv in bandVolCandidates do
        let pf = profitFactorFromDecisions startPcts bv
        tee "%-10.1f %10.4f" bv pf
        yield bv, pf |]

let bestBandVol, bestBandVolPF = bandVolResults |> Array.maxBy snd
tee ""
tee "Best bandVol: %.1f  PF=%.4f" bestBandVol bestBandVolPF
tee ""

// =============================================================================
tee "============================================================"
tee "STAGE 2: Optimize volPcts (no fill sim) at bandVol=%.1f" bestBandVol
tee "============================================================"
tee ""

let bestExponents1, bestPF1 =
    optimizeVolPcts bestBandVol (fun pcts -> profitFactorFromDecisions pcts bestBandVol) startExponents "stage2"

let bestPcts1 = bestExponents1 |> Array.map (fun i -> basePct * (decay ** float i))
tee ""
tee "Stage 2 result: exponents=[%s]  PF=%.4f" (String.Join(";", bestExponents1)) bestPF1
tee ""

// =============================================================================
tee "============================================================"
tee "STAGE 3: Sweep fill percentile at bandVol=%.1f" bestBandVol
tee "============================================================"
tee ""

let fillPercentiles = [| 0.1; 0.2; 0.3; 0.4; 0.5; 0.6; 0.7 |]

tee "%-12s %10s" "Percentile" "ProfFactor"
tee "%s" (String.replicate 25 "-")

let fillResults =
    [| for pctile in fillPercentiles do
        let pf = profitFactorFromFills bestPcts1 bestBandVol pctile
        tee "%-12.1f %10.4f" pctile pf
        yield pctile, pf |]

let bestPercentile, bestFillPF = fillResults |> Array.maxBy snd
tee ""
tee "Best percentile: %.1f  PF=%.4f" bestPercentile bestFillPF
tee ""

// =============================================================================
tee "============================================================"
tee "STAGE 4: Re-optimize volPcts with fill sim (pctile=%.1f)" bestPercentile
tee "============================================================"
tee ""

let bestExponents2, bestPF2 =
    optimizeVolPcts bestBandVol
        (fun pcts -> profitFactorFromFills pcts bestBandVol bestPercentile)
        bestExponents1
        "stage4"

let bestPcts2 = bestExponents2 |> Array.map (fun i -> basePct * (decay ** float i))
tee ""
tee "Stage 4 result: exponents=[%s]  PF=%.4f" (String.Join(";", bestExponents2)) bestPF2
tee ""

// =============================================================================
tee "============================================================"
tee "FINAL RESULTS"
tee "============================================================"
tee ""
tee "bandVol:     %.1f" bestBandVol
tee "Exponents:   [%s]" (String.Join(";", bestExponents2))
tee "Pcts:        [%s]" (String.Join(";", bestPcts2 |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Percentile:  %.1f" bestPercentile
tee "Profit Factor: %.4f" bestPF2
tee ""
tee "Total time: %.1fs" swTotal.Elapsed.TotalSeconds

logWriter.Close()
