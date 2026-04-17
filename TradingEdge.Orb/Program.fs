module TradingEdge.Orb.Program

open System
open System.IO
open System.Diagnostics
open Argu
open TradeLoader
open TradeBinary
open Pipeline

// ============================================================================
// Benchmark harness
// ============================================================================

type SinkContext = {
    mutable BarCount: int
    mutable DecisionCount: int
    mutable FillCount: int
    mutable Sink : float
}

type DayResult = {
    Ticker: string
    Date: string
    DayPnL: float
    RoundTrips: RoundTrip[]
    TotalCommission: float
    NumFills: int
    AvgPositionSize: float
}

type DayData = { Date: string; Header: DayHeader; Ticker: string; Trades: Trade[]; AvgVolume4w: float }

/// Default time-bar bucket length in seconds. 10s gives PF ~2 on the current dataset.
let defaultBucketSeconds = 10.0

let positionSize = 30000.0
let referenceVol = ValueSome 5.82e-4
let commissionPerShare = 0.0035
let fillPercentile = 0.05
let fillDelayMs = 100.0
let fillRejectionRate = 0.30
/// Default stop mode. rangeLo matches widest-vol-stop PF (~1.70) without tuning.
let stopMode = StopAtRange

let configure (header: DayHeader) (bucketSeconds: float) fillPercentile stopMode =
    let seg = SegregateTrades(TimeSpan.FromSeconds bucketSeconds, DateTime header.BaseTicks)
    seg.OpeningPrintIdx <- header.OpeningPrintIndex
    let vs = OrbSystem(positionSize, referenceVol, stopMode)
    let td = TrackDecisions()
    let tf = TrackFills(commissionPerShare)
    let ell = EnforceLossLimit((fun () -> tf.NetPnL), infinity)
    let fs = FillSimulator(fillPercentile, fillDelayMs, fillRejectionRate, ValueNone, DateTime header.BaseTicks)
    seg, vs, td, ell, fs, tf

/// Load (ticker, date) -> avg_volume_4w from the augmented continuation plays JSON.
/// Every experiment JSON is a subset of continuation_plays, so this covers all the
/// days we might encounter.
let private loadAvgVolumes () =
    let path = "data/continuation_plays_augmented.json"
    let bytes = File.ReadAllBytes path
    use doc = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory bytes)
    let d = System.Collections.Generic.Dictionary<struct (string * string), float>()
    for el in doc.RootElement.EnumerateArray() do
        let ticker = el.GetProperty("ticker").GetString()
        let date = el.GetProperty("date").GetString()
        let avgVol = el.GetProperty("avg_volume_4w").GetDouble()
        if avgVol > 0.0 then d.[struct (ticker, date)] <- avgVol
    d

let loadDayData (jsonPath: string) =
    let entries = Convert.loadPlays jsonPath

    printfn "Loading %d days from %s ..." entries.Length jsonPath
    let swLoad = Stopwatch.StartNew()
    let avgVols = loadAvgVolumes ()
    let mutable skippedNoVol = 0
    let dayData : DayData[] =
        [| for ticker, date in entries do
            let header, trades = loadDay {Directory = "data/trades_bin"; Ticker = ticker; Date = date}
            if header.OpeningPrintIndex <> ValueNone then
                match avgVols.TryGetValue(struct (ticker, date)) with
                | true, avgVol when avgVol > 0.0 ->
                    yield { Ticker = ticker; Date = date
                            Header = header; Trades = trades; AvgVolume4w = avgVol }
                | _ ->
                    skippedNoVol <- skippedNoVol + 1 |]
    swLoad.Stop()
    let totalTrades = dayData |> Array.sumBy (fun d -> int64 d.Trades.Length)
    printfn "Loaded %d days (%s trades) in %.3fs (skipped %d for missing avg_volume_4w)\n"
        dayData.Length (totalTrades.ToString("N0")) swLoad.Elapsed.TotalSeconds skippedNoVol
    dayData, totalTrades

/// Run the full pipeline for one day with the given pcts and return NetPnL.
[<Struct>]
type DaySummary = {
    NetPnL: float
    GrossWins: float
    GrossLosses: float   // absolute value
}

let evaluateDay (d: DayData) (bucketSeconds: float) fillPercentile (stopMode: StopMode) : DaySummary =
    let seg, vs, td, ell, fs, tf = configure d.Header bucketSeconds fillPercentile stopMode
    let onFillSink (_: Fill) = ()
    let onFill (fill: Fill) = tf.Process(onFillSink, fill)
    let onTracked (decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
        fs.Process(onFill, decision, bar, stage, trade)
    for i in 0 .. d.Trades.Length - 1 do
        seg.Process(
            (fun (bar, stage, trade) ->
                vs.Process(
                    (fun (decision, bar, stage, trade) ->
                        ell.Process(
                            (fun (decision, bar, stage, trade) ->
                                td.Process(onTracked, decision, bar, stage, trade)),
                            decision, bar, stage, trade)),
                    bar, stage, trade, seg.Timestamp trade)),
            d.Trades.[i], i)
    let trips = extractRoundTrips tf.Fills commissionPerShare
    let mutable gw = 0.0
    let mutable gl = 0.0
    for rt in trips do
        if rt.PnL > 0.0 then gw <- gw + rt.PnL
        elif rt.PnL < 0.0 then gl <- gl + (-rt.PnL)
    { NetPnL = tf.NetPnL; GrossWins = gw; GrossLosses = gl }

/// Build N values log-spaced in [lo, hi].
let logSpaced (n: int) (lo: float) (hi: float) : float[] =
    if n <= 1 then [| lo |]
    else
        let logLo = log lo
        let logHi = log hi
        [| for k in 0 .. n - 1 ->
            exp (logLo + (logHi - logLo) * float k / float (n - 1)) |]

let stopModeLabel (sm: StopMode) =
    match sm with
    | StopAtVol v -> sprintf "vol=%.2f" v
    | StopAtRange -> "rangeLo"
    | StopAtRangeVolCapped v -> sprintf "capped=%.2f" v
    | StopNever -> "none"

/// Parallel sweep over time-bar bucket sizes (seconds) at fixed stop mode.
let runTimeBarSweep (dayData: DayData[]) (seconds: float[]) =
    let ns = seconds.Length
    printfn "=== Time-bar sweep: %d bucket sizes x %d days = %d tasks (cores=%d) ==="
        ns dayData.Length (ns * dayData.Length) Environment.ProcessorCount

    evaluateDay dayData.[0] seconds.[0] fillPercentile stopMode |> ignore

    let parResults = Array2D.zeroCreate<DaySummary> ns dayData.Length
    let swPar = Stopwatch.StartNew()
    for di in 0 .. dayData.Length - 1 do
        let d = dayData.[di]
        System.Threading.Tasks.Parallel.For(0, ns, fun si ->
            parResults.[si, di] <- evaluateDay d seconds.[si] fillPercentile stopMode
        ) |> ignore
    swPar.Stop()
    printfn "  Parallel:   %.3fs" swPar.Elapsed.TotalSeconds

    printfn "  Per-bucket-size results:"
    printfn "    %-4s %10s  %14s  %14s  %14s  %8s" "#" "seconds" "NetPnL" "grossWins" "grossLosses" "PF"
    for si in 0 .. ns - 1 do
        let mutable net = 0.0
        let mutable gw = 0.0
        let mutable gl = 0.0
        for di in 0 .. dayData.Length - 1 do
            let r = parResults.[si, di]
            net <- net + r.NetPnL
            gw <- gw + r.GrossWins
            gl <- gl + r.GrossLosses
        let pf = if gl > 0.0 then gw / gl else infinity
        printfn "    [%2d] %10.2f  $%13.2f  $%13.2f  $%13.2f  %8.3f"
            si seconds.[si] net gw gl pf

/// Sweep over stopMode at a fixed bar divisor.
let runParallelSweep (dayData: DayData[]) (stopModes: StopMode[]) =
    let ns = stopModes.Length
    printfn "=== Parallel sweep: %d stopMode x %d days = %d tasks (cores=%d) ==="
        ns dayData.Length (ns * dayData.Length) Environment.ProcessorCount

    evaluateDay dayData.[0] defaultBucketSeconds fillPercentile stopModes.[0] |> ignore

    let parResults = Array2D.zeroCreate<DaySummary> ns dayData.Length
    let swPar = Stopwatch.StartNew()
    for di in 0 .. dayData.Length - 1 do
        let d = dayData.[di]
        System.Threading.Tasks.Parallel.For(0, ns, fun si ->
            parResults.[si, di] <- evaluateDay d defaultBucketSeconds fillPercentile stopModes.[si]
        ) |> ignore
    swPar.Stop()
    printfn "  Parallel:   %.3fs" swPar.Elapsed.TotalSeconds

    printfn "  Per-stopMode results:"
    printfn "    %-4s %10s  %14s  %14s  %14s  %8s" "#" "stopMode" "NetPnL" "grossWins" "grossLosses" "PF"
    for si in 0 .. ns - 1 do
        let mutable net = 0.0
        let mutable gw = 0.0
        let mutable gl = 0.0
        for di in 0 .. dayData.Length - 1 do
            let r = parResults.[si, di]
            net <- net + r.NetPnL
            gw <- gw + r.GrossWins
            gl <- gl + r.GrossLosses
        let pf = if gl > 0.0 then gw / gl else infinity
        printfn "    [%2d] %10s  $%13.2f  $%13.2f  $%13.2f  %8.3f"
            si (stopModeLabel stopModes.[si]) net gw gl pf

let runBenchmark (dayData: DayData[]) (totalTrades: int64) =
    let bench(d : DayData, ctx : SinkContext) =
        let seg, vs, td, ell, fs, tf = configure d.Header defaultBucketSeconds fillPercentile stopMode
        let inline onFillSink (fill: Fill) =
            ctx.FillCount <- ctx.FillCount + 1
            ctx.Sink <- ctx.Sink + fill.Price
        let inline onFill (fill: Fill) = tf.Process(onFillSink, fill)
        let inline onTracked (decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
            match bar with
            | ValueSome b -> ctx.BarCount <- ctx.BarCount + 1; ctx.Sink <- ctx.Sink + b.Bar.VWAP
            | ValueNone -> ()
            match decision with
            | ValueSome dd -> ctx.DecisionCount <- ctx.DecisionCount + 1; ctx.Sink <- ctx.Sink + dd.Price
            | ValueNone -> ()
            fs.Process(onFill, decision, bar, stage, trade)
        for i in 0 .. d.Trades.Length - 1 do
            seg.Process(
                (fun (bar, stage, trade) ->
                    vs.Process(
                        (fun (decision, bar, stage, trade) ->
                            ell.Process(
                                (fun (decision, bar, stage, trade) ->
                                    td.Process(onTracked, decision, bar, stage, trade)),
                                decision, bar, stage, trade)),
                        bar, stage, trade, seg.Timestamp trade)),
                d.Trades.[i], i)
        ctx.Sink <- ctx.Sink + td.RealizedPnL + tf.GrossPnL - tf.Commissions

    printfn "Warming up..."
    let warmup_ctx = {BarCount = 0; DecisionCount = 0; FillCount = 0; Sink = 0.0}
    for d in dayData.[..2] do
        bench(d, warmup_ctx)

    printfn "=== OrbSystem (full pipeline) ==="
    let sw = Stopwatch.StartNew()
    let ctx = {BarCount = 0; DecisionCount = 0; FillCount = 0; Sink = 0.0}
    for d in dayData do
        bench(d, ctx)
    sw.Stop()
    printfn "  Time: %.3fs  Bars: %d  Decisions: %d  Fills: %d  Sink: %.2f  Trades/sec: %s"
        sw.Elapsed.TotalSeconds ctx.BarCount ctx.DecisionCount ctx.FillCount ctx.Sink
        ((float totalTrades / sw.Elapsed.TotalSeconds).ToString("N0"))

let runFillBreakdown (dayData: DayData[]) (bucketSeconds: float) fillPercentile (entryMode: EntryMode) (stopMode: StopMode) (direction: Direction) =
    let logPath = "logs/fill_breakdown.log"
    Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
    use logWriter = new StreamWriter(logPath, false)
    let tee fmt =
        Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

    tee "=== ORB System Fill Breakdown ==="
    tee "direction: %A  entryMode: %A  stopMode: %s" direction entryMode (stopModeLabel stopMode)
    tee "bucketSeconds: %.2f  entryDelay: %.1fs" bucketSeconds entryDelaySeconds
    tee "Position size: $%.0f  referenceVol: %.4g  lossLimit: infinity"
        positionSize (match referenceVol with ValueSome v -> v | _ -> 0.0)
    tee "Fill sim: pctile=%.3f, delay=%.0fms, commission=$%.4f/share, rejection=%.0f%%"
        fillPercentile fillDelayMs commissionPerShare (fillRejectionRate * 100.0)
    tee ""

    let dayResults =
        [| for d in dayData do
            let seg, vs, td, ell, fs, tf = configure d.Header bucketSeconds fillPercentile stopMode
            vs.EntryMode <- entryMode
            vs.Direction <- direction
            let onFillSink (_: Fill) = ()
            let onFill (fill: Fill) = tf.Process(onFillSink, fill)
            let onTracked (decision: TradingDecision voption, bar: OrbSystemBar voption, stage: TradeStage, trade: Trade) =
                fs.Process(onFill, decision, bar, stage, trade)
            for i in 0 .. d.Trades.Length - 1 do
                seg.Process(
                    (fun (bar, stage, trade) ->
                        vs.Process(
                            (fun (decision, bar, stage, trade) ->
                                ell.Process(
                                    (fun (decision, bar, stage, trade) ->
                                        td.Process(onTracked, decision, bar, stage, trade)),
                                    decision, bar, stage, trade)),
                            bar, stage, trade, seg.Timestamp trade)),
                    d.Trades.[i], i)
            let roundTrips = extractRoundTrips tf.Fills commissionPerShare
            let posSizes =
                [| for i in 0 .. td.Decisions.Count - 2 do
                    if td.Decisions.[i].Shares <> 0 then
                        float (abs td.Decisions.[i].Shares) * td.Decisions.[i].Price |]
            let avgPos = if posSizes.Length > 0 then (posSizes |> Array.sum) / float posSizes.Length else 0.0
            yield {
                Ticker = d.Ticker
                Date = d.Date
                DayPnL = tf.NetPnL
                RoundTrips = roundTrips
                TotalCommission = tf.Commissions
                NumFills = tf.Fills.Count
                AvgPositionSize = avgPos
            } |]

    tee "=== Per-Day Results (sorted by P&L) ==="
    tee "%-6s %-12s %10s %6s %6s %6s %10s %10s %10s %10s"
        "Ticker" "Date" "DayP&L" "trips" "W" "L" "avgWin" "avgLoss" "commiss" "avgPos$"
    tee "%s" (String.replicate 101 "-")
    let sortedDays = dayResults |> Array.sortByDescending (fun d -> d.DayPnL)
    for d in sortedDays do
        let pnls = d.RoundTrips |> Array.map (fun rt -> rt.PnL)
        let wins = pnls |> Array.filter (fun p -> p > 0.01)
        let losses = pnls |> Array.filter (fun p -> p < -0.01)
        let avgWin = if wins.Length > 0 then wins |> Array.average else 0.0
        let avgLoss = if losses.Length > 0 then losses |> Array.average else 0.0
        tee "%-6s %-12s %10.2f %6d %6d %6d %10.2f %10.2f %10.2f %10.2f"
            d.Ticker d.Date d.DayPnL d.RoundTrips.Length wins.Length losses.Length avgWin avgLoss d.TotalCommission d.AvgPositionSize

    let allTrips = dayResults |> Array.collect (fun d -> d.RoundTrips)
    let allPnLs = allTrips |> Array.map (fun rt -> rt.PnL)
    let winTrips = allPnLs |> Array.filter (fun p -> p > 0.01)
    let lossTrips = allPnLs |> Array.filter (fun p -> p < -0.01)
    let flatTrips = allPnLs |> Array.filter (fun p -> abs p <= 0.01)
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
    let winRate =
        if winTrips.Length + lossTrips.Length > 0 then
            100.0 * float winTrips.Length / float (winTrips.Length + lossTrips.Length)
        else 0.0
    let expectancy =
        if winTrips.Length + lossTrips.Length > 0 then
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

    let winDays = dayResults |> Array.filter (fun d -> d.DayPnL > 0.01)
    let lossDays = dayResults |> Array.filter (fun d -> d.DayPnL < -0.01)
    let flatDays = dayResults |> Array.filter (fun d -> abs d.DayPnL <= 0.01)
    let dayWinRate =
        if winDays.Length + lossDays.Length > 0 then
            100.0 * float winDays.Length / float (winDays.Length + lossDays.Length)
        else 0.0
    let avgWinDay = if winDays.Length > 0 then (winDays |> Array.sumBy (fun d -> d.DayPnL)) / float winDays.Length else 0.0
    let avgLossDay = if lossDays.Length > 0 then (lossDays |> Array.sumBy (fun d -> d.DayPnL)) / float lossDays.Length else 0.0
    let worstDay = if lossDays.Length > 0 then lossDays |> Array.minBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0
    let bestDay = if winDays.Length > 0 then winDays |> Array.maxBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0

    let totalPnL = dayResults |> Array.sumBy (fun d -> d.DayPnL)
    let totalCommissions = dayResults |> Array.sumBy (fun d -> d.TotalCommission)
    let totalFillsCount = dayResults |> Array.sumBy (fun d -> d.NumFills)
    let avgPosOverall =
        if dayResults.Length > 0 then
            (dayResults |> Array.sumBy (fun d -> d.AvgPositionSize)) / float dayResults.Length
        else 0.0

    tee ""
    tee "=== Aggregate Statistics ==="
    tee ""
    tee "--- Overall ---"
    tee "Total P&L:         $%12.2f" totalPnL
    tee "Total commissions: $%12.2f" totalCommissions
    tee "Total days:         %12d" dayResults.Length
    tee "Total round trips:  %12d" allTrips.Length
    tee "Total fills:        %12d" totalFillsCount
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
    tee "Worst day:          $%12.2f" worstDay
    tee "Best day:           $%12.2f" bestDay
    tee ""
    tee "--- Long/Short Split ---"
    tee "Long trades:        %12d" longTrips.Length
    tee "Long P&L:           $%12.2f" longPnL
    tee "Avg long trade:     $%12.2f" (if longTrips.Length > 0 then longPnL / float longTrips.Length else 0.0)
    tee "Short trades:       %12d" shortTrips.Length
    tee "Short P&L:          $%12.2f" shortPnL
    tee "Avg short trade:    $%12.2f" (if shortTrips.Length > 0 then shortPnL / float shortTrips.Length else 0.0)

// ============================================================================
// Decision-level (no fill sim) breakdown
// ============================================================================

type TradeBreakdownDay = {
    Ticker: string
    Date: string
    DayPnL: float
    TradePnLs: (float * int)[]  // (pnl, prevShares)
    AvgPositionSize: float
    NumDecisions: int
}

let runTradeBreakdown (dayData: DayData[]) (bucketSeconds: float) (entryMode: EntryMode) (stopMode: StopMode) (direction: Direction) =
    let logPath = "logs/trade_breakdown.log"
    Directory.CreateDirectory(Path.GetDirectoryName logPath) |> ignore
    use logWriter = new StreamWriter(logPath, false)
    let tee fmt =
        Printf.kprintf (fun s -> Console.WriteLine s; logWriter.WriteLine s; logWriter.Flush()) fmt

    tee "=== ORB System Trade Breakdown (decision-level, no fill sim) ==="
    tee "direction: %A  entryMode: %A  stopMode: %s" direction entryMode (stopModeLabel stopMode)
    tee "bucketSeconds: %.2f  entryDelay: %.1fs" bucketSeconds entryDelaySeconds
    tee "Position size: $%.0f  referenceVol: %.4g  lossLimit: infinity"
        positionSize (match referenceVol with ValueSome v -> v | _ -> 0.0)
    tee ""

    let dayResults =
        [| for d in dayData do
            let seg = SegregateTrades(TimeSpan.FromSeconds bucketSeconds, DateTime d.Header.BaseTicks)
            seg.OpeningPrintIdx <- d.Header.OpeningPrintIndex
            let vs = OrbSystem(positionSize, referenceVol, stopMode)
            vs.EntryMode <- entryMode
            vs.Direction <- direction
            let td = TrackDecisions()
            let ell = EnforceLossLimit((fun () -> td.RealizedPnL), infinity)
            let onTracked (_decision, _bar, _stage, _trade) = ()
            for i in 0 .. d.Trades.Length - 1 do
                seg.Process(
                    (fun (bar, stage, trade) ->
                        vs.Process(
                            (fun (decision, bar, stage, trade) ->
                                ell.Process(
                                    (fun (decision, bar, stage, trade) ->
                                        td.Process(onTracked, decision, bar, stage, trade)),
                                    decision, bar, stage, trade)),
                            bar, stage, trade, seg.Timestamp trade)),
                    d.Trades.[i], i)
            let decs = td.Decisions
            let tradePnLs =
                [| for i in 1 .. decs.Count - 1 do
                    let prev = decs.[i - 1]
                    let curr = decs.[i]
                    (curr.Price - prev.Price) * float prev.Shares, prev.Shares |]
            let posSizes =
                [| for i in 0 .. decs.Count - 2 do
                    if decs.[i].Shares <> 0 then
                        float (abs decs.[i].Shares) * decs.[i].Price |]
            let avgPos = if posSizes.Length > 0 then (posSizes |> Array.sum) / float posSizes.Length else 0.0
            yield {
                Ticker = d.Ticker
                Date = d.Date
                DayPnL = td.RealizedPnL
                TradePnLs = tradePnLs
                AvgPositionSize = avgPos
                NumDecisions = decs.Count
            } |]

    tee "=== Per-Day Results (sorted by P&L) ==="
    tee "%-6s %-12s %10s %6s %6s %6s %10s %10s %10s"
        "Ticker" "Date" "DayP&L" "trades" "W" "L" "avgWin" "avgLoss" "avgPos$"
    tee "%s" (String.replicate 91 "-")
    let sortedDays = dayResults |> Array.sortByDescending (fun d -> d.DayPnL)
    for d in sortedDays do
        let pnls = d.TradePnLs |> Array.map fst
        let wins = pnls |> Array.filter (fun p -> p > 0.01)
        let losses = pnls |> Array.filter (fun p -> p < -0.01)
        let avgWin = if wins.Length > 0 then wins |> Array.average else 0.0
        let avgLoss = if losses.Length > 0 then losses |> Array.average else 0.0
        tee "%-6s %-12s %10.2f %6d %6d %6d %10.2f %10.2f %10.2f"
            d.Ticker d.Date d.DayPnL pnls.Length wins.Length losses.Length avgWin avgLoss d.AvgPositionSize

    let allTradesWithSide = dayResults |> Array.collect (fun d -> d.TradePnLs)
    let allTrades = allTradesWithSide |> Array.map fst
    let winTrades = allTrades |> Array.filter (fun p -> p > 0.01)
    let lossTrades = allTrades |> Array.filter (fun p -> p < -0.01)
    let flatTrades = allTrades |> Array.filter (fun p -> abs p <= 0.01)
    let longTrades = allTradesWithSide |> Array.filter (fun (_, s) -> s > 0) |> Array.map fst
    let shortTrades = allTradesWithSide |> Array.filter (fun (_, s) -> s < 0) |> Array.map fst
    let longPnL = if longTrades.Length > 0 then longTrades |> Array.sum else 0.0
    let shortPnL = if shortTrades.Length > 0 then shortTrades |> Array.sum else 0.0
    let grossWins = winTrades |> Array.sum
    let grossLosses = lossTrades |> Array.sum |> abs
    let profitFactor = if grossLosses > 0.0 then grossWins / grossLosses else infinity
    let avgWin = if winTrades.Length > 0 then winTrades |> Array.average else 0.0
    let avgLoss = if lossTrades.Length > 0 then lossTrades |> Array.average else 0.0
    let avgTrade = if allTrades.Length > 0 then allTrades |> Array.average else 0.0
    let winRate =
        if winTrades.Length + lossTrades.Length > 0 then
            100.0 * float winTrades.Length / float (winTrades.Length + lossTrades.Length)
        else 0.0
    let expectancy =
        if winTrades.Length + lossTrades.Length > 0 then
            (winRate / 100.0) * avgWin + (1.0 - winRate / 100.0) * avgLoss
        else 0.0
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

    let winDays = dayResults |> Array.filter (fun d -> d.DayPnL > 0.01)
    let lossDays = dayResults |> Array.filter (fun d -> d.DayPnL < -0.01)
    let flatDays = dayResults |> Array.filter (fun d -> abs d.DayPnL <= 0.01)
    let dayWinRate =
        if winDays.Length + lossDays.Length > 0 then
            100.0 * float winDays.Length / float (winDays.Length + lossDays.Length)
        else 0.0
    let avgWinDay = if winDays.Length > 0 then (winDays |> Array.sumBy (fun d -> d.DayPnL)) / float winDays.Length else 0.0
    let avgLossDay = if lossDays.Length > 0 then (lossDays |> Array.sumBy (fun d -> d.DayPnL)) / float lossDays.Length else 0.0
    let worstDay = if lossDays.Length > 0 then lossDays |> Array.minBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0
    let bestDay = if winDays.Length > 0 then winDays |> Array.maxBy (fun d -> d.DayPnL) |> fun d -> d.DayPnL else 0.0

    let totalPnL = dayResults |> Array.sumBy (fun d -> d.DayPnL)
    let avgPosOverall =
        if dayResults.Length > 0 then
            (dayResults |> Array.sumBy (fun d -> d.AvgPositionSize)) / float dayResults.Length
        else 0.0

    tee ""
    tee "=== Aggregate Statistics ==="
    tee ""
    tee "--- Overall ---"
    tee "Total P&L:         $%12.2f" totalPnL
    tee "Total days:         %12d" dayResults.Length
    tee "Total trades:       %12d" allTrades.Length
    tee "Avg trades/day:     %12.1f" (float allTrades.Length / float (max 1 dayResults.Length))
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
    tee "Worst day:          $%12.2f" worstDay
    tee "Best day:           $%12.2f" bestDay
    tee ""
    tee "--- Long/Short Split ---"
    tee "Long trades:        %12d" longTrades.Length
    tee "Long P&L:           $%12.2f" longPnL
    tee "Avg long trade:     $%12.2f" (if longTrades.Length > 0 then longPnL / float longTrades.Length else 0.0)
    tee "Short trades:       %12d" shortTrades.Length
    tee "Short P&L:          $%12.2f" shortPnL
    tee "Avg short trade:    $%12.2f" (if shortTrades.Length > 0 then shortPnL / float shortTrades.Length else 0.0)

// ============================================================================
// CLI
// ============================================================================

type ConvertArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-t")>] Trades_Dir of string
    | [<AltCommandLine("-o")>] Output of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input plays JSON (e.g. data/continuation_plays.json)"
            | Trades_Dir _ -> "Parquet root (default: data/trades)"
            | Output _ -> "Output binary directory (default: data/trades_bin)"

type BreakdownArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-s")>] Seconds of float
    | [<AltCommandLine("-p")>] Percentile of float
    | Buy_At_Open
    | Short

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input JSON with [{ticker, date}] entries (e.g. data/breakdown_2k.json)"
            | Seconds _ -> sprintf "Time-bar bucket length in seconds (default: %.1f)" defaultBucketSeconds
            | Percentile _ -> "The target percentile to buy down in a bar (e.g. 0.05)"
            | Buy_At_Open -> "Baseline: enter on first AfterOpeningPrint bar with StopNever (measures dataset directional bias)"
            | Short -> "Short-side mirror: enter on range-low break, stop at range-high"

type SweepArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    | [<AltCommandLine("-n")>] Stop_Steps of int
    | [<AltCommandLine("-l")>] Stop_Lo of float
    | [<AltCommandLine("-u")>] Stop_Hi of float
    | Time_Sweep_Lo of float
    | Time_Sweep_Hi of float
    | Time_Sweep_Step of float
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input JSON with [{ticker, date}] entries"
            | Stop_Steps _ -> "Number of log-spaced stopVol steps (default: 5)"
            | Stop_Lo _ -> "Lower stopVol in vol units (default: 1.0)"
            | Stop_Hi _ -> "Upper stopVol in vol units (default: 10.0)"
            | Time_Sweep_Lo _ -> "If set along with --time-sweep-hi, runs a time-bar sweep (seconds) instead of the stop sweep."
            | Time_Sweep_Hi _ -> "Upper time-bucket in seconds."
            | Time_Sweep_Step _ -> "Step in seconds (default: 1.0)."

type BenchmarkArgs =
    | [<Mandatory; AltCommandLine("-i")>] Input of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Input _ -> "Input JSON with [{ticker, date}] entries"

type Command =
    | [<CliPrefix(CliPrefix.None)>] Convert of ParseResults<ConvertArgs>
    | [<CliPrefix(CliPrefix.None)>] Breakdown of ParseResults<BreakdownArgs>
    | [<CliPrefix(CliPrefix.None)>] Trade_Breakdown of ParseResults<BreakdownArgs>
    | [<CliPrefix(CliPrefix.None)>] Sweep of ParseResults<SweepArgs>
    | [<CliPrefix(CliPrefix.None)>] Benchmark of ParseResults<BenchmarkArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Convert _ -> "Convert parquet trade files to the v2 binary format"
            | Breakdown _ -> "Run the full pipeline with fills and print fill-level breakdown"
            | Trade_Breakdown _ -> "Run decisions-only (no fill sim) and print decision-level breakdown"
            | Sweep _ -> "Run a parallel parameter sweep"
            | Benchmark _ -> "Benchmark the pipeline (throughput)"

let runConvert (args: ParseResults<ConvertArgs>) =
    let input = args.GetResult ConvertArgs.Input
    let tradesDir = args.GetResult(ConvertArgs.Trades_Dir, "data/trades")
    let outDir = args.GetResult(ConvertArgs.Output, "data/trades_bin")
    Convert.convertPlays input tradesDir outDir

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Command>(programName = "TradingEdge.Orb")
    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)
        match results.GetSubCommand() with
        | Convert args -> runConvert args
        | Breakdown args ->
            let input = args.GetResult <@ BreakdownArgs.Input @>
            let percentile = args.TryGetResult <@ BreakdownArgs.Percentile @> |> Option.map float |> Option.defaultValue fillPercentile
            let entryMode, stopMode =
                if args.Contains <@ BreakdownArgs.Buy_At_Open @> then BuyAtOpen, StopNever
                else RangeBreakout, stopMode
            let direction = if args.Contains <@ BreakdownArgs.Short @> then Direction.Short else Direction.Long
            let bucketSeconds = args.TryGetResult <@ BreakdownArgs.Seconds @> |> Option.defaultValue defaultBucketSeconds
            let dayData, _ = loadDayData input
            runFillBreakdown dayData bucketSeconds percentile entryMode stopMode direction
        | Trade_Breakdown args ->
            let input = args.GetResult <@ BreakdownArgs.Input @>
            let entryMode, stopMode =
                if args.Contains <@ BreakdownArgs.Buy_At_Open @> then BuyAtOpen, StopNever
                else RangeBreakout, stopMode
            let direction = if args.Contains <@ BreakdownArgs.Short @> then Direction.Short else Direction.Long
            let bucketSeconds = args.TryGetResult <@ BreakdownArgs.Seconds @> |> Option.defaultValue defaultBucketSeconds
            let dayData, _ = loadDayData input
            runTradeBreakdown dayData bucketSeconds entryMode stopMode direction
        | Sweep args ->
            let input = args.GetResult <@ SweepArgs.Input @>
            let dayData, _ = loadDayData input
            match args.TryGetResult <@ SweepArgs.Time_Sweep_Lo @>,
                  args.TryGetResult <@ SweepArgs.Time_Sweep_Hi @> with
            | Some lo, Some hi ->
                let step = args.GetResult(<@ SweepArgs.Time_Sweep_Step @>, 1.0)
                let seconds =
                    [| let mutable s = lo
                       while s <= hi + 1e-9 do
                           yield s
                           s <- s + step |]
                runTimeBarSweep dayData seconds
            | _ ->
                let sn = args.GetResult(<@ SweepArgs.Stop_Steps @>, 5)
                let slo = args.GetResult(<@ SweepArgs.Stop_Lo @>, 1.0)
                let shi = args.GetResult(<@ SweepArgs.Stop_Hi @>, 10.0)
                // Build stopMode list: [rangeLo baseline; vol-stop steps...; capped-range steps...].
                let volModes = logSpaced sn slo shi |> Array.map StopAtVol
                let cappedModes = logSpaced sn slo shi |> Array.map StopAtRangeVolCapped
                let stopModes = Array.concat [| [| StopAtRange |]; volModes; cappedModes |]
                runParallelSweep dayData stopModes
        | Benchmark args ->
            let input = args.GetResult <@ BenchmarkArgs.Input @>
            let dayData, totalTrades = loadDayData input
            runBenchmark dayData totalTrades
        0
    with
    | :? ArguParseException as e ->
        eprintfn "%s" e.Message
        1
