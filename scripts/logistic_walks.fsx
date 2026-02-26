#r "nuget: MathNet.Numerics, 5.0.0"
#r "nuget: Plotly.NET, 5.1.0"

open System
open MathNet.Numerics.Distributions
open Plotly.NET

let sigmoid x = 1.0 / (1.0 + exp(-x))

let runLogisticWalk (rng: Random) (mu: float) (theta: float) (sigma: float) (steps: int) =
    let mutable z = mu
    let trace = Array.zeroCreate steps
    for i in 0 .. steps - 1 do
        z <- z + theta * (mu - z) + Normal.Sample(rng, 0.0, sigma)
        trace.[i] <- sigmoid z
    trace

let rng = Random(42)
let steps = 50000

let configs = [|
    (2.0, 0.05, 0.3, "mu=2 theta=0.05 sigma=0.3 (gentle reversion)")
    (2.0, 0.05, 0.6, "mu=2 theta=0.05 sigma=0.6 (more volatile)")
    (2.0, 0.10, 0.3, "mu=2 theta=0.10 sigma=0.3 (faster reversion)")
    (2.0, 0.10, 0.6, "mu=2 theta=0.10 sigma=0.6 (fast + volatile)")
    (3.0, 0.05, 0.3, "mu=3 theta=0.05 sigma=0.3 (stronger hold bias)")
    (1.0, 0.05, 0.3, "mu=1 theta=0.05 sigma=0.3 (weaker hold bias)")
|]

let charts =
    configs
    |> Array.mapi (fun i (mu, theta, sigma, label) ->
        let trace = runLogisticWalk rng mu theta sigma steps
        Chart.Line(Array.init steps id, trace, Name = label)
        |> Chart.withYAxisStyle(label, MinMax = (0.0, 1.0))
    )
    |> Array.toList

let combined =
    charts
    |> Chart.Grid(configs.Length, 1)
    |> Chart.withSize(1200, 1600)
    |> Chart.withTitle "Logistic-Normal Walks (OU in logit space, 50k steps)"

combined |> Chart.saveHtml "/home/mrakgr/Trading-Edge/scripts/logistic_walks.html"
printfn "Written to scripts/logistic_walks.html"
