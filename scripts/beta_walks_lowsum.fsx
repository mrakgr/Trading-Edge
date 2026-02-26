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
let steps = 50000
let proposalStdDev = 0.05

let configs = [|
    (0.40, 0.10, "Beta(0.40,0.10) sum=0.50")
    (0.20, 0.05, "Beta(0.20,0.05) sum=0.25")
    (0.10, 0.025, "Beta(0.10,0.025) sum=0.125")
    (0.04, 0.01, "Beta(0.04,0.01) sum=0.05")
    (0.008, 0.002, "Beta(0.008,0.002) sum=0.01")
    (0.004, 0.001, "Beta(0.004,0.001) sum=0.005")
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
    |> Chart.withTitle "Beta walks with 4:1 ratio, decreasing sum (proposal σ=0.05, 5000 steps)"

combined |> Chart.saveHtml "/home/mrakgr/Trading-Edge/scripts/beta_walks_lowsum.html"
printfn "Written to scripts/beta_walks_lowsum.html"
