#r "nuget: MathNet.Numerics, 5.0.0"
#r "nuget: Plotly.NET, 5.1.0"

open System
open Plotly.NET

let runHMM (rng: Random) (pHoldToLoose: float) (pLooseToHold: float) (steps: int) =
    let mutable holding = true
    let trace = Array.zeroCreate steps
    for i in 0 .. steps - 1 do
        if holding then
            if rng.NextDouble() < pHoldToLoose then holding <- false
        else
            if rng.NextDouble() < pLooseToHold then holding <- true
        trace.[i] <- if holding then 1.0 else 0.0
    trace

let rng = Random(42)
let steps = 50000

let configs = [|
    (0.002, 0.05,  "P(h→l)=0.002 P(l→h)=0.05  avg hold=500 avg loose=20")
    (0.005, 0.05,  "P(h→l)=0.005 P(l→h)=0.05  avg hold=200 avg loose=20")
    (0.002, 0.10,  "P(h→l)=0.002 P(l→h)=0.10  avg hold=500 avg loose=10")
    (0.01,  0.05,  "P(h→l)=0.01  P(l→h)=0.05  avg hold=100 avg loose=20")
    (0.005, 0.10,  "P(h→l)=0.005 P(l→h)=0.10  avg hold=200 avg loose=10")
    (0.001, 0.02,  "P(h→l)=0.001 P(l→h)=0.02  avg hold=1000 avg loose=50")
|]

let charts =
    configs
    |> Array.mapi (fun i (pHL, pLH, label) ->
        let trace = runHMM rng pHL pLH steps
        Chart.Line(Array.init steps id, trace, Name = label)
        |> Chart.withYAxisStyle(label, MinMax = (-0.1, 1.1))
    )
    |> Array.toList

let combined =
    charts
    |> Chart.Grid(configs.Length, 1)
    |> Chart.withSize(1200, 1600)
    |> Chart.withTitle "HMM Hold/Loose State (1=holding, 0=loose, 50k steps)"

combined |> Chart.saveHtml "/home/mrakgr/Trading-Edge/scripts/hmm_walks.html"
printfn "Written to scripts/hmm_walks.html"
