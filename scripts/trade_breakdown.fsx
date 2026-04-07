#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.1.3"
#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"

open System
open System.IO
open System.Text.RegularExpressions
open TradingEdge.Parsing.TradeLoader
open TradingEdge.Parsing.VwapSystem

// ----- Tee output to log file -----
let logPath = "logs/trade_breakdown.log"
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
let referenceVol = 0.0095
let lossLimit = positionSize * 0.085
let basePct = 0.005
let decay = 0.9
let exponents = [| -13; -5; -6; -6 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))

tee "=== VWAP System Trade Breakdown ==="
tee "Bar exponents: [%s]  pcts: [%s]"
    (String.Join(";", exponents))
    (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f  referenceVol: %.4f  lossLimit: $%.0f" positionSize referenceVol lossLimit
tee ""

// ----- 3. Load and run -----
tee "Loading trade files..."
let swLoad = System.Diagnostics.Stopwatch.StartNew()

type DayResult = {
    Ticker: string
    Date: string
    DayPnL: float
    TradePnLs: (float * int)[] // (pnl, prevShares) to distinguish long/short
    AvgPositionSize: float
    NumDecisions: int
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
                let addTrade, getResult =
                    createPipeline window pcts positionSize (Some referenceVol) (Some lossLimit) None None
                for tr in trades do addTrade tr
                let r = getResult()
                let decs = r.Decisions
                let tradePnLs =
                    [| for i in 1 .. decs.Count - 1 do
                        let prev = decs.[i - 1]
                        let curr = decs.[i]
                        (curr.Price - prev.Price) * float prev.Shares, prev.Shares |]
                let posSizes =
                    [| for i in 0 .. decs.Count - 2 do
                        float (abs decs.[i].Shares) * decs.[i].Price |]
                let avgPos = if posSizes.Length > 0 then (posSizes |> Array.sum) / float posSizes.Length else 0.0
                yield {
                    Ticker = ticker
                    Date = date
                    DayPnL = r.RealizedPnL
                    TradePnLs = tradePnLs
                    AvgPositionSize = avgPos
                    NumDecisions = decs.Count
                }
            | _ -> () |]

tee "Loaded and processed %d days in %.1fs" dayResults.Length swLoad.Elapsed.TotalSeconds
tee ""

// ----- 4. Per-day breakdown -----
tee "=== Per-Day Results (sorted by P&L) ==="
tee "%-6s %-12s %10s %6s %6s %6s %10s %10s %10s"
    "Ticker" "Date" "DayP&L" "trades" "W" "L" "avgWin" "avgLoss" "avgPos$"
tee "%s" (String.replicate 90 "-")

let sortedDays = dayResults |> Array.sortByDescending (fun d -> d.DayPnL)
for d in sortedDays do
    let pnls = d.TradePnLs |> Array.map fst
    let wins = pnls |> Array.filter (fun p -> p > 0.01)
    let losses = pnls |> Array.filter (fun p -> p < -0.01)
    let avgWin = if wins.Length > 0 then wins |> Array.average else 0.0
    let avgLoss = if losses.Length > 0 then losses |> Array.average else 0.0
    tee "%-6s %-12s %10.2f %6d %6d %6d %10.2f %10.2f %10.2f"
        d.Ticker d.Date d.DayPnL pnls.Length wins.Length losses.Length avgWin avgLoss d.AvgPositionSize

// ----- 5. Aggregate trade-level stats -----
let allTradesWithSide = dayResults |> Array.collect (fun d -> d.TradePnLs)
let allTrades = allTradesWithSide |> Array.map fst
let winTrades = allTrades |> Array.filter (fun p -> p > 0.01)
let lossTrades = allTrades |> Array.filter (fun p -> p < -0.01)
let flatTrades = allTrades |> Array.filter (fun p -> abs p <= 0.01)

// Long/short split
let longTrades = allTradesWithSide |> Array.filter (fun (_, shares) -> shares > 0) |> Array.map fst
let shortTrades = allTradesWithSide |> Array.filter (fun (_, shares) -> shares < 0) |> Array.map fst
let longPnL = if longTrades.Length > 0 then longTrades |> Array.sum else 0.0
let shortPnL = if shortTrades.Length > 0 then shortTrades |> Array.sum else 0.0

let grossWins = winTrades |> Array.sum
let grossLosses = lossTrades |> Array.sum |> abs
let profitFactor = if grossLosses > 0.0 then grossWins / grossLosses else infinity
let avgWin = if winTrades.Length > 0 then winTrades |> Array.average else 0.0
let avgLoss = if lossTrades.Length > 0 then lossTrades |> Array.average else 0.0
let avgTrade = if allTrades.Length > 0 then allTrades |> Array.average else 0.0
let winRate = if winTrades.Length + lossTrades.Length > 0 then
                  100.0 * float winTrades.Length / float (winTrades.Length + lossTrades.Length)
              else 0.0
let expectancy = if winTrades.Length + lossTrades.Length > 0 then
                     (winRate / 100.0) * avgWin + (1.0 - winRate / 100.0) * avgLoss
                 else 0.0

// Largest win/loss
let maxWin = if winTrades.Length > 0 then winTrades |> Array.max else 0.0
let maxLoss = if lossTrades.Length > 0 then lossTrades |> Array.min else 0.0
let medianWin =
    if winTrades.Length > 0 then
        let s = winTrades |> Array.sort
        s.[s.Length / 2]
    else 0.0
let medianLoss =
    if lossTrades.Length > 0 then
        let s = lossTrades |> Array.sort
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

// Position sizing stats
let allPosSizes = dayResults |> Array.collect (fun d ->
    [| for i in 0 .. d.NumDecisions - 2 -> d.AvgPositionSize |])
let avgPosOverall = if dayResults.Length > 0 then (dayResults |> Array.sumBy (fun d -> d.AvgPositionSize)) / float dayResults.Length else 0.0

// Total P&L
let totalPnL = dayResults |> Array.sumBy (fun d -> d.DayPnL)

tee ""
tee "=== Aggregate Statistics ==="
tee ""
tee "--- Overall ---"
tee "Total P&L:         $%12.2f" totalPnL
tee "Total days:         %12d" dayResults.Length
tee "Total trades:       %12d" allTrades.Length
tee "Avg trades/day:     %12.1f" (float allTrades.Length / float dayResults.Length)
tee "Avg position size:  $%12.2f" avgPosOverall
tee ""
tee "--- Trade-Level ---"
tee "Win trades:         %12d" winTrades.Length
tee "Loss trades:        %12d" lossTrades.Length
tee "Flat trades:        %12d" flatTrades.Length
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
tee "Long trades:        %12d" longTrades.Length
tee "Long P&L:           $%12.2f" longPnL
tee "Avg long trade:     $%12.2f" (if longTrades.Length > 0 then longTrades |> Array.average else 0.0)
tee "Short trades:       %12d" shortTrades.Length
tee "Short P&L:          $%12.2f" shortPnL
tee "Avg short trade:    $%12.2f" (if shortTrades.Length > 0 then shortTrades |> Array.average else 0.0)
