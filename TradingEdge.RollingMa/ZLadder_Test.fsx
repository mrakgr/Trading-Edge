#r "bin/Release/net10.0/TradingEdge.RollingMa.dll"
// ⭐ ZLadder / AnchoredZCount oracle (2026-09-02, the z-quantile feature).
//   1. Z ORACLE — ZLadder.Z must equal a brute-force (x − mean)/sd over the
//      trailing W values INCLUSIVE of the current bar, sample sd (n−1).
//   2. INDICATOR ORACLE — indicators must equal |z| > t elementwise, and be
//      all-zero while the window is cold.
//   3. ⭐ THE √3 WALL — on a pure linear ramp, z_last must equal
//      sqrt(3(n−1)/(n+1)) regardless of the drift SLOPE or INTERCEPT, and the
//      count above any t >= √3 must be exactly zero.
//   4. DRIFT FRACTION — over a long ramp the fraction of bars with |z| > t must
//      match the closed form max(0, 1 − t/√3).
//   5. ANCHORED — Count must equal the brute-force sum since the last Reset,
//      Rate = Count/Bars, and Rate must be nan below minBars.
open System
open TradingEdge.RollingMa

let mutable failed = false
let check name ok =
    printfn "  %-64s %s" name (if ok then "OK" else "❌ FAIL")
    if not ok then failed <- true
let approx name (a: float) (b: float) tol =
    let e = abs (a - b) / max 1e-12 (abs b)
    check (sprintf "%s  got %+.8f want %+.8f" name a b) (e <= tol || (Double.IsNaN a && Double.IsNaN b))

let ths = [| 1.0; 1.5; 1.7; 2.0; 2.5; 3.0 |]

// -------- 1 + 2: z and indicator oracle on a random walk ---------------------
printfn "\n1/2. Z + INDICATOR ORACLE (random walk, W=300)"
let rng = Random 7
let W = 300
let path = Array.init 5000 (fun _ -> rng.NextDouble() - 0.5) |> Array.scan (+) 0.0 |> Array.skip 1
let lad = ZLadder(W, ths)
let mutable zbad = 0
let mutable ibad = 0
let mutable coldbad = 0
for i in 0 .. path.Length - 1 do
    lad.Push path.[i]
    let lo = max 0 (i - W + 1)
    let win = path.[lo .. i]
    let n = win.Length
    if n >= 2 then
        let m = Array.average win
        let sd = sqrt ((win |> Array.sumBy (fun x -> (x - m) * (x - m))) / float (n - 1))
        let want = if sd > 0.0 then (path.[i] - m) / sd else nan
        if not (Double.IsNaN want) then
            if abs (lad.Z - want) > 1e-9 * max 1.0 (abs want) then zbad <- zbad + 1
        for k in 0 .. ths.Length - 1 do
            let wi = if abs want > ths.[k] then 1.0 else 0.0
            if lad.Indicators.[k] <> wi then ibad <- ibad + 1
    else
        if not (Double.IsNaN lad.Z) then coldbad <- coldbad + 1
        if lad.Indicators |> Array.exists (fun v -> v <> 0.0) then coldbad <- coldbad + 1
check (sprintf "Z matches brute force on all %d bars (%d mismatches)" path.Length zbad) (zbad = 0)
check (sprintf "indicators match |z|>t elementwise (%d mismatches)" ibad) (ibad = 0)
check (sprintf "cold window -> nan z, all-zero indicators (%d violations)" coldbad) (coldbad = 0)

// -------- 3: the √3 wall -----------------------------------------------------
printfn "\n3. THE √3 WALL — z_last on a pure ramp, any slope/intercept"
for (slope, intercept) in [ (1.0, 0.0); (0.001, 500.0); (-3.7, -20.0); (1e6, 1e9) ] do
    for n in [ 10; 60; 300; 1200 ] do
        let l = ZLadder(n, ths)
        for i in 0 .. n * 3 do l.Push (intercept + slope * float i)
        // SAMPLE sd (n−1), matching WinStdMa: z_last = (n−1)·√(3/(n(n+1))).
        let want = float (n - 1) * sqrt (3.0 / (float n * float (n + 1)))
        // a NEGATIVE slope puts the newest bar at the window MINIMUM -> z = −want
        approx (sprintf "ramp slope=%g n=%d" slope n) (abs l.Z) want 1e-9
printfn "   (independent of slope AND intercept — z is scale/shift free)"

let wall = sqrt 3.0
printfn "\n   count above t >= √3 must be EXACTLY zero on a ramp:"
let lw = ZLadder(300, ths)
let mutable above = 0
for i in 0 .. 4000 do
    lw.Push (float i * 0.37 + 11.0)
    for k in 0 .. ths.Length - 1 do
        if ths.[k] >= wall && lw.Indicators.[k] > 0.0 then above <- above + 1
check (sprintf "no exceedance above the wall on a pure ramp (%d violations)" above) (above = 0)

// -------- 4: the closed-form drift fraction ----------------------------------
printfn "\n4. ROLLING-RAMP FRACTION — every warm bar shares one z, so 0/1 about it"
printfn "   (⚠ NOT max(0,1−t/√3): that is the spread WITHIN one static window,"
printfn "    across positions i=0..n−1 — a different statistic entirely.)"
let l4 = ZLadder(300, ths)
let hits = Array.zeroCreate<int> ths.Length
let mutable warm = 0
for i in 0 .. 20000 do
    l4.Push (float i * 2.5 + 3.0)
    if i >= 299 then
        warm <- warm + 1
        for k in 0 .. ths.Length - 1 do
            if l4.Indicators.[k] > 0.0 then hits.[k] <- hits.[k] + 1
let zRamp = float (300 - 1) * sqrt (3.0 / (300.0 * 301.0))
for k in 0 .. ths.Length - 1 do
    let got = float hits.[k] / float warm
    let want = if ths.[k] < zRamp then 1.0 else 0.0
    check (sprintf "t=%.1f frac got %.4f want %.4f (z_ramp=%.4f)" ths.[k] got want zRamp)
          (abs (got - want) < 1e-9)
// and the STATIC-window spread, which IS the max(0,1−t/√3) law
printfn "\n4b. STATIC-WINDOW SPREAD — one ramp window, across its own positions"
let xs = Array.init 300 float
let m = Array.average xs
let sd = sqrt ((xs |> Array.sumBy (fun x -> (x - m) * (x - m))) / 299.0)
for t in ths do
    let got = (xs |> Array.filter (fun x -> abs ((x - m) / sd) > t) |> Array.length |> float) / 300.0
    let want = max 0.0 (1.0 - t / wall)
    check (sprintf "t=%.1f static frac got %.4f want %.4f" t got want) (abs (got - want) < 0.01)

// -------- 5: anchored counters ----------------------------------------------
printfn "\n5. ANCHORED — count / rate / span / fail-closed floor"
let minBars = 30
let anc = AnchoredZCount(ths.Length, minBars)
let l5 = ZLadder(300, ths)
let rng5 = Random 3
let mutable bruteCnt = Array.zeroCreate<float> ths.Length
let mutable bruteBars = 0
let mutable abad = 0
let mutable ratebad = 0
for i in 0 .. 6000 do
    l5.Push (float i * 0.01 + (rng5.NextDouble() - 0.5) * 3.0)
    if rng5.NextDouble() < 0.004 then          // random "new low" anchor events
        anc.Reset(); bruteCnt <- Array.zeroCreate ths.Length; bruteBars <- 0
    anc.Push l5.Indicators
    bruteBars <- bruteBars + 1
    for k in 0 .. ths.Length - 1 do bruteCnt.[k] <- bruteCnt.[k] + l5.Indicators.[k]
    if anc.Bars <> bruteBars then abad <- abad + 1
    for k in 0 .. ths.Length - 1 do
        if anc.Count k <> bruteCnt.[k] then abad <- abad + 1
        let r = anc.Rate k
        if bruteBars < minBars then
            if not (Double.IsNaN r) then ratebad <- ratebad + 1
        elif abs (r - bruteCnt.[k] / float bruteBars) > 1e-12 then ratebad <- ratebad + 1
check (sprintf "count + span match brute force across resets (%d mismatches)" abad) (abad = 0)
check (sprintf "rate = count/bars, nan below minBars=%d (%d violations)" minBars ratebad) (ratebad = 0)

printfn "\n%s" (if failed then "❌ SOME CHECKS FAILED" else "✅ ALL CHECKS PASSED")
exit (if failed then 1 else 0)
