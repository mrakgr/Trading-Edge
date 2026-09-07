#r "bin/Release/net10.0/TradingEdge.RollingMa.dll"
// ⭐ SlotVarRatioMa oracle — validated four ways:
//   1. ORACLE — at every completed slot, Ratio(k, lag) must equal the DIRECT
//      numpy-style computation (mean of per-slot population variances over the
//      population variance of the concatenated window) on the raw history.
//   2. BOUNDS — every reading in [0, 1]; shift invariance at +1e6.
//   3. LIMITS — i.i.d. noise around a level → ≈ 1 (a coil); a straight line
//      → ≈ 0 (a trend); a random walk → ≈ 1/k (the window-dependent reference).
//   4. WARM-UP — ValueNone until k + lag slots completed; constant tape ValueNone.
open System
open TradingEdge.RollingMa

let approx name a b tol =
    let e = abs (a - b) / max 1e-12 (abs b)
    printfn "  %-46s got %+12.6e  want %+12.6e   relerr %.2e %s" name a b e (if e <= tol then "OK" else "❌ FAIL")
    if e > tol then exit 1
let check name ok = printfn "  %-46s %s" name (if ok then "OK" else "❌ FAIL"); if not ok then exit 1
let vv o = match o with ValueSome x -> x | ValueNone -> nan

let popVar (xs: float[]) =
    let m = Array.average xs
    xs |> Array.averageBy (fun x -> (x - m) * (x - m))
/// direct oracle over the raw history: slots = consecutive slotLen blocks
let oracle (hist: float[]) slotLen k lag =
    let nSlots = hist.Length / slotLen
    if k + lag > nSlots then nan
    else
        let slots = [| for s in nSlots - lag - k .. nSlots - lag - 1 -> hist.[s * slotLen .. (s + 1) * slotLen - 1] |]
        let within = slots |> Array.averageBy popVar
        let total = popVar (Array.concat slots)
        if total <= 0.0 then nan else within / total

let rng = Random 7
let gauss () =
    let u1 = 1.0 - rng.NextDouble()
    let u2 = rng.NextDouble()
    sqrt (-2.0 * log u1) * cos (2.0 * Math.PI * u2)

printfn "1. ORACLE — direct computation at every completed slot, k in {3,7}, lag in {0,2}"
let slotLen = 5
let m = SlotVarRatioMa(slotLen, 12)
let hist = ResizeArray<float>()
let mutable worst = 0.0
let mutable px = 4.6
for i in 1 .. 400 do
    px <- px + 0.002 * gauss () + (if i % 90 < 45 then 0.0005 else -0.0005)
    m.Push px
    hist.Add px
    if hist.Count % slotLen = 0 then
        for k in [3; 7] do
            for lag in [0; 2] do
                let want = oracle (hist.ToArray()) slotLen k lag
                let got = vv (m.Ratio(k, lag))
                if Double.IsNaN want then check (sprintf "warm-up ValueNone at slot %d k%d lag%d" (hist.Count / slotLen) k lag) (Double.IsNaN got)
                else
                    let e = abs (got - want) / max 1e-12 want
                    worst <- max worst e
                    if e > 1e-9 then approx (sprintf "step %d k%d lag%d" i k lag) got want 1e-9
printfn "  worst relerr over the run: %.2e %s" worst (if worst <= 1e-9 then "OK" else "❌ FAIL")
if worst > 1e-9 then exit 1

printfn "2. BOUNDS + SHIFT INVARIANCE"
let m2 = SlotVarRatioMa(slotLen, 12)
let hist2 = hist.ToArray()
for x in hist2 do m2.Push (x + 1e6)
approx "Ratio(7,0) shifted by 1e6" (vv (m2.Ratio(7, 0))) (vv (m.Ratio(7, 0))) 1e-6
approx "Ratio(3,2) shifted by 1e6" (vv (m2.Ratio(3, 2))) (vv (m.Ratio(3, 2))) 1e-6
let m3 = SlotVarRatioMa(30, 40)
let mutable inb = true
let mutable p = 2.3
for i in 1 .. 20000 do
    p <- p + 0.001 * gauss ()
    m3.Push p
    for k in [6; 10; 20; 40] do
        match m3.Ratio(k, 0) with
        | ValueSome r when r < 0.0 || r > 1.0 -> inb <- false
        | _ -> ()
check "every reading in [0,1] over 20,000 random-walk bars" inb

printfn "3. LIMITS — noise ≈ 1, line ≈ 0, random walk ≈ 1/k (30-bar slots)"
let avgRatio (gen: int -> float) k =
    let mm = SlotVarRatioMa(30, 40)
    let acc = ResizeArray<float>()
    for i in 1 .. 60000 do
        mm.Push (gen i)
        if i % 30 = 0 then match mm.Ratio(k, 0) with ValueSome r -> acc.Add r | _ -> ()
    Seq.average acc
let noise = avgRatio (fun _ -> 0.001 * gauss ()) 20
printfn "  i.i.d. noise k=20: %.4f (want > 0.95)" noise; check "noise → ~1" (noise > 0.95)
let line = avgRatio (fun i -> 1e-5 * float i) 20
printfn "  straight line k=20: %.4f (want < 0.05)" line; check "line → ~0" (line < 0.05)
let mutable rw = 0.0
for k in [6; 10; 20; 40] do
    rw <- 0.0
    let r = avgRatio (fun _ -> rw <- rw + 0.001 * gauss (); rw) k
    printfn "  random walk k=%2d: %.4f  (1/k = %.4f)" k r (1.0 / float k)
    check (sprintf "random walk k=%d within [0.5/k, 2/k]" k) (r > 0.5 / float k && r < 2.0 / float k)

printfn "4. WARM-UP / CONSTANT"
let m4 = SlotVarRatioMa(5, 12)
for i in 1 .. 14 do m4.Push (float (i % 3))
check "ValueNone before 3 slots (14 pushes, k=3)" (m4.Ratio(3, 0).IsNone)
m4.Push 1.0
check "ValueSome after 3 slots (15 pushes, k=3)" (m4.Ratio(3, 0).IsSome)
check "ValueNone at k=3 lag=1 (needs 4 slots)" (m4.Ratio(3, 1).IsNone)
let m5 = SlotVarRatioMa(5, 12)
for i in 1 .. 20 do m5.Push 7.0
check "constant tape → ValueNone" (m5.Ratio(3, 0).IsNone)
printfn "ALL OK"
