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
let positionSize = 30000.0
let referenceVol = 0.0095
let lossLimit = positionSize * 0.085
let basePct = 0.005
let decay = 0.9
let exponents = [| -13; -5; -6; -6 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
let commissionPerShare = 0.0035
let delayMs = 100.0

// Percentiles to sweep: exponential interpolation over [0.01, 0.15]
let numPoints = 40
let percentiles = [| for i in 0 .. numPoints - 1 -> 0.01 * 15.0 ** (float i / float (numPoints - 1)) |]

tee "=== Fill Simulator Parameter Sweep ==="
tee "Bar exponents: [%s]  pcts: [%s]"
    (String.Join(";", exponents))
    (String.Join(";", pcts |> Array.map (fun p -> sprintf "%.5f" p)))
tee "Position size: $%.0f  referenceVol: %.4f  lossLimit: $%.0f" positionSize referenceVol lossLimit
tee "Commission: $%.4f/share  Delay: %.0fms" commissionPerShare delayMs
tee "Percentiles: [%s]" (String.Join("; ", percentiles |> Array.map (fun p -> sprintf "%.4f" p)))
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

// ----- 4. Run sweep -----
// First, run idealized (no fill sim) as baseline
let idealizedResults =
    [| for d in dayData do
        let addTrade, getResult, _ =
            createPipeline d.Window pcts positionSize (Some referenceVol) (Some lossLimit) None None None
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

// Sweep percentiles
type SweepResult = {
    Percentile: float
    TotalPnL: float
    TotalCommissions: float
    NetPnL: float
    WinDays: int
    LossDays: int
    TotalFills: int
    AvgDayPnL: float
}

tee "=== Percentile Sweep Results ==="
tee "%-12s %12s %12s %12s %8s %8s %10s %12s"
    "Pctile" "GrossPnL" "Commission" "NetPnL" "WinDays" "LossDays" "Fills" "AvgDayPnL"
tee "%s" (String.replicate 97 "-")

let sweepResults =
    [| for pctile in percentiles do
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let fp = { Percentile = pctile; DelayMs = delayMs; CommissionPerShare = commissionPerShare }
        let mutable totalPnL = 0.0
        let mutable totalCommissions = 0.0
        let mutable totalFills = 0
        let mutable winDays = 0
        let mutable lossDays = 0
        let dayPnLs = ResizeArray<float>()

        for d in dayData do
            let addTrade, _, getFillResult =
                createPipeline d.Window pcts positionSize (Some referenceVol) (Some lossLimit) None None (Some fp)
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
        tee "%-12.4f %12.2f %12.2f %12.2f %8d %8d %10d %12.2f"
            pctile totalPnL totalCommissions netPnL winDays lossDays totalFills avgDay

        yield {
            Percentile = pctile
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
tee "=== Best Percentile ==="
tee "Percentile:    %10.4f" best.Percentile
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
