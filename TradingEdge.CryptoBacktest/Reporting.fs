module TradingEdge.CryptoBacktest.Reporting

open System
open System.IO
open System.Globalization
open TradingEdge.CryptoBacktest.OrderflowMA
open TradingEdge.CryptoBacktest.Backtest

let private inv = CultureInfo.InvariantCulture

let private fmt (x: float) : string =
    if Double.IsNaN x then "nan"
    elif Double.IsPositiveInfinity x then "inf"
    elif Double.IsNegativeInfinity x then "-inf"
    else x.ToString("R", inv)

let private writeAtomic (path: string) (lines: seq<string>) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let tmp = path + ".tmp"
    File.WriteAllLines(tmp, lines)
    if File.Exists path then File.Delete path
    File.Move(tmp, path)

let resultsHeader =
    "symbol,timeframe,ma_hours,allow_short,bars_total,trades,wins,win_rate,profit_factor,net_pnl,gross_wins,gross_losses,sharpe,max_drawdown,total_return_pct,long_trades,long_wins,long_net_pnl,long_profit_factor,short_trades,short_wins,short_net_pnl,short_profit_factor,start_us,end_us"

let private resultsRow (m: Metrics) : string =
    String.concat "," [
        m.Symbol
        m.Timeframe
        string m.MaWindowHours
        (if m.AllowShort then "1" else "0")
        string m.BarsTotal
        string m.Trades
        string m.Wins
        fmt m.WinRate
        fmt m.ProfitFactor
        fmt m.NetPnL
        fmt m.GrossWins
        fmt m.GrossLosses
        fmt m.Sharpe
        fmt m.MaxDrawdown
        fmt m.TotalReturnPct
        string m.LongTrades
        string m.LongWins
        fmt m.LongNetPnL
        fmt m.LongProfitFactor
        string m.ShortTrades
        string m.ShortWins
        fmt m.ShortNetPnL
        fmt m.ShortProfitFactor
        string m.StartUs
        string m.EndUs
    ]

let writeResults (path: string) (rows: Metrics[]) =
    let lines = seq {
        yield resultsHeader
        for r in rows -> resultsRow r
    }
    writeAtomic path lines

let appendResults (path: string) (rows: Metrics[]) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let exists = File.Exists path
    use sw = new StreamWriter(path, append = true)
    if not exists then sw.WriteLine resultsHeader
    for r in rows do sw.WriteLine(resultsRow r)

let private median (xs: float[]) =
    if xs.Length = 0 then 0.0
    else
        let s = Array.sortBy id xs
        let n = s.Length
        if n % 2 = 1 then s.[n / 2]
        else 0.5 * (s.[n / 2 - 1] + s.[n / 2])

let private mean (xs: float[]) =
    if xs.Length = 0 then 0.0
    else (Array.sum xs) / float xs.Length

/// Aggregate per-(timeframe, ma_hours) across symbols. Reported separately
/// per AllowShort mode so the long-only and long/short cells don't get
/// pooled into one summary row.
type SummaryRow = {
    Timeframe: string
    MaWindowHours: int
    AllowShort: bool
    Symbols: int
    MedianSharpe: float
    MeanSharpe: float
    PctProfitable: float       // % of symbols with NetPnL > 0
    PctProfitFactorGT1: float  // % of symbols with PF > 1
    MedianTotalReturnPct: float
    MeanTotalReturnPct: float
}

let summarize (rows: Metrics[]) : SummaryRow[] =
    rows
    |> Array.groupBy (fun m -> m.Timeframe, m.MaWindowHours, m.AllowShort)
    |> Array.map (fun ((tf, ma, sh), grp) ->
        let validGrp = grp |> Array.filter (fun m -> m.Trades > 0)
        let sharpes = validGrp |> Array.map (fun m -> m.Sharpe)
        let returns = validGrp |> Array.map (fun m -> m.TotalReturnPct)
        let nProf = validGrp |> Array.filter (fun m -> m.NetPnL > 0.0) |> Array.length
        let nPF = validGrp |> Array.filter (fun m -> m.ProfitFactor > 1.0) |> Array.length
        let denom = max 1 validGrp.Length
        {
            Timeframe = tf
            MaWindowHours = ma
            AllowShort = sh
            Symbols = validGrp.Length
            MedianSharpe = median sharpes
            MeanSharpe = mean sharpes
            PctProfitable = float nProf / float denom
            PctProfitFactorGT1 = float nPF / float denom
            MedianTotalReturnPct = median returns
            MeanTotalReturnPct = mean returns
        })

let summaryHeader =
    "timeframe,ma_hours,allow_short,symbols,median_sharpe,mean_sharpe,pct_profitable,pct_profit_factor_gt1,median_total_return_pct,mean_total_return_pct"

let writeSummary (path: string) (rows: SummaryRow[]) =
    let lines = seq {
        yield summaryHeader
        for r in rows ->
            String.concat "," [
                r.Timeframe
                string r.MaWindowHours
                (if r.AllowShort then "1" else "0")
                string r.Symbols
                fmt r.MedianSharpe
                fmt r.MeanSharpe
                fmt r.PctProfitable
                fmt r.PctProfitFactorGT1
                fmt r.MedianTotalReturnPct
                fmt r.MeanTotalReturnPct
            ]
    }
    writeAtomic path lines

let tripsHeader =
    "symbol,timeframe,ma_hours,allow_short,entry_us,exit_us,side,entry_price,exit_price,net_pnl,fees,bars_held,mfe,mae,ratio_at_entry,effective_notional,funding_pnl,adv_at_entry"

let private sideStr =
    function
    | Flat -> "flat"
    | Long -> "long"
    | Short -> "short"

let private tripRow (symbol: string) (timeframe: string) (cfg: StrategyConfig) (t: RoundTrip) : string =
    String.concat "," [
        symbol
        timeframe
        string cfg.MaWindowHours
        (if cfg.AllowShort then "1" else "0")
        string t.EntryUs
        string t.ExitUs
        sideStr t.Side
        fmt t.EntryPrice
        fmt t.ExitPrice
        fmt t.NetPnL
        fmt t.Fees
        string t.BarsHeld
        fmt t.MaxFavorableExcursion
        fmt t.MaxAdverseExcursion
        fmt t.RatioAtEntry
        fmt t.EffectiveNotional
        fmt t.FundingPnL
        fmt t.AvgDailyVolumeAtEntry
    ]

let writeTrips (path: string) (symbol: string) (timeframe: string) (cfg: StrategyConfig) (trips: RoundTrip[]) =
    let lines = seq {
        yield tripsHeader
        for t in trips ->
            tripRow symbol timeframe cfg t
    }
    writeAtomic path lines

/// Append a batch of trips for one (symbol, timeframe, ma) cell to a single
/// shared CSV. Header is written on first append; subsequent appends just add
/// rows. Used by the sweep to build a per-cell-config trips file containing
/// every symbol's round-trips for that (timeframe, ma) combination.
let appendTrips
    (path: string)
    (symbol: string)
    (timeframe: string)
    (cfg: StrategyConfig)
    (trips: RoundTrip[])
    : unit =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let exists = File.Exists path
    use sw = new StreamWriter(path, append = true)
    if not exists then sw.WriteLine tripsHeader
    for t in trips do sw.WriteLine(tripRow symbol timeframe cfg t)

// =============================================================================
// Orb-style breakdown report
// =============================================================================
//
// Three sections, mirroring TradingEdge.Orb's breakdown:
//   1. Per-cell table — one row per (symbol) inside a (timeframe, ma_hours)
//      group. Sorted by net P&L. Top + bottom rows printed to console; full
//      table written to CSV.
//   2. Aggregate stats over pooled round-trips across all symbols in that
//      cell-config. Trade-level metrics (PF, expectancy, percentiles) plus
//      cell-level metrics (Sharpe / PF distribution across symbols).
//   3. Long/short split — pooled long P&L vs short P&L.
//
// One report per (timeframe, ma_hours, allow_short) group.

let private percentile (xs: float[]) (p: float) =
    if xs.Length = 0 then 0.0
    else
        let s = Array.sortBy id xs
        let i = int (p * float (s.Length - 1) + 0.5)
        s.[max 0 (min (s.Length - 1) i)]

/// Print a per-cell breakdown for one (timeframe, ma_hours, allow_short)
/// group. Trips are pooled across symbols. The metrics array gives per-symbol
/// stats so we can stratify and emit top/bottom symbols by net P&L.
let printGroupBreakdown
    (logWrite: string -> unit)
    (consoleWrite: string -> unit)
    (timeframe: string)
    (maWindowHours: int)
    (allowShort: bool)
    (notional: float)
    (cellMetrics: Metrics[])
    (allTrips: RoundTrip[])
    (advBySymbol: System.Collections.Generic.IDictionary<string, float>)
    : unit =
    let pln (s: string) =
        consoleWrite s
        logWrite s
    let plnf fmt = Printf.kprintf pln fmt

    plnf ""
    plnf "=== Breakdown: timeframe=%s ma=%dh short=%b ===" timeframe maWindowHours allowShort
    plnf ""

    // ----- Per-cell table (top/bottom by net P&L) -----
    let sortedCells = cellMetrics |> Array.sortByDescending (fun m -> m.NetPnL)
    let topN = 20
    let nCells = sortedCells.Length

    plnf "--- Per-Symbol Results (sorted by net P&L) ---"
    plnf "%-20s %8s %6s %6s %7s %9s %8s %10s %12s"
        "symbol" "trades" "wins" "loss" "winRate" "PF" "Sharpe" "totalRet%" "netPnL"
    let sep = String.replicate 100 "-"
    plnf "%s" sep
    let printRow (m: Metrics) =
        let wins = m.Wins
        let losses = m.Trades - m.Wins
        let pfStr =
            if Double.IsPositiveInfinity m.ProfitFactor then "    inf"
            else sprintf "%9.3f" m.ProfitFactor
        plnf "%-20s %8d %6d %6d %6.1f%% %s %8.3f %9.2f%% %12.2f"
            m.Symbol m.Trades wins losses (m.WinRate * 100.0)
            pfStr m.Sharpe (m.TotalReturnPct * 100.0) m.NetPnL
    let printRange (a: int) (b: int) =
        for i in a .. b - 1 do printRow sortedCells.[i]
    if nCells <= topN * 2 then
        printRange 0 nCells
    else
        printRange 0 topN
        plnf "  ... %d more rows ..." (nCells - 2 * topN)
        printRange (nCells - topN) nCells

    plnf ""

    // ----- Aggregate trade-level stats -----
    let pnls = allTrips |> Array.map (fun t -> t.NetPnL)
    let nTrips = allTrips.Length
    let wins = pnls |> Array.filter (fun p -> p > 0.0)
    let losses = pnls |> Array.filter (fun p -> p < 0.0)
    let flats = pnls |> Array.filter (fun p -> p = 0.0)
    let grossWins = Array.sum wins
    let grossLosses = (Array.sum losses) |> abs
    let pf =
        if grossLosses > 0.0 then grossWins / grossLosses
        elif grossWins > 0.0 then infinity
        else 0.0
    let avgWin = if wins.Length > 0 then Array.average wins else 0.0
    let avgLoss = if losses.Length > 0 then Array.average losses else 0.0
    let medWin = if wins.Length > 0 then percentile wins 0.5 else 0.0
    let medLoss = if losses.Length > 0 then percentile losses 0.5 else 0.0
    let maxWin = if wins.Length > 0 then Array.max wins else 0.0
    let maxLoss = if losses.Length > 0 then Array.min losses else 0.0
    let avgTrade = if pnls.Length > 0 then Array.average pnls else 0.0
    let winRate =
        let denom = wins.Length + losses.Length
        if denom > 0 then float wins.Length / float denom else 0.0
    let expectancy =
        if wins.Length + losses.Length > 0 then
            winRate * avgWin + (1.0 - winRate) * avgLoss
        else 0.0

    plnf "--- Aggregate Trade-Level Stats (pooled across symbols) ---"
    plnf "  Symbols (cells):    %d" nCells
    plnf "  Total round trips:  %d" nTrips
    plnf "  Win trades:         %d" wins.Length
    plnf "  Loss trades:        %d" losses.Length
    plnf "  Flat trades:        %d" flats.Length
    plnf "  Win rate:           %.2f%%" (winRate * 100.0)
    plnf "  Avg winner:         $%.2f" avgWin
    plnf "  Avg loser:          $%.2f" avgLoss
    plnf "  Median winner:      $%.2f" medWin
    plnf "  Median loser:       $%.2f" medLoss
    plnf "  Largest winner:     $%.2f" maxWin
    plnf "  Largest loser:      $%.2f" maxLoss
    plnf "  Avg trade:          $%.4f" avgTrade
    plnf "  Expectancy:         $%.4f" expectancy
    plnf "  Gross wins:         $%.2f" grossWins
    plnf "  Gross losses:       $%.2f" grossLosses
    plnf "  Profit factor:      %s" (if Double.IsPositiveInfinity pf then "inf" else sprintf "%.3f" pf)
    plnf ""

    // ----- Cell-level distribution stats (Sharpe, PF, total-return) -----
    let cellSharpes = cellMetrics |> Array.map (fun m -> m.Sharpe)
    let cellPFs =
        cellMetrics
        |> Array.choose (fun m ->
            if Double.IsPositiveInfinity m.ProfitFactor || m.Trades = 0 then None
            else Some m.ProfitFactor)
    let cellReturns = cellMetrics |> Array.map (fun m -> m.TotalReturnPct * 100.0)
    let nProfitable = cellMetrics |> Array.filter (fun m -> m.NetPnL > 0.0) |> Array.length
    let nPFGT1 = cellMetrics |> Array.filter (fun m -> m.ProfitFactor > 1.0) |> Array.length

    let pctRow label (xs: float[]) =
        plnf "  %-18s p5=%.3f  p25=%.3f  med=%.3f  p75=%.3f  p95=%.3f  mean=%.3f"
            label
            (percentile xs 0.05) (percentile xs 0.25) (percentile xs 0.5)
            (percentile xs 0.75) (percentile xs 0.95)
            (if xs.Length > 0 then Array.average xs else 0.0)

    plnf "--- Cell-Level Distribution (per symbol) ---"
    plnf "  Profitable cells:   %d / %d (%.1f%%)" nProfitable nCells (100.0 * float nProfitable / float (max 1 nCells))
    plnf "  Cells PF > 1:       %d / %d (%.1f%%)" nPFGT1 nCells (100.0 * float nPFGT1 / float (max 1 nCells))
    pctRow "Sharpe:" cellSharpes
    pctRow "Profit factor:" cellPFs
    pctRow "Total return %:" cellReturns
    plnf ""

    // ----- Long/short split (pooled trips) -----
    let longTrips = allTrips |> Array.filter (fun t -> t.Side = Long)
    let shortTrips = allTrips |> Array.filter (fun t -> t.Side = Short)
    let longPnL = longTrips |> Array.sumBy (fun t -> t.NetPnL)
    let shortPnL = shortTrips |> Array.sumBy (fun t -> t.NetPnL)
    let avgLong = if longTrips.Length > 0 then longPnL / float longTrips.Length else 0.0
    let avgShort = if shortTrips.Length > 0 then shortPnL / float shortTrips.Length else 0.0
    let longWins = longTrips |> Array.filter (fun t -> t.NetPnL > 0.0) |> Array.length
    let shortWins = shortTrips |> Array.filter (fun t -> t.NetPnL > 0.0) |> Array.length
    let pfFromTrips (tps: RoundTrip[]) =
        let gw = tps |> Array.sumBy (fun t -> if t.NetPnL > 0.0 then t.NetPnL else 0.0)
        let gl = tps |> Array.sumBy (fun t -> if t.NetPnL < 0.0 then -t.NetPnL else 0.0)
        if gl > 0.0 then gw / gl
        elif gw > 0.0 then infinity
        else 0.0
    let longPF = pfFromTrips longTrips
    let shortPF = pfFromTrips shortTrips

    plnf "--- Long/Short Split ---"
    plnf "  Long trades:        %d (wins %d, %.1f%% win rate)"
        longTrips.Length longWins
        (if longTrips.Length > 0 then 100.0 * float longWins / float longTrips.Length else 0.0)
    plnf "  Long net P&L:       $%.2f" longPnL
    plnf "  Long avg trade:     $%.4f" avgLong
    plnf "  Long profit factor: %s" (if Double.IsPositiveInfinity longPF then "inf" else sprintf "%.3f" longPF)
    plnf "  Short trades:       %d (wins %d, %.1f%% win rate)"
        shortTrips.Length shortWins
        (if shortTrips.Length > 0 then 100.0 * float shortWins / float shortTrips.Length else 0.0)
    plnf "  Short net P&L:      $%.2f" shortPnL
    plnf "  Short avg trade:    $%.4f" avgShort
    plnf "  Short profit factor: %s" (if Double.IsPositiveInfinity shortPF then "inf" else sprintf "%.3f" shortPF)
    plnf ""

    // ----- Excursion (MFE / MAE) and holding-period analysis -----
    //
    // MAE distribution on the SHORT side is the squeeze metric: a tight
    // distribution (worst case ~ -5% to -10% of notional) means the system
    // is closing shorts before they get squeezed; a fat tail (worst case
    // -50%+) means many shorts sit through painful rebounds.
    let pnlPct (t: RoundTrip) =
        // Express MFE/MAE as % of the trip's effective notional. With
        // vol-based sizing, EffectiveNotional varies per trade — falling
        // back to cfg.Notional when sizing is disabled (or when an older
        // RoundTrip without the field is encountered, which is = 0).
        let denom =
            if t.EffectiveNotional > 0.0 then t.EffectiveNotional else notional
        100.0 * t.MaxFavorableExcursion / denom,
        100.0 * t.MaxAdverseExcursion / denom

    let excursionStats label (tps: RoundTrip[]) =
        if tps.Length = 0 then
            plnf "  %-18s (no trades)" label
        else
            let mfes = tps |> Array.map (fun t -> fst (pnlPct t))
            let maes = tps |> Array.map (fun t -> snd (pnlPct t))
            let bars = tps |> Array.map (fun t -> float t.BarsHeld)
            let ratios = tps |> Array.map (fun t -> t.RatioAtEntry)
            plnf "  %s" label
            plnf "    MFE %% (favorable): p5=%.2f  p25=%.2f  med=%.2f  p75=%.2f  p95=%.2f  mean=%.2f"
                (percentile mfes 0.05) (percentile mfes 0.25) (percentile mfes 0.5)
                (percentile mfes 0.75) (percentile mfes 0.95) (Array.average mfes)
            plnf "    MAE %% (adverse):   p5=%.2f  p25=%.2f  med=%.2f  p75=%.2f  p95=%.2f  mean=%.2f"
                (percentile maes 0.05) (percentile maes 0.25) (percentile maes 0.5)
                (percentile maes 0.75) (percentile maes 0.95) (Array.average maes)
            plnf "    Bars held:         p5=%.0f  p25=%.0f  med=%.0f  p75=%.0f  p95=%.0f  mean=%.1f"
                (percentile bars 0.05) (percentile bars 0.25) (percentile bars 0.5)
                (percentile bars 0.75) (percentile bars 0.95) (Array.average bars)
            plnf "    Ratio at entry:    p5=%.3f  p25=%.3f  med=%.3f  p75=%.3f  p95=%.3f  mean=%.3f"
                (percentile ratios 0.05) (percentile ratios 0.25) (percentile ratios 0.5)
                (percentile ratios 0.75) (percentile ratios 0.95) (Array.average ratios)

    plnf "--- Excursion + Holding (per-trade) ---"
    excursionStats "All trades:" allTrips
    excursionStats "Long trades:" longTrips
    excursionStats "Short trades:" shortTrips
    plnf ""

    // ----- Squeeze-survival breakdown (shorts only) -----
    // Bucket short trades by their MAE depth and report win rate / avg P&L
    // within each bucket. Tells us whether shorts that dipped deep into the
    // red still recovered (would surprise us — usually deep MAE = stop-out
    // territory) or whether the system effectively avoids that scenario.
    if shortTrips.Length > 0 then
        plnf "--- Short Squeeze-Survival (by MAE %% of notional) ---"
        let buckets =
            [| "MAE > -2%",       (fun (mae: float) -> mae > -2.0)
               "MAE -2% to -5%",  (fun mae -> mae <= -2.0 && mae > -5.0)
               "MAE -5% to -10%", (fun mae -> mae <= -5.0 && mae > -10.0)
               "MAE -10% to -20%",(fun mae -> mae <= -10.0 && mae > -20.0)
               "MAE -20% to -50%",(fun mae -> mae <= -20.0 && mae > -50.0)
               "MAE <= -50%",     (fun mae -> mae <= -50.0) |]
        plnf "  %-22s %8s %8s %10s %10s %10s"
            "bucket" "trades" "wins" "winRate" "avgPnL$" "totalPnL$"
        for (label, pred) in buckets do
            let inBucket =
                shortTrips
                |> Array.filter (fun t ->
                    let _, maePct = pnlPct t
                    pred maePct)
            if inBucket.Length > 0 then
                let nWin = inBucket |> Array.filter (fun t -> t.NetPnL > 0.0) |> Array.length
                let total = inBucket |> Array.sumBy (fun t -> t.NetPnL)
                let avg = total / float inBucket.Length
                let wr = 100.0 * float nWin / float inBucket.Length
                plnf "  %-22s %8d %8d %9.1f%% %10.2f %10.2f"
                    label inBucket.Length nWin wr avg total
        plnf ""

    // ----- Volume-decile stratification (per-trade, leak-free) -----
    // Tests the "big = long edge, small = short edge" thesis WITHOUT
    // contamination from future volume. Each round-trip carries the
    // symbol's trailing-90d average daily quote volume AT THE TIME OF
    // ENTRY (RoundTrip.AvgDailyVolumeAtEntry). We rank trips by ADV-at-
    // entry, bin into 10 deciles, and report per-decile long/short PF and
    // P&L. Same symbol can contribute trips to different deciles over
    // time as its liquidity regime changes — which is exactly the right
    // thing for testing the thesis.
    //
    // Trips opened during the warmup window (before the rolling buffer
    // accumulated meaningful history) get ADV = 0. We exclude those from
    // the decile bins entirely (would otherwise dominate decile 1).
    ignore advBySymbol
    let tripsWithAdv =
        allTrips |> Array.filter (fun t -> t.AvgDailyVolumeAtEntry > 0.0)
    if tripsWithAdv.Length >= 100 then
        plnf "--- Volume Decile Stratification (per-trade, trailing 90d ADV at entry) ---"
        let ranked = tripsWithAdv |> Array.sortBy (fun t -> t.AvgDailyVolumeAtEntry)
        let n = ranked.Length
        let pfRatio (gw: float) (gl: float) =
            if gl > 0.0 then sprintf "%.3f" (gw / gl)
            elif gw > 0.0 then "inf"
            else "0.000"
        plnf "  %-7s %12s %12s %8s %10s %10s %10s %10s %10s"
            "decile" "advLo$" "advHi$" "trips" "longTrades" "longPnL$" "longPF" "shortPnL$" "shortPF"
        // Group ranked trips into 10 chunks of equal size.
        for d in 1 .. 10 do
            let lo = ((d - 1) * n) / 10
            let hi = (d * n) / 10  // exclusive
            if hi > lo then
                let chunk = ranked.[lo .. hi - 1]
                let advLo = chunk.[0].AvgDailyVolumeAtEntry
                let advHi = chunk.[chunk.Length - 1].AvgDailyVolumeAtEntry
                let longs = chunk |> Array.filter (fun t -> t.Side = Long)
                let shorts = chunk |> Array.filter (fun t -> t.Side = Short)
                let longGw = longs |> Array.sumBy (fun t -> if t.NetPnL > 0.0 then t.NetPnL else 0.0)
                let longGl = longs |> Array.sumBy (fun t -> if t.NetPnL < 0.0 then -t.NetPnL else 0.0)
                let shortGw = shorts |> Array.sumBy (fun t -> if t.NetPnL > 0.0 then t.NetPnL else 0.0)
                let shortGl = shorts |> Array.sumBy (fun t -> if t.NetPnL < 0.0 then -t.NetPnL else 0.0)
                let longPnl = longs |> Array.sumBy (fun t -> t.NetPnL)
                let shortPnl = shorts |> Array.sumBy (fun t -> t.NetPnL)
                plnf "  %-7d %12s %12s %8d %10d %10.0f %10s %10.0f %10s"
                    d
                    (advLo.ToString "N0") (advHi.ToString "N0") chunk.Length
                    longs.Length longPnl (pfRatio longGw longGl)
                    shortPnl (pfRatio shortGw shortGl)
        let warmupTrips = allTrips.Length - tripsWithAdv.Length
        if warmupTrips > 0 then
            plnf "  (excluded %d trips with no ADV-at-entry — opened in warmup window)" warmupTrips
        plnf ""
