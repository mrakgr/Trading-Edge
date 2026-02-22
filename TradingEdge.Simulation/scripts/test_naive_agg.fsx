#r "../bin/Debug/net9.0/TradingEdge.Simulation.dll"

open System
open System.IO
open TradingEdge.Simulation.EpisodeMCMC
open TradingEdge.Simulation.OrderFlowGeneration
open TradingEdge.Simulation.DatasetGeneration

let rng = Random(42)
let result = generateDay SessionLevel.defaultConfig TrendLevel.defaultConfig { MCMC.Iterations = 10000 } rng 390.0
let trades = generateDayTrades rng 100.0 result
let totalSeconds = barsPerDay

// Online: use existing pipeline
let bars = aggregateToSecondBars trades totalSeconds
assignLabels bars result
let online1m = combineBars bars 60
let online5m = combineBars bars 300

// Naive: aggregate trades directly into 1m/5m
let buckets = Array.init totalSeconds (fun _ -> ResizeArray<Trade>())
for t in trades do
    let sec = int t.Time |> min (totalSeconds - 1) |> max 0
    buckets.[sec].Add(t)

let naiveAgg (periodSec: int) =
    Array.init totalSeconds (fun i ->
        let start = i - (i % periodSec)
        let stop = min (start + periodSec) totalSeconds
        let mutable totalVol = 0
        let mutable sumPV = 0.0
        for s in start .. stop - 1 do
            for t in buckets.[s] do
                totalVol <- totalVol + t.Size
                sumPV <- sumPV + float t.Size * t.Price
        let vwap = if totalVol > 0 then sumPV / float totalVol else 0.0
        let mutable sumVar = 0.0
        for s in start .. stop - 1 do
            for t in buckets.[s] do
                sumVar <- sumVar + float t.Size * (t.Price - vwap) ** 2.0
        let std = if totalVol > 0 then sqrt(sumVar / float totalVol) else 0.0
        vwap, totalVol, std)

let naive1m = naiveAgg 60
let naive5m = naiveAgg 300

use w = new StreamWriter("../../data/naive_vs_online.csv")
w.WriteLine("time,online_vwap_1m,online_stddev_1m,naive_vwap_1m,naive_stddev_1m,online_vwap_5m,online_stddev_5m,naive_vwap_5m,naive_stddev_5m")
for i in 0 .. totalSeconds - 1 do
    let ov1m, _, os1m = online1m.[i]
    let nv1m, _, ns1m = naive1m.[i]
    let ov5m, _, os5m = online5m.[i]
    let nv5m, _, ns5m = naive5m.[i]
    w.WriteLine($"{i},{ov1m},{os1m},{nv1m},{ns1m},{ov5m},{os5m},{nv5m},{ns5m}")

printfn "Wrote %d rows" totalSeconds
