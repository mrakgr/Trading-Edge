#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.5.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TDigest.dll"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/rejection_sensitivity.log"
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

// ----- 2. Configuration -----
#load "config.fsx"
open Config

// Rejection rates to sweep and number of seeds per rate
let rejectionRates = [| 0.0; 0.05; 0.10; 0.15; 0.20; 0.25; 0.30; 0.35; 0.40; 0.45; 0.50 |]
let numSeeds = 10
let baseSeed = 42

tee "=== Rejection Rate Sensitivity & Variance Analysis ==="
tee "Bar exponents: [%s]  pcts: [%s]"
    (String.Join(";", exponents))
    (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f  referenceVol: %.4f  bandVol: %.2f  lossLimit: $%.0f" positionSize referenceVol bandVol lossLimit
tee "Fill sim: pctile=%.3f, delay=%.0fms, commission=$%.4f/share" percentile delayMs commissionPerShare
tee "Rejection rates: %s" (String.Join(", ", rejectionRates |> Array.map (fun r -> sprintf "%.0f%%" (r * 100.0))))
tee "Seeds per rate: %d (base=%d)" numSeeds baseSeed
tee "Available days: %d" availableEntries.Length
tee ""

// ----- 3. Preload trade data -----
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

// ----- 5. Single-run evaluation -----
type RunResult = {
    NetPnL: float
    ProfitFactor: float
    WinRate: float
    RoundTrips: int
}

let runOnce (rejRate: float) (seed: int) : RunResult =
    // A single seeded Random drives both the rejection check AND the t-digest
    // QuickSort pivot selection (via MergingDigest.SortRng), so each (rate, seed)
    // pair produces a fully deterministic outcome. This also makes concurrent
    // runs safe because each run gets its own Random instance.
    let rng = Random(seed)
    let fp = {
        Percentile = percentile
        DelayMs = delayMs
        CommissionPerShare = commissionPerShare
        RejectionRate = rejRate
        Rng = Some rng
    }
    let allTrips = ResizeArray<float>()
    let mutable netPnL = 0.0
    for d in dayData do
        let addTrade, _, getFillResult =
            createPipeline d.Window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None (Some fp)
        for tr in d.Trades do addTrade tr
        let fr = getFillResult()
        netPnL <- netPnL + fr.RealizedPnL
        let trips = extractRoundTrips (fr.Fills |> Seq.toArray) commissionPerShare
        allTrips.AddRange trips
    let wins = allTrips |> Seq.filter (fun p -> p > 0.01) |> Seq.sum
    let losses = allTrips |> Seq.filter (fun p -> p < -0.01) |> Seq.sum |> abs
    let winCount = allTrips |> Seq.filter (fun p -> p > 0.01) |> Seq.length
    let lossCount = allTrips |> Seq.filter (fun p -> p < -0.01) |> Seq.length
    let pf = if losses > 0.0 then wins / losses else infinity
    let winRate = if winCount + lossCount > 0 then 100.0 * float winCount / float (winCount + lossCount) else 0.0
    {
        NetPnL = netPnL
        ProfitFactor = pf
        WinRate = winRate
        RoundTrips = allTrips.Count
    }

// ----- 6. Statistics -----
let mean (xs: float[]) = Array.average xs
let stddev (xs: float[]) =
    let m = mean xs
    let variance = (xs |> Array.sumBy (fun x -> (x - m) ** 2.0)) / float xs.Length
    sqrt variance

let percentileOf (xs: float[]) (p: float) =
    let sorted = Array.sort xs
    let idx = (float sorted.Length - 1.0) * p
    let lo = int (floor idx)
    let hi = int (ceil idx)
    if lo = hi then sorted.[lo]
    else
        let frac = idx - float lo
        sorted.[lo] * (1.0 - frac) + sorted.[hi] * frac

// ----- 7. Run sweep -----
tee "=== Results (mean ± std over %d seeds per rejection rate) ===" numSeeds
tee "%-10s %10s %10s %10s %10s %10s %10s %10s %10s"
    "RejRate" "PF_mean" "PF_std" "PF_min" "PF_median" "PF_max" "PnL_mean" "PnL_std" "Trips_mean"
tee "%s" (String.replicate 100 "-")

let swTotal = System.Diagnostics.Stopwatch.StartNew()
let allResults = ResizeArray<float * RunResult[]>()

for rejRate in rejectionRates do
    // Parallel over seeds: each runOnce owns its own Random, so concurrent
    // runs are safe and reproducible (seed → result is a pure function).
    let results =
        [| 0 .. numSeeds - 1 |]
        |> Array.Parallel.map (fun i -> runOnce rejRate (baseSeed + i))
    let pfs = results |> Array.map (fun r -> r.ProfitFactor)
    let pnls = results |> Array.map (fun r -> r.NetPnL)
    let trips = results |> Array.map (fun r -> float r.RoundTrips)
    tee "%-10.2f %10.4f %10.4f %10.4f %10.4f %10.4f %10.0f %10.0f %10.0f"
        rejRate
        (mean pfs) (stddev pfs)
        (Array.min pfs) (percentileOf pfs 0.5) (Array.max pfs)
        (mean pnls) (stddev pnls) (mean trips)
    allResults.Add(rejRate, results)

tee ""
tee "Total time: %.1fs" swTotal.Elapsed.TotalSeconds
tee ""

// ----- 8. Full per-seed detail -----
tee "=== Per-Seed Detail ==="
for (rejRate, results) in allResults do
    tee ""
    tee "Rejection rate: %.0f%%" (rejRate * 100.0)
    tee "%-6s %10s %10s %10s %10s" "Seed" "PF" "NetPnL" "WinRate" "Trips"
    tee "%s" (String.replicate 55 "-")
    for i in 0 .. results.Length - 1 do
        let r = results.[i]
        tee "%-6d %10.4f %10.2f %9.1f%% %10d"
            (baseSeed + i) r.ProfitFactor r.NetPnL r.WinRate r.RoundTrips

logWriter.Close()
