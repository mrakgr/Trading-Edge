#r "../TradingEdge.Orb/bin/Release/net10.0/TradingEdge.Orb.dll"
#r "nuget: Argu, 6.2.5"
#r "nuget: FSharp.SystemTextJson, 1.4.36"

// Backtest harness for gap-up trades using either
//   (a) no entry gate (baseline), or
//   (b) the calibrated per-bucket (Tv, Ta) threshold gate.
//
// Consumes:
//   * data/gap_up_universe.json        — array of {ticker, date, gap_pct, ...}
//   * minizinc/thresholds_10s.csv — per-(bucket,precision) (Tv, Ta) for gated runs
//   * data/trades_bin/{ticker}/{date}.bin — binary trade stream; RawAvg4w /
//     TxnAvg4w / SplitFactorToday come from the header (written by
//     TradingEdge.Orb.Convert from the plays JSON).
//
// Output:
//   * minizinc/backtest_{config}.json — RoundTrip list + summary
//
// Skips (ticker, date) pairs that are missing a .bin file or whose header has
// NaN 4w metadata (new tickers with < 16 trading days of history).

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Diagnostics
open System.Globalization
open Argu
open TradingEdge.Orb
open TradingEdge.Orb.TradeLoader
open TradingEdge.Orb.TradeBinary
open TradingEdge.Orb.Pipeline

type CliArgs =
    | [<Mandatory; AltCommandLine("-c")>] Config of string
    | [<AltCommandLine("-u")>] Universe of string
    | [<AltCommandLine("-t")>] Thresholds of string
    | [<AltCommandLine("-b")>] Trades_Bin of string
    | [<AltCommandLine("-o")>] Output of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Config _ -> "Configuration name: 'baseline' or e.g. 'p80_t30'. Drives output path and gate selection."
            | Universe _ -> "Setup list JSON. Default: data/gap_up_universe.json"
            | Thresholds _ -> "Per-bucket thresholds CSV from run_minizinc_sweep (ignored for baseline). Default: minizinc/thresholds_10s.csv"
            | Trades_Bin _ -> "Binary trade root. Default: data/trades_bin"
            | Output _ -> "Output JSON. Default: minizinc/backtest_{config}.json"

let parser = ArgumentParser.Create<CliArgs>(programName = "backtest_gapup_thresholds.fsx")
let parsed =
    try parser.Parse(fsi.CommandLineArgs |> Array.skip 1, raiseOnUsage = true)
    with :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        exit 1

let configName = parsed.GetResult Config
let universePath = parsed.GetResult(Universe, defaultValue = "data/gap_up_universe.json")
let thresholdsPath = parsed.GetResult(Thresholds, defaultValue = "minizinc/thresholds_10s.csv")

// Extract the precision percentage to select from the CSV. Config names like
// "p80_t30" imply precision 80. The user can override via --precision-pct.
let precisionPct =
    let m = System.Text.RegularExpressions.Regex.Match(configName, @"^p(\d+)")
    if m.Success then Int32.Parse(m.Groups.[1].Value) else 80
let tradesBinRoot = parsed.GetResult(Trades_Bin, defaultValue = "data/trades_bin")
let outPath = parsed.GetResult(Output, defaultValue = sprintf "minizinc/backtest_%s.json" configName)

let isGated = configName <> "baseline"

// ----- Universe load -----
type Setup = {
    [<JsonPropertyName "ticker">] Ticker: string
    [<JsonPropertyName "date">]   Date: string
    [<JsonPropertyName "gap_pct">] GapPct: double
}

let setups =
    let bytes = File.ReadAllBytes universePath
    JsonSerializer.Deserialize<Setup[]>(bytes, JsonSerializerOptions())
printfn "Universe: %d rows from %s" setups.Length universePath

// ----- Threshold load -----
// CSV schema: bucket,et,precision_pct,Tv,Ta,n_fired,n_hit,precision_actual,solve_time_s,n_rows,status
// We only need bucket/precision_pct/Tv/Ta. Rows with other precisions are
// filtered out.
let bucketSeconds = 10.0
let schedule : ThresholdSchedule =
    if not isGated then
        { BucketTicks = int64 (TimeSpan.FromSeconds(bucketSeconds).Ticks)
          Thresholds = [||] }
    else
        let inv = CultureInfo.InvariantCulture
        let maxBucket = 2700
        let arr = Array.create maxBucket (struct (Double.NaN, Double.NaN))
        let mutable nMatched = 0
        let lines = File.ReadAllLines thresholdsPath
        for i = 1 to lines.Length - 1 do  // skip header
            let parts = lines.[i].Split ','
            if parts.Length >= 5 then
                let bucket = Int32.Parse(parts.[0], inv)
                let pct = Int32.Parse(parts.[2], inv)
                if pct = precisionPct && bucket >= 0 && bucket < maxBucket then
                    let tv = Double.Parse(parts.[3], inv)
                    let ta = Double.Parse(parts.[4], inv)
                    arr.[bucket] <- struct (tv, ta)
                    nMatched <- nMatched + 1
        printfn "Thresholds: %d rows (precision_pct=%d) from %s"
            nMatched precisionPct thresholdsPath
        { BucketTicks = int64 (TimeSpan.FromSeconds(bucketSeconds).Ticks)
          Thresholds = arr }

// The 4w averages used to be looked up here via DuckDB. They now live on the
// binary-file header (written by TradingEdge.Orb.Convert from the plays JSON
// at binary-conversion time). One less runtime dependency; zero per-day
// lookups. See TradeBinary.DayHeader { RawAvg4w; TxnAvg4w; SplitFactorToday }.

// ----- Fill sim constants (mirror Program.fs defaults) -----
let positionSize = 30000.0
let referenceVol = ValueSome 5.82e-4
let commissionPerShare = 0.005
let fillPercentile = 0.05
let fillDelayMs = 100.0
let fillRejectionRate = 0.30
let stopMode = StopAtRange

// ----- Per-day backtest -----
type DayOutcome = {
    Ticker: string
    Date: string
    NetPnL: float
    Commission: float
    NumTrips: int
    Trips: RoundTrip[]
    SkipReason: string    // "" if ran
}

let buildGate (header: DayHeader) : ThresholdGate voption =
    if not isGated then ValueNone
    elif Double.IsNaN header.RawAvg4w || Double.IsNaN header.TxnAvg4w then ValueNone
    else
        ValueSome {
            Schedule = schedule
            StartTicks = header.BaseTicks + int64 (TimeSpan.FromHours(Timezone.startHoursFromBase).Ticks)
            RawAvg4w = header.RawAvg4w
            TxnAvg4w = header.TxnAvg4w
        }

let runOne (s: Setup) : DayOutcome =
    let binPath = Path.Combine(tradesBinRoot, s.Ticker, sprintf "%s.bin" s.Date)
    if not (File.Exists binPath) then
        { Ticker = s.Ticker; Date = s.Date; NetPnL = 0.0; Commission = 0.0
          NumTrips = 0; Trips = [||]; SkipReason = "no_bin" }
    else
        let header, trades = loadDay { Directory = tradesBinRoot; Ticker = s.Ticker; Date = s.Date }
        if header.OpeningPrintIndex.IsNone then
            { Ticker = s.Ticker; Date = s.Date; NetPnL = 0.0; Commission = 0.0
              NumTrips = 0; Trips = [||]; SkipReason = "no_opening_print" }
        elif isGated && (Double.IsNaN header.RawAvg4w || Double.IsNaN header.TxnAvg4w) then
            { Ticker = s.Ticker; Date = s.Date; NetPnL = 0.0; Commission = 0.0
              NumTrips = 0; Trips = [||]; SkipReason = "no_4w_meta" }
        else
            let gate = buildGate header
            let seg = SegregateTrades(TimeSpan.FromSeconds bucketSeconds, DateTime header.BaseTicks)
            seg.OpeningPrintIdx <- header.OpeningPrintIndex
            let vs = OrbSystem(positionSize, referenceVol, stopMode, gate)
            let td = TrackDecisions()
            let tf = TrackFills(commissionPerShare)
            let ell = EnforceLossLimit((fun () -> tf.NetPnL), infinity)
            let fs = FillSimulator(fillPercentile, fillDelayMs, fillRejectionRate, ValueNone, DateTime header.BaseTicks)

            let onFillSink (_: Fill) = ()
            let onFill (fill: Fill) = tf.Process(onFillSink, fill)
            let onTracked (decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
                fs.Process(onFill, decision, bar, stage, trade)

            for i in 0 .. trades.Length - 1 do
                seg.Process(
                    (fun (bar, stage, trade) ->
                        vs.Process(
                            (fun (decision, bar, stage, trade) ->
                                ell.Process(
                                    (fun (decision, bar, stage, trade) ->
                                        td.Process(onTracked, decision, bar, stage, trade)),
                                    decision, bar, stage, trade)),
                            bar, stage, trade, seg.Timestamp trade)),
                    trades.[i], i)

            let trips = extractRoundTrips tf.Fills commissionPerShare |> Array.ofSeq
            { Ticker = s.Ticker; Date = s.Date
              NetPnL = tf.NetPnL
              Commission = trips |> Array.sumBy (fun t -> t.Commission)
              NumTrips = trips.Length
              Trips = trips
              SkipReason = "" }

// ----- Drive -----
printfn ""
printfn "Config: %s (gated: %b, commission: $%.3f/share)" configName isGated commissionPerShare
printfn ""

let swOverall = Stopwatch.StartNew()
let outcomes = ResizeArray<DayOutcome>(setups.Length)
let mutable lastPrint = 0L
for i in 0 .. setups.Length - 1 do
    let o = runOne setups.[i]
    outcomes.Add o
    if (i + 1) % 250 = 0 || i = setups.Length - 1 then
        let elapsed = swOverall.Elapsed.TotalSeconds
        printfn "  [%d/%d] elapsed=%.1fs" (i + 1) setups.Length elapsed
swOverall.Stop()

// ----- Aggregate -----
let ran = outcomes |> Seq.filter (fun o -> o.SkipReason = "") |> Array.ofSeq
let allTrips = ran |> Array.collect (fun o -> o.Trips)
let skipReasons =
    outcomes
    |> Seq.filter (fun o -> o.SkipReason <> "")
    |> Seq.groupBy (fun o -> o.SkipReason)
    |> Seq.map (fun (k, xs) -> k, Seq.length xs)
    |> Seq.sortBy fst
    |> Array.ofSeq

let net = allTrips |> Array.sumBy (fun t -> t.PnL)
let grossWins = allTrips |> Array.sumBy (fun t -> if t.PnL > 0.0 then t.PnL + t.Commission else 0.0)
let grossLosses = allTrips |> Array.sumBy (fun t -> if t.PnL < 0.0 then -(t.PnL + t.Commission) else 0.0)
let wins = allTrips |> Array.filter (fun t -> t.PnL > 0.0)
let losses = allTrips |> Array.filter (fun t -> t.PnL < 0.0)
let totalCommission = allTrips |> Array.sumBy (fun t -> t.Commission)
let pf = if grossLosses > 0.0 then grossWins / grossLosses else infinity
let winRate = if allTrips.Length > 0 then float wins.Length / float allTrips.Length else 0.0
let avgWin = if wins.Length > 0 then (wins |> Array.sumBy (fun t -> t.PnL)) / float wins.Length else 0.0
let avgLoss = if losses.Length > 0 then (losses |> Array.sumBy (fun t -> t.PnL)) / float losses.Length else 0.0

// Daily PnL series for Sharpe + DD
let dailyPnL =
    ran
    |> Array.groupBy (fun o -> o.Date)
    |> Array.map (fun (d, xs) -> d, xs |> Array.sumBy (fun o -> o.NetPnL))
    |> Array.sortBy fst
let meanDaily = if dailyPnL.Length > 0 then Array.averageBy snd dailyPnL else 0.0
let stdDaily =
    if dailyPnL.Length < 2 then 0.0
    else
        let m = meanDaily
        let s2 = dailyPnL |> Array.sumBy (fun (_, p) -> (p - m) ** 2.0)
        sqrt (s2 / float (dailyPnL.Length - 1))
let sharpe = if stdDaily > 0.0 then meanDaily / stdDaily * sqrt 252.0 else 0.0
let maxDD =
    let mutable peak = 0.0
    let mutable cum = 0.0
    let mutable dd = 0.0
    for _, p in dailyPnL do
        cum <- cum + p
        if cum > peak then peak <- cum
        let cur = peak - cum
        if cur > dd then dd <- cur
    dd

printfn ""
printfn "=== %s summary ===" configName
printfn "  days ran:        %d" ran.Length
for r, n in skipReasons do
    printfn "  skip:%-15s %d" r n
printfn "  round trips:     %d" allTrips.Length
printfn "  net PnL:         $%.2f" net
printfn "  commissions:     $%.2f" totalCommission
printfn "  gross wins:      $%.2f" grossWins
printfn "  gross losses:    $%.2f" grossLosses
printfn "  profit factor:   %.3f" pf
printfn "  win rate:        %.2f%%" (100.0 * winRate)
printfn "  avg win:         $%.2f" avgWin
printfn "  avg loss:        $%.2f" avgLoss
printfn "  max drawdown:    $%.2f" maxDD
printfn "  daily Sharpe:    %.2f" sharpe
printfn "  wall time:       %.1fs" swOverall.Elapsed.TotalSeconds

// ----- Write JSON -----
let inv = CultureInfo.InvariantCulture
let sb = StringBuilder()
sb.Append "{\n" |> ignore
sb.AppendFormat(inv, "  \"config\": \"{0}\",\n", configName) |> ignore
sb.AppendFormat(inv, "  \"days_ran\": {0},\n", ran.Length) |> ignore
sb.AppendFormat(inv, "  \"round_trips\": {0},\n", allTrips.Length) |> ignore
sb.AppendFormat(inv, "  \"net_pnl\": {0:F2},\n", net) |> ignore
sb.AppendFormat(inv, "  \"commission\": {0:F2},\n", totalCommission) |> ignore
sb.AppendFormat(inv, "  \"gross_wins\": {0:F2},\n", grossWins) |> ignore
sb.AppendFormat(inv, "  \"gross_losses\": {0:F2},\n", grossLosses) |> ignore
sb.AppendFormat(inv, "  \"profit_factor\": {0:F4},\n", pf) |> ignore
sb.AppendFormat(inv, "  \"win_rate\": {0:F4},\n", winRate) |> ignore
sb.AppendFormat(inv, "  \"avg_win\": {0:F2},\n", avgWin) |> ignore
sb.AppendFormat(inv, "  \"avg_loss\": {0:F2},\n", avgLoss) |> ignore
sb.AppendFormat(inv, "  \"max_drawdown\": {0:F2},\n", maxDD) |> ignore
sb.AppendFormat(inv, "  \"sharpe_daily\": {0:F4},\n", sharpe) |> ignore
sb.Append "  \"per_day\": [\n" |> ignore
for i = 0 to ran.Length - 1 do
    let o = ran.[i]
    if i > 0 then sb.Append ",\n" |> ignore
    sb.AppendFormat(inv,
        "    {{\"ticker\": \"{0}\", \"date\": \"{1}\", \"net_pnl\": {2:F2}, \"n_trips\": {3}}}",
        o.Ticker, o.Date, o.NetPnL, o.NumTrips)
    |> ignore
sb.Append "\n  ]\n}\n" |> ignore
Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
File.WriteAllText(outPath, sb.ToString())
printfn ""
printfn "Wrote %s" outPath
