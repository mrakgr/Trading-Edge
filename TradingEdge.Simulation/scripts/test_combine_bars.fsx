#r "../bin/Debug/net9.0/TradingEdge.Simulation.dll"

open TradingEdge.Simulation.DatasetGeneration

// Test: combineBars should produce correct VWAP, Volume, StdDev
// by comparing against naive two-pass computation over each period

let bars = [|
    { Vwap = 100.0; Volume = 500; StdDev = 0.01; Session = 0; Trend = 0 }
    { Vwap = 100.5; Volume = 300; StdDev = 0.02; Session = 0; Trend = 0 }
    { Vwap = 101.0; Volume = 200; StdDev = 0.015; Session = 0; Trend = 0 }
    { Vwap = 99.0;  Volume = 400; StdDev = 0.03; Session = 0; Trend = 0 }
    { Vwap = 99.5;  Volume = 0;   StdDev = 0.0;  Session = 0; Trend = 0 }  // empty bar
    { Vwap = 100.2; Volume = 600; StdDev = 0.025; Session = 0; Trend = 0 }
|]

let period = 3
let result = combineBars bars period

// Naive two-pass for each period
let naiveAggregate (bars: SecondBar[]) (start: int) (endExcl: int) =
    let mutable totalVol = 0
    for i in start .. endExcl - 1 do
        totalVol <- totalVol + bars.[i].Volume
    if totalVol = 0 then (0.0, 0, 0.0)
    else
        let vwap = 
            let mutable s = 0.0
            for i in start .. endExcl - 1 do
                s <- s + float bars.[i].Volume * bars.[i].Vwap
            s / float totalVol
        let variance =
            let mutable s = 0.0
            for i in start .. endExcl - 1 do
                let w = float bars.[i].Volume
                if w > 0.0 then
                    s <- s + w * (bars.[i].StdDev * bars.[i].StdDev + (bars.[i].Vwap - vwap) ** 2.0)
            s / float totalVol
        (vwap, totalVol, sqrt variance)

let tol = 1e-10
let mutable passed = 0
let mutable failed = 0

for period_idx in 0 .. (bars.Length / period) - 1 do
    let start = period_idx * period
    for i in start .. start + period - 1 do
        let expected = naiveAggregate bars start (i + 1)
        let actual = result.[i]
        let evwap, evol, estd = expected
        let avwap, avol, astd = actual
        
        let ok = abs(evwap - avwap) < tol && evol = avol && abs(estd - astd) < tol
        if ok then
            passed <- passed + 1
        else
            failed <- failed + 1
            printfn "FAIL at index %d:" i
            printfn "  Expected: vwap=%.6f vol=%d std=%.6f" evwap evol estd
            printfn "  Actual:   vwap=%.6f vol=%d std=%.6f" avwap avol astd

printfn ""
printfn "Results: %d passed, %d failed" passed failed
if failed = 0 then printfn "ALL TESTS PASSED"
else printfn "SOME TESTS FAILED"
