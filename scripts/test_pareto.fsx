#r "nuget: MathNet.Numerics, 5.0.0"
open MathNet.Numerics.Distributions
let rng = System.Random(42)
// Try both orderings
let p1 = Pareto(2.0, 100.0, rng)
printfn "Pareto(2.0, 100.0): mean=%.2f, min=%.2f, mode=%.2f" p1.Mean p1.Minimum p1.Mode
let p2 = Pareto(100.0, 2.0, rng)
printfn "Pareto(100.0, 2.0): mean=%.2f, min=%.2f, mode=%.2f" p2.Mean p2.Minimum p2.Mode
