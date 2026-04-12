/// Standalone profiling script: runs the VWAP pipeline on all 106 stocks-in-play
/// days sequentially (no parallelism, no HTTP) to profile the processing path.
/// Run with: dotnet fsi scripts/profile_pipeline.fsx
/// Profile with: dotnet-trace collect -- dotnet fsi scripts/profile_pipeline.fsx

#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"
#r "../TradingEdge.Optimize/bin/Debug/net10.0/TradingEdge.Optimize.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.TradeBinary
open TradingEdge.Parsing.VwapSystem
open TradingEdge.Optimize.Config

// Parse stocks-in-play list
let ps1Path = "docs/generate_stocks_in_play_charts.ps1"
let entryRegex = Regex("""Ticker\s*=\s*['"]([^'"]+)['"][^}]*Date\s*=\s*['"]([^'"]+)['"]""")
let entries =
    File.ReadAllText ps1Path
    |> entryRegex.Matches
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Seq.distinct
    |> Seq.filter (fun (t, d) -> File.Exists (sprintf "data/trades_bin/%s/%s.bin" t d))
    |> Seq.toArray

printfn "Loading %d days from binary files..." entries.Length
let swLoad = Diagnostics.Stopwatch.StartNew()
let dayData =
    [| for (ticker, date) in entries do
        let path = sprintf "data/trades_bin/%s/%s.bin" ticker date
        let _, trades = loadDay path
        let op = trades |> Array.tryFind (fun tr -> tr.Session = OpeningPrint)
        let cp = trades |> Array.tryFind (fun tr -> tr.Session = ClosingPrint)
        match op, cp with
        | Some o, Some c ->
            yield {| Ticker = ticker; Date = date; Trades = trades; Window = { openTime = o.Timestamp; closeTime = c.Timestamp } |}
        | _ -> () |]
swLoad.Stop()
let totalTrades = dayData |> Array.sumBy (fun d -> int64 d.Trades.Length)
printfn "Loaded %d days (%s trades) in %.3fs\n" dayData.Length (totalTrades.ToString("N0")) swLoad.Elapsed.TotalSeconds

// Fill params
let fp =
    { Percentile = percentile
      DelayMs = delayMs
      CommissionPerShare = commissionPerShare
      RejectionRate = rejectionRate
      Rng = None }

// === Decisions-only pass ===
printfn "=== Decisions-only pass ==="
let swDec = Diagnostics.Stopwatch.StartNew()
let mutable decWins = 0.0
let mutable decLosses = 0.0
for d in dayData do
    let addTrade, getResult, _ =
        createPipeline d.Window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None None
    for tr in d.Trades do addTrade tr
    let r = getResult()
    let decs = r.Decisions
    for i in 1 .. decs.Count - 1 do
        let prev = decs.[i - 1]
        let curr = decs.[i]
        let pnl = (curr.Price - prev.Price) * float prev.Shares
        if pnl > 0.01 then decWins <- decWins + pnl
        elif pnl < -0.01 then decLosses <- decLosses + abs pnl
swDec.Stop()
let decPF = if decLosses > 0.0 then decWins / decLosses else infinity
printfn "  Time: %.3fs  PF: %.4f\n" swDec.Elapsed.TotalSeconds decPF

// === Fills pass ===
printfn "=== Fills pass ==="
let swFill = Diagnostics.Stopwatch.StartNew()
let mutable fillWins = 0.0
let mutable fillLosses = 0.0
for d in dayData do
    let addTrade, _, getFillResult =
        createPipeline d.Window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None (Some fp)
    for tr in d.Trades do addTrade tr
    let fr = getFillResult()
    let fills = fr.Fills |> Seq.toArray
    let mutable position = 0
    let mutable avgCost = 0.0
    for f in fills do
        let qty = f.Quantity
        if position = 0 then
            avgCost <- f.Price
            position <- qty
        elif sign qty = sign position then
            let totalQty = position + qty
            avgCost <- (avgCost * float (abs position) + f.Price * float (abs qty)) / float (abs totalQty)
            position <- totalQty
        else
            let closingQty = min (abs qty) (abs position)
            let pnl = float (sign position) * (f.Price - avgCost) * float closingQty
            let commission = float closingQty * commissionPerShare * 2.0
            let netPnl = pnl - commission
            if netPnl > 0.01 then fillWins <- fillWins + netPnl
            elif netPnl < -0.01 then fillLosses <- fillLosses + abs netPnl
            let remaining = position + qty
            if remaining = 0 then
                position <- 0; avgCost <- 0.0
            elif sign remaining <> sign position then
                avgCost <- f.Price; position <- remaining
            else
                position <- remaining
swFill.Stop()
let fillPF = if fillLosses > 0.0 then fillWins / fillLosses else infinity
printfn "  Time: %.3fs  PF: %.4f\n" swFill.Elapsed.TotalSeconds fillPF

printfn "Total processing time: %.3fs" (swDec.Elapsed.TotalSeconds + swFill.Elapsed.TotalSeconds)
