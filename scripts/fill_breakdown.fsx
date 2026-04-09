#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "nuget: T-Digest, 1.0.0"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/fill_breakdown.log"
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
let positionSize = 30000.0
let referenceVol = 2.25e-6
let lossLimit = positionSize * 0.085
let basePct = 0.005
let decay = 0.9
let bandVol = 0.6
let exponents = [| -14; -2; -9; -18 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
let fillParams = { Percentile = 0.1; DelayMs = 100.0; CommissionPerShare = 0.0035 }

tee "=== VWAP System Fill Breakdown ==="
tee "Bar exponents: [%s]  pcts: [%s]"
    (String.Join(";", exponents))
    (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f  referenceVol: %.4f  bandVol: %.1f  lossLimit: $%.0f" positionSize referenceVol bandVol lossLimit
tee "Fill sim: pctile=%.3f, delay=%.0fms, commission=$%.4f/share"
    fillParams.Percentile fillParams.DelayMs fillParams.CommissionPerShare
tee ""

// ----- 3. Round-trip extraction from fills -----
// A round trip starts when we go from flat to a position, and ends when we
// return to flat (or flip). Each round trip has a PnL and a side (long/short).

type RoundTrip = {
    PnL: float
    Commission: float
    Side: int // +1 long, -1 short
    EntryPrice: float
    ExitPrice: float
    Shares: int
}

let extractRoundTrips (fills: Fill array) (commissionPerShare: float) =
    let trips = ResizeArray<RoundTrip>()
    let mutable position = 0
    let mutable avgCost = 0.0
    let mutable entryCommission = 0.0

    for f in fills do
        let qty = f.Quantity
        let commission = float (abs qty) * commissionPerShare

        if position = 0 then
            // Opening new position
            avgCost <- f.Price
            position <- qty
            entryCommission <- commission
        elif sign qty = sign position then
            // Adding to position
            let totalQty = position + qty
            avgCost <- (avgCost * float (abs position) + f.Price * float (abs qty)) / float (abs totalQty)
            position <- totalQty
            entryCommission <- entryCommission + commission
        else
            // Reducing or closing or flipping
            let closingQty = min (abs qty) (abs position)
            let pnl = float (sign position) * (f.Price - avgCost) * float closingQty
            let totalCommission = entryCommission * (float closingQty / float (abs position)) + commission
            trips.Add {
                PnL = pnl - totalCommission
                Commission = totalCommission
                Side = sign position
                EntryPrice = avgCost
                ExitPrice = f.Price
                Shares = closingQty
            }
            let remaining = position + qty
            if remaining = 0 then
                position <- 0
                avgCost <- 0.0
                entryCommission <- 0.0
            elif sign remaining <> sign position then
                // Flipped
                entryCommission <- float (abs remaining) * commissionPerShare
                avgCost <- f.Price
                position <- remaining
            else
                // Partially closed — prorate entry commission
                entryCommission <- entryCommission * (1.0 - float closingQty / float (abs (position)))
                position <- remaining

    trips.ToArray()

// ----- 4. Load and run -----
tee "Loading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()

type DayResult = {
    Ticker: string
    Date: string
    DayPnL: float
    RoundTrips: RoundTrip[]
    TotalCommission: float
    NumFills: int
    AvgPositionSize: float
}

let dayResults =
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
                let window = { openTime = o.Timestamp; closeTime = c.Timestamp }
                let addTrade, getDecisionResult, getFillResult =
                    createPipeline window pcts positionSize (Some referenceVol) bandVol (Some lossLimit) None None (Some fillParams)
                for tr in trades do addTrade tr
                let fr = getFillResult()
                let dr = getDecisionResult()
                let decs = dr.Decisions
                let roundTrips = extractRoundTrips (fr.Fills |> Seq.toArray) fillParams.CommissionPerShare
                let posSizes =
                    [| for i in 0 .. decs.Count - 2 do
                        if decs.[i].Shares <> 0 then
                            float (abs decs.[i].Shares) * decs.[i].Price |]
                let avgPos = if posSizes.Length > 0 then (posSizes |> Array.sum) / float posSizes.Length else 0.0
                yield {
                    Ticker = ticker
                    Date = date
                    DayPnL = fr.RealizedPnL
                    RoundTrips = roundTrips
                    TotalCommission = fr.Commissions
                    NumFills = fr.Fills.Length
                    AvgPositionSize = avgPos
                }
            | _ -> () |]

tee "Loaded and processed %d days in %.1fs" dayResults.Length swLoad.Elapsed.TotalSeconds
tee ""

// ----- 5. Per-day breakdown -----
tee "=== Per-Day Results (sorted by P&L) ==="
tee "%-6s %-12s %10s %6s %6s %6s %10s %10s %10s %10s"
    "Ticker" "Date" "DayP&L" "trips" "W" "L" "avgWin" "avgLoss" "commiss" "avgPos$"
tee "%s" (String.replicate 100 "-")

let sortedDays = dayResults |> Array.sortByDescending (fun d -> d.DayPnL)
for d in sortedDays do
    let pnls = d.RoundTrips |> Array.map (fun rt -> rt.PnL)
    let wins = pnls |> Array.filter (fun p -> p > 0.01)
    let losses = pnls |> Array.filter (fun p -> p < -0.01)
    let avgWin = if wins.Length > 0 then wins |> Array.average else 0.0
    let avgLoss = if losses.Length > 0 then losses |> Array.average else 0.0
    tee "%-6s %-12s %10.2f %6d %6d %6d %10.2f %10.2f %10.2f %10.2f"
        d.Ticker d.Date d.DayPnL d.RoundTrips.Length wins.Length losses.Length avgWin avgLoss d.TotalCommission d.AvgPositionSize

// ----- 6. Aggregate trade-level stats -----
let allTrips = dayResults |> Array.collect (fun d -> d.RoundTrips)
let allPnLs = allTrips |> Array.map (fun rt -> rt.PnL)
let winTrips = allPnLs |> Array.filter (fun p -> p > 0.01)
let lossTrips = allPnLs |> Array.filter (fun p -> p < -0.01)
let flatTrips = allPnLs |> Array.filter (fun p -> abs p <= 0.01)

// Long/short split
let longTrips = allTrips |> Array.filter (fun rt -> rt.Side > 0)
let shortTrips = allTrips |> Array.filter (fun rt -> rt.Side < 0)
let longPnL = longTrips |> Array.sumBy (fun rt -> rt.PnL)
let shortPnL = shortTrips |> Array.sumBy (fun rt -> rt.PnL)

let grossWins = winTrips |> Array.sum
let grossLosses = lossTrips |> Array.sum |> abs
let profitFactor = if grossLosses > 0.0 then grossWins / grossLosses else infinity
let avgWin = if winTrips.Length > 0 then winTrips |> Array.average else 0.0
let avgLoss = if lossTrips.Length > 0 then lossTrips |> Array.average else 0.0
let avgTrade = if allPnLs.Length > 0 then allPnLs |> Array.average else 0.0
let winRate = if winTrips.Length + lossTrips.Length > 0 then
                  100.0 * float winTrips.Length / float (winTrips.Length + lossTrips.Length)
              else 0.0
let expectancy = if winTrips.Length + lossTrips.Length > 0 then
                     (winRate / 100.0) * avgWin + (1.0 - winRate / 100.0) * avgLoss
                 else 0.0

let maxWin = if winTrips.Length > 0 then winTrips |> Array.max else 0.0
let maxLoss = if lossTrips.Length > 0 then lossTrips |> Array.min else 0.0
let medianWin =
    if winTrips.Length > 0 then
        let s = winTrips |> Array.sort
        s.[s.Length / 2]
    else 0.0
let medianLoss =
    if lossTrips.Length > 0 then
        let s = lossTrips |> Array.sort
        s.[s.Length / 2]
    else 0.0

// Day-level stats
let winDays = dayResults |> Array.filter (fun d -> d.DayPnL > 0.01)
let lossDays = dayResults |> Array.filter (fun d -> d.DayPnL < -0.01)
let flatDays = dayResults |> Array.filter (fun d -> abs d.DayPnL <= 0.01)
let dayWinRate = if winDays.Length + lossDays.Length > 0 then
                     100.0 * float winDays.Length / float (winDays.Length + lossDays.Length)
                 else 0.0
let avgWinDay = if winDays.Length > 0 then (winDays |> Array.sumBy (fun d -> d.DayPnL)) / float winDays.Length else 0.0
let avgLossDay = if lossDays.Length > 0 then (lossDays |> Array.sumBy (fun d -> d.DayPnL)) / float lossDays.Length else 0.0
let maxDrawdownDay = if lossDays.Length > 0 then lossDays |> Array.minBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0

let totalPnL = dayResults |> Array.sumBy (fun d -> d.DayPnL)
let totalCommissions = dayResults |> Array.sumBy (fun d -> d.TotalCommission)
let totalFills = dayResults |> Array.sumBy (fun d -> d.NumFills)
let avgPosOverall = if dayResults.Length > 0 then (dayResults |> Array.sumBy (fun d -> d.AvgPositionSize)) / float dayResults.Length else 0.0

tee ""
tee "=== Aggregate Statistics ==="
tee ""
tee "--- Overall ---"
tee "Total P&L:         $%12.2f" totalPnL
tee "Total commissions: $%12.2f" totalCommissions
tee "Total days:         %12d" dayResults.Length
tee "Total round trips:  %12d" allTrips.Length
tee "Total fills:        %12d" totalFills
tee "Avg trips/day:      %12.1f" (float allTrips.Length / float dayResults.Length)
tee "Avg position size:  $%12.2f" avgPosOverall
tee ""
tee "--- Round-Trip Level ---"
tee "Win trades:         %12d" winTrips.Length
tee "Loss trades:        %12d" lossTrips.Length
tee "Flat trades:        %12d" flatTrips.Length
tee "Win rate:           %12.1f%%" winRate
tee "Avg winner:         $%12.2f" avgWin
tee "Avg loser:          $%12.2f" avgLoss
tee "Median winner:      $%12.2f" medianWin
tee "Median loser:       $%12.2f" medianLoss
tee "Largest winner:     $%12.2f" maxWin
tee "Largest loser:      $%12.2f" maxLoss
tee "Avg trade:          $%12.2f" avgTrade
tee "Expectancy:         $%12.2f" expectancy
tee "Gross wins:         $%12.2f" grossWins
tee "Gross losses:       $%12.2f" grossLosses
tee "Profit factor:      %12.2f" profitFactor
tee ""
tee "--- Day-Level ---"
tee "Winning days:       %12d" winDays.Length
tee "Losing days:        %12d" lossDays.Length
tee "Flat days:          %12d" flatDays.Length
tee "Day win rate:       %12.1f%%" dayWinRate
tee "Avg winning day:    $%12.2f" avgWinDay
tee "Avg losing day:     $%12.2f" avgLossDay
tee "Worst day:          $%12.2f" maxDrawdownDay
tee "Best day:           $%12.2f" (if winDays.Length > 0 then winDays |> Array.maxBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0)
tee ""
tee "--- Long/Short Split ---"
tee "Long trades:        %12d" longTrips.Length
tee "Long P&L:           $%12.2f" longPnL
tee "Avg long trade:     $%12.2f" (if longTrips.Length > 0 then longPnL / float longTrips.Length else 0.0)
tee "Short trades:       %12d" shortTrips.Length
tee "Short P&L:          $%12.2f" shortPnL
tee "Avg short trade:    $%12.2f" (if shortTrips.Length > 0 then shortPnL / float shortTrips.Length else 0.0)

logWriter.Close()
