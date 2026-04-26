#r "nuget: MathNet.Numerics, 5.0.0"
open MathNet.Numerics.Distributions
let rng = System.Random(42)
let b = Beta(0.5, 0.2, rng)
printfn "Beta(0.5,0.2) sample: %f" (b.Sample())
printfn "Beta DensityLn(0.8): %f" (b.DensityLn(0.8))
let l = Laplace(0.0, 0.01, rng)
printfn "Laplace(0,0.01) sample: %f" (l.Sample())
printfn "Laplace DensityLn(0.005): %f" (l.DensityLn(0.005))
