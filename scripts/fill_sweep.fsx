#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TDigest.dll"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/fill_sweep.log"
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
        File.Exists (sprintf "data/trades/%s/%s.json" t d))

// ----- 2. Configuration -----
#load "config.fsx"
open Config

// Percentiles to sweep: exponential interpolation over [0.05, 0.5]
let numPoints = 40
let percentiles = [| for i in 0 .. numPoints - 1 -> 0.05 * 10.0 ** (float i / float (numPoints - 1)) |]

tee "=== Fill Simulator Percentile Sweep (Profit Factor) ==="
tee "Bar exponents: [%s]  pcts: [%s]"
    (String.Join(";", exponents))
    (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f  referenceVol: %.4f  lossLimit: $%.0f" positionSize referenceVol lossLimit
tee "Commission: $%.4f/share  Delay: %.0fms" commissionPerShare delayMs
tee "Available days: %d" availableEntries.Length
tee ""

// ----- 3. Round-trip extraction (same as fill_breakdown) -----
let extractRoundTrips (fills: Fill array) (commPerShare: float) =
    let trips = ResizeArray<float>() // just PnLs
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

// ----- 4. Preload all trade data -----
tee "Loading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()

type DayData = {
    Ticker: string
    Date: string
    Trades: Trade[]
    Window: MarketHours
}

let dayData =
    [| for (ticker, date) in availableEntries do
        let path = sprintf "data/trades/%s/%s.json" ticker date
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

// ----- 5. Sweep -----
type SweepResult = {
    Percentile: float
    NetPnL: float
    GrossWins: float
    GrossLosses: float
    ProfitFactor: float
    WinRate: float
    Expectancy: float
    WinDays: int
    LossDays: int
    RoundTrips: int
    TotalFills: int
}

tee "=== Percentile Sweep Results ==="
tee "%-12s %12s %12s %12s %10s %8s %12s %8s %8s %8s"
    "Pctile" "NetPnL" "GrossWins" "GrossLoss" "ProfFactor" "WinRate" "Expectancy" "WinDays" "LossDays" "Trips"
tee "%s" (String.replicate 115 "-")

let sweepResults =
    [| for pctile in percentiles do
        let fp = { Percentile = pctile; DelayMs = delayMs; CommissionPerShare = commissionPerShare; RejectionRate = rejectionRate; Rng = None }
        let mutable allTripPnLs = ResizeArray<float>()
        let mutable winDays = 0
        let mutable lossDays = 0
        let mutable totalFills = 0
        let mutable netPnL = 0.0

        for d in dayData do
            let addTrade, _, getFillResult =
                createPipeline d.Window pcts positionSize (Some referenceVol) 0.0 (Some lossLimit) None None (Some fp)
            for tr in d.Trades do addTrade tr
            let fr = getFillResult()
            netPnL <- netPnL + fr.RealizedPnL
            totalFills <- totalFills + fr.Fills.Length
            if fr.RealizedPnL > 0.01 then winDays <- winDays + 1
            elif fr.RealizedPnL < -0.01 then lossDays <- lossDays + 1
            let trips = extractRoundTrips (fr.Fills |> Seq.toArray) commissionPerShare
            allTripPnLs.AddRange trips

        let wins = allTripPnLs |> Seq.filter (fun p -> p > 0.01) |> Seq.sum
        let losses = allTripPnLs |> Seq.filter (fun p -> p < -0.01) |> Seq.sum |> abs
        let profitFactor = if losses > 0.0 then wins / losses else infinity
        let winCount = allTripPnLs |> Seq.filter (fun p -> p > 0.01) |> Seq.length
        let lossCount = allTripPnLs |> Seq.filter (fun p -> p < -0.01) |> Seq.length
        let winRate = if winCount + lossCount > 0 then 100.0 * float winCount / float (winCount + lossCount) else 0.0
        let avgWin = if winCount > 0 then wins / float winCount else 0.0
        let avgLoss = if lossCount > 0 then -(losses / float lossCount) else 0.0
        let expectancy = if winCount + lossCount > 0 then (winRate / 100.0) * avgWin + (1.0 - winRate / 100.0) * avgLoss else 0.0

        tee "%-12.4f %12.2f %12.2f %12.2f %10.2f %7.1f%% %12.2f %8d %8d %8d"
            pctile netPnL wins losses profitFactor winRate expectancy winDays lossDays allTripPnLs.Count

        yield {
            Percentile = pctile
            NetPnL = netPnL
            GrossWins = wins
            GrossLosses = losses
            ProfitFactor = profitFactor
            WinRate = winRate
            Expectancy = expectancy
            WinDays = winDays
            LossDays = lossDays
            RoundTrips = allTripPnLs.Count
            TotalFills = totalFills
        } |]

tee ""

// ----- 6. Best results -----
let bestPF = sweepResults |> Array.maxBy (fun r -> r.ProfitFactor)
let bestPnL = sweepResults |> Array.maxBy (fun r -> r.NetPnL)

tee "=== Best by Profit Factor ==="
tee "Percentile:    %10.4f" bestPF.Percentile
tee "Net P&L:      $%10.2f" bestPF.NetPnL
tee "Profit factor: %10.2f" bestPF.ProfitFactor
tee "Win rate:      %10.1f%%" bestPF.WinRate
tee "Expectancy:   $%10.2f" bestPF.Expectancy
tee "Round trips:   %10d" bestPF.RoundTrips
tee ""

tee "=== Best by Net P&L ==="
tee "Percentile:    %10.4f" bestPnL.Percentile
tee "Net P&L:      $%10.2f" bestPnL.NetPnL
tee "Profit factor: %10.2f" bestPnL.ProfitFactor
tee "Win rate:      %10.1f%%" bestPnL.WinRate
tee "Expectancy:   $%10.2f" bestPnL.Expectancy
tee "Round trips:   %10d" bestPnL.RoundTrips

logWriter.Close()
