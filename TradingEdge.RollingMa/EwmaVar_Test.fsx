#r "bin/Release/net10.0/TradingEdge.RollingMa.dll"
// ⭐ EwmaVarMa oracle — validated four ways:
//   1. ORACLE — at every step, Mean/Var must equal the DIRECT weighted
//      computation over the full pushed history with weights α(1−α)^(n−1−i).
//   2. SHIFT INVARIANCE — Var(x + 1e6) = Var(x); Mean shifts by exactly 1e6.
//      This is what the x0-origin trick claims to buy.
//   3. WARM-UP — Var = 0 after one push (a point has no spread), ValueNone
//      before any; a CONSTANT series reads exactly 0 forever.
//   4. RECOVERY — on i.i.d. N(0, σ²) at a modest half-life, Std ≈ σ.
open System
open TradingEdge.RollingMa

let approx name a b tol =
    let e = abs (a - b) / max 1e-12 (abs b)
    printfn "  %-46s got %+12.6e  want %+12.6e   relerr %.2e %s" name a b e (if e <= tol then "OK" else "❌ FAIL")
    if e > tol then exit 1

let vv o = match o with ValueSome x -> x | ValueNone -> nan

printfn "1. ORACLE — direct weighted mean/var over the pushed history, every step"
let hl = 40.0
let alpha = 1.0 - 0.5 ** (1.0 / hl)
let rng = Random 7
// ln-price-like: a level near 3.2 with 50bp-scale moves — the production regime
let xs = Array.init 500 (fun i -> 3.2 + 0.005 * sin (float i / 7.0) + (rng.NextDouble() - 0.5) * 0.01)
let m = EwmaVarMa hl
let mutable worstM, worstV = 0.0, 0.0
for n in 1 .. xs.Length do
    m.Push xs.[n-1]
    let w = Array.init n (fun i -> alpha * (1.0 - alpha) ** float (n - 1 - i))
    let s0 = Array.sum w
    let mean = (Array.map2 (*) w xs.[0 .. n-1] |> Array.sum) / s0
    let var = (Array.map2 (fun wi xi -> wi * (xi - mean) * (xi - mean)) w xs.[0 .. n-1] |> Array.sum) / s0
    worstM <- max worstM (abs (vv m.Mean - mean) / max 1e-12 (abs mean))
    worstV <- max worstV (abs (vv m.Var - var) / max 1e-12 var)
printfn "  worst relerr over %d steps: mean %.2e  var %.2e" xs.Length worstM worstV
if worstM > 1e-10 || worstV > 1e-8 then printfn "❌ FAIL"; exit 1 else printfn "  OK"

printfn "\n2. SHIFT INVARIANCE — x + 1e6 must not move Var at all"
let a0 = EwmaVarMa hl
let a1 = EwmaVarMa hl
for x in xs do a0.Push x; a1.Push (x + 1e6)
approx "Var shifted vs unshifted" (vv a1.Var) (vv a0.Var) 1e-9
approx "Mean shifted - 1e6" (vv a1.Mean - 1e6) (vv a0.Mean) 1e-6

printfn "\n3. WARM-UP + CONSTANT SERIES"
let w1 = EwmaVarMa hl
if w1.Var <> ValueNone then printfn "❌ FAIL: Var before any push" ; exit 1
w1.Push 5.0
approx "Var after one push" (vv w1.Var) 0.0 1e-12
approx "Mean after one push" (vv w1.Mean) 5.0 1e-12
let c = EwmaVarMa hl
for _ in 1 .. 1000 do c.Push 3.14159
approx "Var of a constant series" (vv c.Var) 0.0 1e-12
printfn "  OK (warm-up ValueNone confirmed)"

printfn "\n4. RECOVERY — Std on i.i.d. N(0, 0.004²) should read ~0.004"
let rng2 = Random 99
let gauss () =
    let u1, u2 = rng2.NextDouble(), rng2.NextDouble()
    sqrt (-2.0 * log u1) * cos (2.0 * Math.PI * u2)
let r = EwmaVarMa 200.0
for _ in 1 .. 50000 do r.Push (0.004 * gauss ())
approx "Std vs planted sigma" (vv r.Std) 0.004 0.02

printfn "\nALL OK"
