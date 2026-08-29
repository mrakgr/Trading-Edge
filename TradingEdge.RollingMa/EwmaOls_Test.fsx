// ⭐ EwmaOlsMa oracle (S43cg; user, 2026-08-29). Three-way validation, per the
// EwmaTrend_Test discipline:
//   1. ORACLE — the recursive moments must equal a brute-force weighted OLS
//      (weights λ^age over the FULL history) at every step.
//   2. IDENTITY — at a huge half-life it must converge to the plain unweighted
//      OLS slope over the same history.
//   3. RECOVERY — on a planted noiseless linear trend y = a + b·i it must read
//      slope = b (any positive weighting recovers an exact line) with |r| = 1.
#r "/home/mrakgr/Trading-Edge/research/TradingEdge.RollingMa/bin/Release/net10.0/TradingEdge.RollingMa.dll"
open TradingEdge.RollingMa
open System

let mutable failures = 0
let check label ok =
    if not ok then failures <- failures + 1
    printfn "  %s %s" (if ok then "✓" else "✗ FAIL") label

let bruteSlope (lam: float) (ys: float[]) =
    // weights λ^age, x = −age; returns (slope, signed r)
    let n = ys.Length
    let mutable s0, sx, sxx, sy, sxy, syy = 0.0, 0.0, 0.0, 0.0, 0.0, 0.0
    for i in 0 .. n - 1 do
        let age = float (n - 1 - i)
        let w = lam ** age
        let x = -age
        s0 <- s0 + w
        sx <- sx + w * x
        sxx <- sxx + w * x * x
        sy <- sy + w * ys.[i]
        sxy <- sxy + w * x * ys.[i]
        syy <- syy + w * ys.[i] * ys.[i]
    let dxx = s0 * sxx - sx * sx
    let dyy = s0 * syy - sy * sy
    let dxy = s0 * sxy - sx * sy
    (dxy / dxx), (float (sign (dxy / dxx)) * sqrt (min 1.0 (dxy * dxy / (dxx * dyy))))

printfn "1. ORACLE — recursive vs brute-force weighted OLS, hl ∈ {6, 40}, 400 pushes"
let rng = Random 42
let ys = Array.init 400 (fun _ -> rng.NextDouble() * 0.02)
for hl in [6.0; 40.0] do
    let lam = 0.5 ** (1.0 / hl)
    let m = EwmaOlsMa hl
    let mutable worstS = 0.0
    let mutable worstR = 0.0
    for i in 0 .. ys.Length - 1 do
        m.Push ys.[i]
        if i >= 2 then
            let bs, br = bruteSlope lam ys.[.. i]
            match m.State with
            | ValueSome (struct (s, r)) ->
                worstS <- max worstS (abs (s - bs) / max 1e-12 (abs bs))
                worstR <- max worstR (abs (r - br))
            | ValueNone -> failures <- failures + 1
    check (sprintf "hl=%-4.0f slope worst relerr %.2e, r worst abserr %.2e" hl worstS worstR)
        (worstS <= 1e-8 && worstR <= 1e-10)

printfn "2. IDENTITY — hl=1e7 must equal the unweighted OLS over the history"
let big = EwmaOlsMa 1e7
for y in ys do big.Push y
let ubs, _ = bruteSlope 1.0 ys
(match big.State with
 | ValueSome (struct (s, _)) ->
     // λ = 0.5^(1e-7) leaves the 400 weights within ~3e-5 of 1, so the identity
     // holds to ~1e-4 relative at this n — not to machine precision.
     check (sprintf "slope %+.3e vs unweighted %+.3e" s ubs) (abs (s - ubs) / max 1e-12 (abs ubs) <= 1e-4)
 | ValueNone -> check "state present" false)

printfn "3. RECOVERY — planted line y = 0.5 + 0.003·i: slope = b exactly, |r| = 1"
for hl in [6.0; 40.0] do
    let m = EwmaOlsMa hl
    for i in 0 .. 199 do m.Push (0.5 + 0.003 * float i)
    match m.State with
    | ValueSome (struct (s, r)) ->
        check (sprintf "hl=%-4.0f slope %.6f (want 0.003), r %.6f" hl s r)
            (abs (s - 0.003) <= 1e-9 && abs (abs r - 1.0) <= 1e-6)
    | ValueNone -> check "state present" false

printfn ""
if failures = 0 then printfn "ALL PASS" else printfn "%d FAILURES" failures; exit (min failures 1)
