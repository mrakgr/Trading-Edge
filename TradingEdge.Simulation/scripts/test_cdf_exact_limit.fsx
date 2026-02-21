#r "nuget: MathNet.Numerics"
open MathNet.Numerics.Distributions

// Binary search for the largest z where InvCDF(CDF(z)) is finite
let mutable lo = 8.0
let mutable hi = 9.0
for _ in 0 .. 100 do
    let mid = (lo + hi) / 2.0
    let cdf = Normal.CDF(0.0, 1.0, mid)
    let roundtrip = Normal.InvCDF(0.0, 1.0, cdf)
    if System.Double.IsFinite(roundtrip) then lo <- mid else hi <- mid

printfn "Max safe positive z: %.17f" lo
printfn "  CDF(z)     = %.17e" (Normal.CDF(0.0, 1.0, lo))
printfn "  roundtrip  = %.17f" (Normal.InvCDF(0.0, 1.0, Normal.CDF(0.0, 1.0, lo)))

// Same for negative side
let mutable lo2 = -40.0
let mutable hi2 = -8.0
for _ in 0 .. 100 do
    let mid = (lo2 + hi2) / 2.0
    let cdf = Normal.CDF(0.0, 1.0, mid)
    let roundtrip = Normal.InvCDF(0.0, 1.0, cdf)
    if System.Double.IsFinite(roundtrip) then hi2 <- mid else lo2 <- mid

printfn ""
printfn "Max safe negative z: %.17f" hi2
printfn "  CDF(z)     = %.17e" (Normal.CDF(0.0, 1.0, hi2))
printfn "  roundtrip  = %.17f" (Normal.InvCDF(0.0, 1.0, Normal.CDF(0.0, 1.0, hi2)))
