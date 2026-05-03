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
    "symbol,timeframe,ma_length,allow_short,bars_total,trades,wins,win_rate,profit_factor,net_pnl,gross_wins,gross_losses,sharpe,max_drawdown,total_return_pct,start_us,end_us"

let private resultsRow (m: Metrics) : string =
    String.concat "," [
        m.Symbol
        m.Timeframe
        string m.MaLength
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

/// Aggregate per-(timeframe, ma_length) across symbols. Reported separately
/// per AllowShort mode so the long-only and long/short cells don't get
/// pooled into one summary row.
type SummaryRow = {
    Timeframe: string
    MaLength: int
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
    |> Array.groupBy (fun m -> m.Timeframe, m.MaLength, m.AllowShort)
    |> Array.map (fun ((tf, ma, sh), grp) ->
        let validGrp = grp |> Array.filter (fun m -> m.Trades > 0)
        let sharpes = validGrp |> Array.map (fun m -> m.Sharpe)
        let returns = validGrp |> Array.map (fun m -> m.TotalReturnPct)
        let nProf = validGrp |> Array.filter (fun m -> m.NetPnL > 0.0) |> Array.length
        let nPF = validGrp |> Array.filter (fun m -> m.ProfitFactor > 1.0) |> Array.length
        let denom = max 1 validGrp.Length
        {
            Timeframe = tf
            MaLength = ma
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
    "timeframe,ma_length,allow_short,symbols,median_sharpe,mean_sharpe,pct_profitable,pct_profit_factor_gt1,median_total_return_pct,mean_total_return_pct"

let writeSummary (path: string) (rows: SummaryRow[]) =
    let lines = seq {
        yield summaryHeader
        for r in rows ->
            String.concat "," [
                r.Timeframe
                string r.MaLength
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
    "symbol,timeframe,ma_length,allow_short,entry_us,exit_us,side,entry_price,exit_price,net_pnl,fees"

let writeTrips (path: string) (symbol: string) (timeframe: string) (cfg: StrategyConfig) (trips: RoundTrip[]) =
    let sideStr =
        function
        | Flat -> "flat"
        | Long -> "long"
        | Short -> "short"
    let lines = seq {
        yield tripsHeader
        for t in trips ->
            String.concat "," [
                symbol
                timeframe
                string cfg.MaLength
                (if cfg.AllowShort then "1" else "0")
                string t.EntryUs
                string t.ExitUs
                sideStr t.Side
                fmt t.EntryPrice
                fmt t.ExitPrice
                fmt t.NetPnL
                fmt t.Fees
            ]
    }
    writeAtomic path lines
