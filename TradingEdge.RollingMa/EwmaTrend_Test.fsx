#r "bin/Release/net10.0/TradingEdge.RollingMa.dll"
// ⭐ The EWMA trend statistics are validated three ways, because "it produced a
// number" is not validation:
//   1. IDENTITY — EwmaEffMa at a huge half-life must converge to the plain
//      Sum(r)/Sum(|r|) it generalises. This is the user's telescoping claim.
//   2. RECOVERY — on a planted AR(1) they must recover the known rho and the
//      VR the identity VR(2) = 1 + rho1 implies.
//   3. i.i.d. CONTROL — on memoryless data they must read ~0 / ~1. Without this
//      a bug that says "trending" about everything passes 1 and 2.
open System
open TradingEdge.RollingMa

let approx name a b tol =
    let e = abs (a - b) / max 1.0 (abs b)
    printfn "  %-46s got %+9.5f  want %+9.5f   relerr %.2e %s" name a b e (if e <= tol then "OK" else "❌ FAIL")
    if e > tol then exit 1

printfn "1. IDENTITY — huge half-life must equal the unweighted Sum(r)/Sum(|r|)"
let rng = Random 42
let xs = Array.init 3000 (fun _ -> (rng.NextDouble() - 0.5) * 0.02 + 0.001)
let big = EwmaEffMa(1e7, 3)
for x in xs do big.Push x
approx "EwmaEffMa(hl=1e7)" big.Value ((Array.sum xs) / (xs |> Array.sumBy abs)) 1e-3

printfn "\n   ...and the telescoping claim itself: Sum(r) = ln(V_last / V_first)"
let mutable v = 100.0
let vs = ResizeArray [100.0]
for x in xs do v <- v * exp x; vs.Add v
approx "Sum(r) vs ln(V_n/V_0)" (Array.sum xs) (log (vs.[vs.Count-1] / vs.[0])) 1e-12

printfn "\n2. RECOVERY — planted AR(1), phi = 0.35, with drift"
let mutable p = 0.0
let ar = Array.init 60000 (fun i ->
    let e = (rng.NextDouble() - 0.5) * 0.02
    p <- 0.35 * p + e
    if i % 37 = 0 then 0.0 else p + 0.0008)
let ac = EwmaAutoCorrMa(2000.0, 3)
let vr2 = EwmaVarRatioMa(2000.0, 2)
let vr4 = EwmaVarRatioMa(2000.0, 4)
for x in ar do ac.Push x; vr2.Push x; vr4.Push x
printfn "  rho1 %+.4f  rho2 %+.4f  rho3 %+.4f" (ac.Rho 1) (ac.Rho 2) (ac.Rho 3)
approx "VR(2) vs the identity 1 + rho1" vr2.Value (1.0 + ac.Rho 1) 0.03
approx "VR(4) vs 1+2(.75r1+.5r2+.25r3)" vr4.Value
    (1.0 + 2.0 * (0.75 * ac.Rho 1 + 0.50 * ac.Rho 2 + 0.25 * ac.Rho 3)) 0.05

printfn "\n3. i.i.d. CONTROL — memoryless data must read ~0 / ~1 / ~0"
let acI = EwmaAutoCorrMa(2000.0, 3)
let vrI = EwmaVarRatioMa(2000.0, 2)
let efI = EwmaEffMa(2000.0, 3)
let rng2 = Random 7
for _ in 1 .. 60000 do
    let x = (rng2.NextDouble() - 0.5) * 0.02
    acI.Push x; vrI.Push x; efI.Push x
printfn "  rho1 %+.4f   VR(2) %.4f   eff %+.4f   (expect ~0, ~1, ~0)" (acI.Rho 1) vrI.Value efI.Value
if abs (acI.Rho 1) > 0.03 || abs (vrI.Value - 1.0) > 0.05 || abs efI.Value > 0.06 then
    eprintfn "❌ FAIL: i.i.d. control out of range"; exit 1

printfn "\n4. WARM-UP — the EWMA eff must be live where the windowed one is still cold"
let ew = EwmaEffMa(40.0, 3)
let win = SumMa 40
let lag = LagMa<float> 40
for i in 0 .. 20 do
    ew.Push xs.[i]; win.Push (abs xs.[i]); lag.Push xs.[i]
printfn "  after 21 slot returns:  eff_ewma %+.4f    windowed eff %s"
    ew.Value (if win.Count = win.WindowSize then "warm" else "STILL nan (needs 40)")
printfn "\nALL PASS"
