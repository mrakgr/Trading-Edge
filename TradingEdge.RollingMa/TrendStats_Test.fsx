#r "bin/Release/net10.0/TradingEdge.RollingMa.dll"
// ⭐ ORACLE TEST for AutoCorrMa / VarianceRatioMa / SignPersistMa.
//
// Each streaming class is compared, AT EVERY PUSH, against a naive O(n)
// recomputation over exactly the points the class claims to hold. Both window
// policies are exercised on the same stream, because the whole point of the
// shared implementation is that anchored and rolling cannot drift apart — a test
// that only covered one of them would not notice if they had.
//
// The stream is deliberately nasty: a trending component (non-zero drift, which
// is what breaks an uncentered autocorrelation), an AR(1) component with a KNOWN
// coefficient, exact zeros (the tie path), and sign flips.
open System
open TradingEdge.RollingMa

let rng = Random 20260824
let n = 4000
let xs =
    let mutable prev = 0.0
    [| for i in 1 .. n ->
         let shock = (rng.NextDouble() - 0.5) * 0.02
         let ar = 0.35 * prev + shock            // AR(1), rho1 = 0.35
         prev <- ar
         // drift + occasional exact zeros (ties)
         if i % 37 = 0 then 0.0 else ar + 0.0008 |]

let mutable worstAc = 0.0
let mutable worstVr = 0.0
let mutable worstSp = 0.0
let mutable checks = 0

/// Naive ACF over an explicit slice, same convention as AutoCorrMa.
let oracleRho (w: float[]) (k: int) =
    let m = w.Length
    if m < k + 3 then nan else
    let mean = Array.average w
    let den = w |> Array.sumBy (fun v -> (v - mean) * (v - mean))
    if not (den > 0.0) then nan else
    let mutable num = 0.0
    for t in k .. m - 1 do num <- num + (w.[t] - mean) * (w.[t - k] - mean)
    num / den

let oracleVr (w: float[]) (q: int) =
    let m = w.Length
    if m < q + 3 then nan else
    let mean1 = Array.average w
    let var1 = (w |> Array.sumBy (fun v -> (v - mean1) * (v - mean1))) / float (m - 1)
    let rs = [| for t in q - 1 .. m - 1 -> Array.sum w.[t - q + 1 .. t] |]
    if rs.Length < 2 || not (var1 > 0.0) then nan else
    let meanQ = Array.average rs
    let varq = (rs |> Array.sumBy (fun v -> (v - meanQ) * (v - meanQ))) / float (rs.Length - 1)
    varq / (float q * var1)

let oracleSp (w: float[]) =
    let mutable con = 0
    let mutable dis = 0
    for t in 1 .. w.Length - 1 do
        let p = w.[t - 1] * w.[t]
        if p > 0.0 then con <- con + 1 elif p < 0.0 then dis <- dis + 1
    if con + dis < 4 then nan else float con / float (con + dis)

let cmp (name: string) (got: float) (want: float) (acc: float byref) =
    if Double.IsNaN got <> Double.IsNaN want then
        failwithf "%s: nan mismatch got=%f want=%f" name got want
    if not (Double.IsNaN got) then
        let e = abs (got - want) / max 1.0 (abs want)
        if e > acc then acc <- e
        checks <- checks + 1

let W = 40
let acA = AutoCorrMa(0, 3)
let acR = AutoCorrMa(W, 3)
let vr2A = VarianceRatioMa(0, 2)
let vr4A = VarianceRatioMa(0, 4)
let vr2R = VarianceRatioMa(W, 2)
let vr4R = VarianceRatioMa(W, 4)
let spA = SignPersistMa 0
let spR = SignPersistMa W

for i in 0 .. n - 1 do
    let r = xs.[i]
    acA.Push r; acR.Push r
    vr2A.Push r; vr4A.Push r; vr2R.Push r; vr4R.Push r
    spA.Push r; spR.Push r
    let anch = xs.[0 .. i]
    let roll = xs.[max 0 (i - W + 1) .. i]
    for k in 1 .. 3 do
        cmp $"acA lag{k}" (acA.Rho k) (oracleRho anch k) &worstAc
        cmp $"acR lag{k}" (acR.Rho k) (oracleRho roll k) &worstAc
    cmp "vr2A" vr2A.Value (oracleVr anch 2) &worstVr
    cmp "vr4A" vr4A.Value (oracleVr anch 4) &worstVr
    cmp "vr2R" vr2R.Value (oracleVr roll 2) &worstVr
    cmp "vr4R" vr4R.Value (oracleVr roll 4) &worstVr
    cmp "spA" spA.Value (oracleSp anch) &worstSp
    cmp "spR" spR.Value (oracleSp roll) &worstSp

printfn "checks           %d" checks
printfn "worst rel err    autocorr %.3e   varratio %.3e   signpersist %.3e" worstAc worstVr worstSp
printfn ""
printfn "recovered AR(1) rho1 = %.4f  (true 0.35, plus drift)" (acA.Rho 1)
printfn "  lag2 %.4f (expect ~0.35^2 = %.4f)   lag3 %.4f (expect ~%.4f)"
    (acA.Rho 2) (0.35 ** 2.0) (acA.Rho 3) (0.35 ** 3.0)
printfn "  VR(2) %.4f   VR(4) %.4f   (>1 = trending, as an AR(1) with rho>0 must be)"
    vr2A.Value vr4A.Value
printfn "  sign persistence %.4f   run %d" spA.Value spA.Run
// ⭐ THE CONTROL: an i.i.d. stream must give rho ~ 0 and VR ~ 1. Without this a
// bug that returns "trending" for everything would pass every check above.
let acI = AutoCorrMa(0, 3)
let vrI = VarianceRatioMa(0, 2)
let spI = SignPersistMa 0
let rng2 = Random 7
for _ in 1 .. 40000 do
    let v = (rng2.NextDouble() - 0.5) * 0.02
    acI.Push v; vrI.Push v; spI.Push v
printfn ""
printfn "i.i.d. CONTROL   rho1 %+.4f  VR(2) %.4f  signpersist %.4f   (expect ~0, ~1, ~0.5)"
    (acI.Rho 1) vrI.Value spI.Value
if worstAc > 1e-9 || worstVr > 1e-9 || worstSp > 1e-12 then
    eprintfn "FAIL: streaming/oracle disagreement"; exit 1
printfn ""
printfn "ALL PASS"
