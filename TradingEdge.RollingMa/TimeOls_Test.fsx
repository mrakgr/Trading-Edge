// ⭐ TimeOlsSlopeMa / TimeOlsRoll oracle (FlushFaderV2 V2-45; user, 2026-11-08). Four checks:
//   1. IDENTITY — fed x = 0, 1, 2, … the Ma form reproduces OlsSlopeMa BIT FOR BIT (same arithmetic, same order).
//   2. FORMS — the IRoll form on a WindowRoller equals the Ma form bit for bit on gappy integer x (and R²).
//   3. ORACLE — on gappy x (1s bars with silent seconds) both equal a two-pass CENTERED OLS of the window to 1e-9.
//   4. RECOVERY — a noiseless line y = a + b·x on gappy x reads slope b.
// Run:  dotnet build TradingEdge.RollingMa -c Release && dotnet fsi TradingEdge.RollingMa/TimeOls_Test.fsx
#r "/home/mrakgr/Trading-Edge/research/TradingEdge.RollingMa/bin/Release/net10.0/TradingEdge.RollingMa.dll"
open TradingEdge.RollingMa
open System

let mutable failures = 0
let check label ok =
    if not ok then failures <- failures + 1
    printfn "  %s %s" (if ok then "✓" else "✗ FAIL") label

[<Sealed>]
type PairOls(w) =
    inherit TimeOlsRoll<struct (float * float)>(w)
    override _.ProjectX (struct (x, _)) = x
    override _.Project (struct (_, y)) = y

let centered (xs: float[]) (ys: float[]) =
    let mx = Array.average xs
    let my = Array.average ys
    let mutable sxy = 0.0
    let mutable sxx = 0.0
    for i in 0 .. xs.Length - 1 do
        sxy <- sxy + (xs.[i] - mx) * (ys.[i] - my); sxx <- sxx + (xs.[i] - mx) * (xs.[i] - mx)
    sxy / sxx

let W = 1200
let N = 23_400
let rng = Random 20261108
// a log-price random walk around ln 5, and a session clock with silent seconds (P(present) 0.35)
let ys = Array.zeroCreate<float> N
let mutable p = log 5.0
for i in 0 .. N - 1 do
    p <- p + 0.001 * (rng.NextDouble() - 0.5); ys.[i] <- p
let xs = Array.zeroCreate<float> N
let mutable sec = 0
for i in 0 .. N - 1 do
    sec <- sec + 1
    while rng.NextDouble() > 0.35 do sec <- sec + 1
    xs.[i] <- float sec

printfn "1. IDENTITY: x = push index vs OlsSlopeMa (%d pushes, window %d)" N W
let a = OlsSlopeMa W
let b = TimeOlsSlopeMa W
let mutable same = true
let mutable sameR = true
for i in 0 .. N - 1 do
    a.Push ys.[i]; b.Push(float i, ys.[i])
    if a.State <> b.State then same <- false
    if a.R2 <> b.R2 then sameR <- false
check "slope bit for bit at every push" same
check "R² bit for bit at every push" sameR

printfn "2. FORMS: TimeOlsRoll on a WindowRoller vs TimeOlsSlopeMa, gappy x (last x %.0f s)" xs.[N - 1]
let ma = TimeOlsSlopeMa W
let ro = PairOls W
let roller = WindowRoller<struct (float * float)>([| W, [| ro :> IRoll<_> |] |])
let mutable formsSame = true
let mutable formsR = true
let mutable worst = 0.0         // |error| × 6e5 (bp/min, s20's unit)
let mutable worstIdx = 0.0      // the same for the INDEX OLS (OlsSlopeMa), the error production already carries
let idxOls = OlsSlopeMa W
for i in 0 .. N - 1 do
    ma.Push(xs.[i], ys.[i]); roller.Push(struct (xs.[i], ys.[i])); idxOls.Push ys.[i]
    if ma.State <> ro.State then formsSame <- false
    if ma.R2 <> ro.R2 then formsR <- false
    if i >= W - 1 && i % 7 = 0 then
        let lo = i - W + 1
        let o = centered xs.[lo .. i] ys.[lo .. i]
        worst <- max worst (abs (ma.State.Value - o) * 6e5)
        let oi = centered (Array.init W float) ys.[lo .. i]
        worstIdx <- max worstIdx (abs (idxOls.State.Value - oi) * 6e5)
    if i >= W - 1 && ma.Count <> W then formsSame <- false
check "slope bit for bit at every push" formsSame
check "R² bit for bit at every push" formsR
printfn "3. ORACLE: the running sums vs a two-pass centered OLS of each window (every 7th push)"
printfn "     worst |error| %.2e bp/min (time x, up to %.0f s); the index OLS's own: %.2e bp/min" worst xs.[N - 1] worstIdx
check "|error| < 1e-6 bp/min (s20's p10..p90 spans -54 .. -5)" (worst < 1e-6)

printfn "4. RECOVERY: y = 1.7 − 2.5e-6·x on the gappy clock"
let line = TimeOlsSlopeMa W
for i in 0 .. N - 1 do line.Push(xs.[i], 1.7 - 2.5e-6 * xs.[i])
let s = line.State.Value
printfn "     slope %.10e" s
check "slope = −2.5e-6 to 1e-9 relative" (abs (s + 2.5e-6) / 2.5e-6 < 1e-9)
check "fewer than 2 points = ValueNone" ((TimeOlsSlopeMa W).State = ValueNone)

printfn "\n%s" (if failures = 0 then "ALL PASS" else sprintf "%d FAILED" failures)
exit (if failures = 0 then 0 else 1)
