#r "nuget: MathNet.Numerics, 5.0.0"

open System
open MathNet.Numerics.Distributions

let lo = 1e-3
let hi = 1.0 - 1e-3

let configs = [|
    (0.40, 0.10)
    (0.20, 0.05)
    (0.10, 0.025)
    (0.04, 0.01)
    (0.008, 0.002)
    (0.004, 0.001)
|]

for (a, b) in configs do
    let target = Beta(a, b)
    let dHi = target.DensityLn(hi)
    let dLo = target.DensityLn(lo)
    let logAcceptHiToLo = dLo - dHi
    let logAcceptLoToHi = dHi - dLo
    let pHiToLo = min 1.0 (exp logAcceptHiToLo)
    let pLoToHi = min 1.0 (exp logAcceptLoToHi)
    printfn "Beta(%.3f,%.3f): densityLn(hi)=%.3f densityLn(lo)=%.3f  P(hi->lo)=%.4f  P(lo->hi)=%.4f" a b dHi dLo pHiToLo pLoToHi
