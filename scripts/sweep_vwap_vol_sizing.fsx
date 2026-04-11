#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
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
// Uses createPipeline with current best exponents and bandVol.
// Sweeps referenceVol through a range to find the value that gives
// ~$5k average position size while maintaining good profit factor.

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

// ----- 2. Preload every trade file into memory -----
tee ""
tee "Preloading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()

type DayData = {
    Ticker: string
    Date: string
    Trades: Trade[]
    Window: MarketHours
}

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
                yield {
                    Ticker = ticker
                    Date = date
                    Trades = trades
                    Window = { openTime = o.Timestamp; closeTime = c.Timestamp }
                }
            | _ ->
                tee "  skip %s/%s: missing open/close print" ticker date |]
tee "Loaded %d files in %.1fs (%d MB working set)"
    dataset.Length
    swLoad.Elapsed.TotalSeconds
    (int (GC.GetTotalMemory(false) / 1024L / 1024L))

GC.Collect()
GC.WaitForPendingFinalizers()

// ----- 3. Configuration -----
#load "config.fsx"
open Config

tee ""
tee "Bar exponents: [%s]" (String.Join(";", exponents))
tee "Bar pcts:      [%s]" (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f  bandVol: %.1f  lossLimit: $%.0f" positionSize bandVol lossLimit

// ----- 4. Evaluation function -----

/// Runs the full dataset for a given referenceVol.
/// Returns: totalPnL, profitFactor, avgPositionSize, wins, losses, totalTrades, elapsed
let evaluate (refVol: float) =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable totalPnL = 0.0
    let mutable wins = 0
    let mutable losses = 0
    let allTradePnLs = ResizeArray<float>()
    let allPositionSizes = ResizeArray<float>()
    for d in dataset do
        let addTrade, getResult, _ =
            createPipeline d.Window pcts positionSize (Some refVol) bandVol (Some lossLimit) None None None
        for tr in d.Trades do addTrade tr
        let r = getResult()
        totalPnL <- totalPnL + r.RealizedPnL
        if r.RealizedPnL > 0.01 then wins <- wins + 1
        elif r.RealizedPnL < -0.01 then losses <- losses + 1
        let decs = r.Decisions
        for i in 1 .. decs.Count - 1 do
            let prev = decs.[i - 1]
            let curr = decs.[i]
            allTradePnLs.Add((curr.Price - prev.Price) * float prev.Shares)
            if prev.Shares <> 0 then
                allPositionSizes.Add(float (abs prev.Shares) * prev.Price)
    let grossWins = allTradePnLs |> Seq.filter (fun p -> p > 0.01) |> Seq.sum
    let grossLosses = allTradePnLs |> Seq.filter (fun p -> p < -0.01) |> Seq.sum |> abs
    let profitFactor = if grossLosses > 0.0 then grossWins / grossLosses else infinity
    let avgPosSizeDollars =
        if allPositionSizes.Count > 0 then (allPositionSizes |> Seq.sum) / float allPositionSizes.Count
        else 0.0
    totalPnL, profitFactor, avgPosSizeDollars, wins, losses, allTradePnLs.Count, sw.Elapsed.TotalSeconds

// ----- 5. Sweep -----
// Logarithmic sweep: each step is 1.5x the previous (50% increments)
let refVolCandidates =
    [| for i in -10 .. 10 ->
        1.0e-3 * (1.5 ** float i) |]

tee ""
tee "%-14s %10s %10s %10s %6s %6s %6s %6s"
    "refVol" "totalP&L" "ProfFact" "avgPos$" "W" "L" "trades" "sec"
tee "%s" (String.replicate 80 "-")

let results = ResizeArray<float * float * float * float>()

for rv in refVolCandidates do
    let total, pf, avgPos, w, l, trades, elapsed = evaluate rv
    results.Add(rv, total, pf, avgPos)
    tee "%-14.2e %10.2f %10.4f %10.2f %6d %6d %6d %6.1f"
        rv total pf avgPos w l trades elapsed

// ----- 6. Summary -----
tee ""
tee "=== Best by Profit Factor ==="
let bestPF = results |> Seq.maxBy (fun (_, _, pf, _) -> pf)
let rv1, t1, pf1, a1 = bestPF
tee "  refVol=%.8f  P&L=$%.2f  PF=%.4f  avgPos=$%.2f" rv1 t1 pf1 a1

tee ""
tee "=== Closest to $5000 avg position ==="
let closest5k = results |> Seq.minBy (fun (_, _, _, avgPos) -> abs (avgPos - 5000.0))
let rv2, t2, pf2, a2 = closest5k
tee "  refVol=%.8f  P&L=$%.2f  PF=%.4f  avgPos=$%.2f" rv2 t2 pf2 a2

tee ""
tee "=== Best by total P&L ==="
let bestPnL = results |> Seq.maxBy (fun (_, t, _, _) -> t)
let rv3, t3, pf3, a3 = bestPnL
tee "  refVol=%.8f  P&L=$%.2f  PF=%.4f  avgPos=$%.2f" rv3 t3 pf3 a3

logWriter.Close()
