#r "nuget: MathNet.Numerics, 5.0.0"
#r "nuget: Plotly.NET, 5.1.0"

open System
open MathNet.Numerics.Distributions
open Plotly.NET

let runBetaWalk (rng: Random) (a: float) (b: float) (proposalStdDev: float) (steps: int) =
    let mutable x = 0.5
    let target = Beta(a, b)
    let trace = Array.zeroCreate steps
    for i in 0 .. steps - 1 do
        let proposal = x + Normal.Sample(rng, 0.0, proposalStdDev)
        if proposal > 0.0 && proposal < 1.0 then
            let logAccept = target.DensityLn(proposal) - target.DensityLn(x)
            if log(rng.NextDouble()) < logAccept then
                x <- proposal
        trace.[i] <- x
    trace

let rng = Random(42)
let steps = 5000
let proposalStdDev = 0.05

let configs = [|
    (0.5, 0.5, "Beta(0.5,0.5) U-shaped symmetric")
    (0.3, 0.1, "Beta(0.3,0.1) U-shaped biased→1")
    (0.5, 0.2, "Beta(0.5,0.2) U-shaped biased→1")
    (0.8, 0.2, "Beta(0.8,0.2) moderate bias→1")
    (2.0, 0.5, "Beta(2.0,0.5) hill biased→1")
    (4.0, 1.0, "Beta(4.0,1.0) concentrated→1")
|]

let charts =
    configs
    |> Array.mapi (fun i (a, b, label) ->
        let trace = runBetaWalk rng a b proposalStdDev steps
        Chart.Line(Array.init steps id, trace, Name = label)
        |> Chart.withYAxisStyle(label, MinMax = (0.0, 1.0))
    )
    |> Array.toList

let combined =
    charts
    |> Chart.Grid(configs.Length, 1)
    |> Chart.withSize(1200, 1600)
    |> Chart.withTitle "Beta-targeted MCMC Random Walks (proposal σ=0.05, 5000 steps)"

combined |> Chart.saveHtml "/home/mrakgr/Trading-Edge/scripts/beta_walks.html"
printfn "Written to scripts/beta_walks.html"
