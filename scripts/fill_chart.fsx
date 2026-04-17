#r "../TradingEdge.Orb/bin/Debug/net10.0/TradingEdge.Orb.dll"

open System
open System.IO
open TradingEdge.Orb.TradeLoader
open TradingEdge.Orb.TradeBinary
open TradingEdge.Orb.Program

// ----- CLI -----
let ticker = fsi.CommandLineArgs.[1]
let date = fsi.CommandLineArgs.[2]

// ----- Load binary day -----
let info = { Directory = "data/trades_bin"; Ticker = ticker; Date = date }
let header, trades = loadDay info
if header.OpeningPrintIndex.IsNone then
    eprintfn "%s %s has no opening print — nothing to chart" ticker date
    exit 1

printfn "Loaded %s %s: %d trades, openingPrintIdx=%d" ticker date trades.Length header.OpeningPrintIndex.Value

// ----- Wire up pipeline -----
let seg, vs, td, ell, fs, tf = configure header

type BarRow = {
    CumulativeVolume: float
    Volume: float
    VWAP: float
    StdDev: float
    Vwma: float
    StartTime: DateTime
    EndTime: DateTime
    NumTrades: int
}
let barRows = ResizeArray<BarRow>()

// Shadow state: first-trade timestamp and trade count for the bar currently being built.
let mutable barStartTs : DateTime voption = ValueNone
let mutable barTradeCount = 0
let mutable cumulativeVolume = 0.0

let onFillSink (_: Fill) = ()
let onFill (fill: Fill) = tf.Process(onFillSink, fill)
let onTracked (decision, bar, stage, trade: Trade) =
    fs.Process(onFill, decision, bar, stage, trade)

for i in 0 .. trades.Length - 1 do
    let trade = trades.[i]
    let ts = seg.Timestamp trade
    if barStartTs.IsNone then barStartTs <- ValueSome ts
    barTradeCount <- barTradeCount + 1
    cumulativeVolume <- cumulativeVolume + float trade.Volume

    seg.Process(
        (fun (bar, stage, trade) ->
            match bar with
            | ValueSome b ->
                barRows.Add {
                    CumulativeVolume = cumulativeVolume
                    Volume = b.Bar.Volume
                    VWAP = b.Bar.VWAP
                    StdDev = b.Bar.StdDev
                    Vwma = b.Vwma
                    StartTime = barStartTs.Value
                    EndTime = ts
                    NumTrades = barTradeCount
                }
                barStartTs <- ValueNone
                barTradeCount <- 0
            | ValueNone -> ()
            vs.Process(
                (fun (decision, bar, stage, trade) ->
                    ell.Process(
                        (fun (decision, bar, stage, trade) ->
                            td.Process(onTracked, decision, bar, stage, trade)),
                        decision, bar, stage, trade)),
                bar, stage, trade, ts)),
        ReadOnlySpan(trades, 0, i + 1))

printfn "Bars: %d  Decisions: %d  Fills: %d  NetPnL: $%.2f"
    barRows.Count td.Decisions.Count tf.Fills.Count tf.NetPnL

// ----- Output CSVs -----
let outDir = Path.Combine("data", "charts", "fills", $"{ticker}_{date}")
Directory.CreateDirectory outDir |> ignore

let barsPath = Path.Combine(outDir, "bars.csv")
do
    use bw = new StreamWriter(barsPath)
    bw.WriteLine "cumulative_volume,vwap,stddev,vwma,volume,start_time,end_time,num_trades"
    for b in barRows do
        bw.WriteLine(sprintf "%.2f,%.6f,%.6f,%.6f,%.2f,%s,%s,%d"
            b.CumulativeVolume b.VWAP b.StdDev b.Vwma b.Volume
            (b.StartTime.ToString "o") (b.EndTime.ToString "o") b.NumTrades)
printfn "Wrote %s (%d bars)" barsPath barRows.Count

let decisionsPath = Path.Combine(outDir, "decisions.csv")
do
    use dw = new StreamWriter(decisionsPath)
    dw.WriteLine "timestamp,price,shares,bar_size"
    for d in td.Decisions do
        dw.WriteLine(sprintf "%s,%.6f,%d,%.2f"
            (d.Timestamp.ToString "o") d.Price d.Shares d.BarSize)
printfn "Wrote %s (%d decisions)" decisionsPath td.Decisions.Count

let fillsPath = Path.Combine(outDir, "fills.csv")
do
    use fw = new StreamWriter(fillsPath)
    fw.WriteLine "timestamp,price,quantity,side"
    for f in tf.Fills do
        let side = if f.Quantity > 0 then "buy" else "sell"
        fw.WriteLine(sprintf "%s,%.6f,%d,%s"
            (f.Timestamp.ToString "o") f.Price (abs f.Quantity) side)
printfn "Wrote %s (%d fills)" fillsPath tf.Fills.Count
