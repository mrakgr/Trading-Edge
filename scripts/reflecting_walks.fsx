#r "nuget: MathNet.Numerics, 5.0.0"
#r "nuget: Plotly.NET, 5.1.0"

open System
open MathNet.Numerics.Distributions
open Plotly.NET

let runReflectingWalk (rng: Random) (drift: float) (sigma: float) (steps: int) =
    let mutable x = 0.8
    let trace = Array.zeroCreate steps
    for i in 0 .. steps - 1 do
        let mutable z = x + drift + Normal.Sample(rng, 0.0, sigma)
        if z > 1.0 then z <- 2.0 - z
        if z < 0.0 then z <- -z
        x <- max 0.0 (min 1.0 z)
        trace.[i] <- x
    trace

let rng = Random(42)
let steps = 50000

let configs = [|
    (0.002, 0.02, "drift=0.002 sigma=0.02 (gentle drift, tight)")
    (0.002, 0.05, "drift=0.002 sigma=0.05 (gentle drift, moderate)")
    (0.005, 0.02, "drift=0.005 sigma=0.02 (moderate drift, tight)")
    (0.005, 0.05, "drift=0.005 sigma=0.05 (moderate drift, moderate)")
    (0.01,  0.05, "drift=0.01 sigma=0.05 (strong drift, moderate)")
    (0.0,   0.05, "drift=0.0 sigma=0.05 (no drift, moderate)")
|]

let charts =
    configs
    |> Array.mapi (fun i (drift, sigma, label) ->
        let trace = runReflectingWalk rng drift sigma steps
        Chart.Line(Array.init steps id, trace, Name = label)
        |> Chart.withYAxisStyle(label, MinMax = (0.0, 1.0))
    )
    |> Array.toList

let combined =
    charts
    |> Chart.Grid(configs.Length, 1)
    |> Chart.withSize(1200, 1600)
    |> Chart.withTitle "Reflecting Barrier Random Walks (50k steps)"

combined |> Chart.saveHtml "/home/mrakgr/Trading-Edge/scripts/reflecting_walks.html"
printfn "Written to scripts/reflecting_walks.html"
