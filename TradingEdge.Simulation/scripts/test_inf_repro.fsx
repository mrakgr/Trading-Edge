#r "../bin/Debug/net9.0/TradingEdge.Simulation.dll"

open System
open TradingEdge.Simulation.EpisodeMCMC
open TradingEdge.Simulation.DatasetGeneration

for dayId in 0 .. 9 do
    let data = generateSingleDay dayId (42 + dayId) { MCMC.Iterations = 10000 } SessionLevel.defaultConfig TrendLevel.defaultConfig 100.0
    let infs = data.Vwap1s |> Array.filter Double.IsInfinity |> Array.length
    let nans = data.Vwap1s |> Array.filter Double.IsNaN |> Array.length
    if infs > 0 || nans > 0 then
        printfn "Day %d: %d Infs, %d NaNs" dayId infs nans
    else
        printfn "Day %d: OK" dayId
