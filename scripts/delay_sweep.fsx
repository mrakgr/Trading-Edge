#r "nuget: FSharp.SystemTextJson, 1.3.13"
#r "nuget: DuckDB.NET.Data.Full, 1.4.4"
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
        File.Exists (sprintf "data/trades/%s/%s.parquet" t d))

// ----- 2. Configuration -----
#load "config.fsx"
open Config

// Delays to sweep (ms): 0, 10, 20, ..., 500
let delays = [| for i in 0 .. 25 -> float i * 20.0 |]

tee "=== Fill Simulator Delay Sweep ==="
tee "Bar exponents: [%s]  pcts: [%s]"
    (String.Join(";", exponents))
    (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f  referenceVol: %.4f  lossLimit: $%.0f" positionSize referenceVol lossLimit
tee "Commission: $%.4f/share  Percentile: %.4f" commissionPerShare percentile
tee "Delays (ms): [%s]" (String.Join("; ", delays |> Array.map (fun d -> sprintf "%.0f" d)))
tee "Available days: %d" availableEntries.Length
tee ""

// ----- 3. Preload all trade data -----
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

// ----- 4. Run sweep -----
// First, run idealized (no fill sim) as baseline
let idealizedResults =
    [| for d in dayData do
        let addTrade, getResult, _ =
            createPipeline d.Window pcts positionSize (Some referenceVol) 0.0 (Some lossLimit) None None None
        for tr in d.Trades do addTrade tr
        let r = getResult()
        yield d.Ticker, d.Date, r.RealizedPnL |]

let idealizedTotal = idealizedResults |> Array.sumBy (fun (_, _, pnl) -> pnl)
let idealizedWinDays = idealizedResults |> Array.filter (fun (_, _, pnl) -> pnl > 0.01) |> Array.length
let idealizedLossDays = idealizedResults |> Array.filter (fun (_, _, pnl) -> pnl < -0.01) |> Array.length

tee "=== Baseline (Idealized, No Fill Sim) ==="
tee "Total P&L:    $%10.2f" idealizedTotal
tee "Win days:      %10d" idealizedWinDays
tee "Loss days:     %10d" idealizedLossDays
tee "Avg day P&L:  $%10.2f" (idealizedTotal / float dayData.Length)
tee ""

// Sweep delays
type SweepResult = {
    DelayMs: float
    TotalPnL: float
    TotalCommissions: float
    NetPnL: float
    WinDays: int
    LossDays: int
    TotalFills: int
    AvgDayPnL: float
}

tee "=== Delay Sweep Results ==="
tee "%-12s %12s %12s %12s %8s %8s %10s %12s"
    "Delay(ms)" "GrossPnL" "Commission" "NetPnL" "WinDays" "LossDays" "Fills" "AvgDayPnL"
tee "%s" (String.replicate 97 "-")

let sweepResults =
    [| for delayMs in delays do
        let fp = { Percentile = percentile; DelayMs = delayMs; CommissionPerShare = commissionPerShare; RejectionRate = rejectionRate; Rng = None }
        let mutable totalPnL = 0.0
        let mutable totalCommissions = 0.0
        let mutable totalFills = 0
        let mutable winDays = 0
        let mutable lossDays = 0
        let dayPnLs = ResizeArray<float>()

        for d in dayData do
            let addTrade, _, getFillResult =
                createPipeline d.Window pcts positionSize (Some referenceVol) 0.0 (Some lossLimit) None None (Some fp)
            for tr in d.Trades do addTrade tr
            let fr = getFillResult()
            let netPnL = fr.RealizedPnL
            totalPnL <- totalPnL + fr.RealizedPnL + fr.Commissions // gross (add back commissions)
            totalCommissions <- totalCommissions + fr.Commissions
            totalFills <- totalFills + fr.Fills.Length
            dayPnLs.Add netPnL
            if netPnL > 0.01 then winDays <- winDays + 1
            elif netPnL < -0.01 then lossDays <- lossDays + 1

        let netPnL = totalPnL - totalCommissions
        let avgDay = netPnL / float dayData.Length
        tee "%-12.0f %12.2f %12.2f %12.2f %8d %8d %10d %12.2f"
            delayMs totalPnL totalCommissions netPnL winDays lossDays totalFills avgDay

        yield {
            DelayMs = delayMs
            TotalPnL = totalPnL
            TotalCommissions = totalCommissions
            NetPnL = netPnL
            WinDays = winDays
            LossDays = lossDays
            TotalFills = totalFills
            AvgDayPnL = avgDay
        } |]

tee ""

// ----- 5. Best result summary -----
let best = sweepResults |> Array.maxBy (fun r -> r.NetPnL)
tee "=== Best Delay ==="
tee "Delay (ms):    %10.0f" best.DelayMs
tee "Gross P&L:    $%10.2f" best.TotalPnL
tee "Commissions:  $%10.2f" best.TotalCommissions
tee "Net P&L:      $%10.2f" best.NetPnL
tee "Win days:      %10d" best.WinDays
tee "Loss days:     %10d" best.LossDays
tee "Total fills:   %10d" best.TotalFills
tee "Avg day P&L:  $%10.2f" best.AvgDayPnL
tee ""
tee "Edge retained: %.1f%% of idealized P&L" (100.0 * best.NetPnL / idealizedTotal)

logWriter.Close()
