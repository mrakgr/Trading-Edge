#r "bin/Release/net10.0/TradingEdge.RollingMa.dll"
// ⭐ TimeSumMa / TimeLagMa / gap-aware EmaHlMa oracle (2026-08-27 clock review).
//   1. TimeSumMa ORACLE — on a random sparse stream (gaps 0..600 incl. halt-sized
//      jumps), the windowed sum must equal the brute-force sum over records whose
//      stamp is inside (clock − W, clock], warmth must equal clock >= W, and
//      Count must equal the records inside the window.
//   2. TimeLagMa ORACLE — Lagged must equal the newest value stamped <= clock − L.
//   3. DENSE EQUIVALENCE — with gapCount = 0 throughout, TimeSumMa == SumMa
//      (full-window states) and TimeLagMa == LagMa, exactly.
//   4. DecaySumMa.Mean — must match an EmaHlMa fed g explicit Push(0.0) calls
//      then Push(x) (num = α·Sum, den = α·w identity), incl. dense g = 0.
//   5. RATE SEMANTICS — steady state: one bar of volume V every (g+1) seconds
//      reads Mean ≈ V/(g+1) (volume per tradeable second), NOT V (per bar).
open System
open TradingEdge.RollingMa

let vv o = match o with ValueSome x -> x | ValueNone -> nan
let mutable failed = false
let check name ok =
    printfn "  %-64s %s" name (if ok then "OK" else "❌ FAIL")
    if not ok then failed <- true
let approx name (a: float) (b: float) tol =
    let e = abs (a - b) / max 1e-12 (abs b)
    check (sprintf "%s  got %+.6e want %+.6e relerr %.2e" name a b e) (e <= tol || (Double.IsNaN a && Double.IsNaN b))

// -------- shared random sparse stream: (gapCount, value) ---------------------
let rng = Random 11
let stream =
    Array.init 3000 (fun _ ->
        let g =
            match rng.Next(10) with
            | 0 | 1 | 2 | 3 -> 0                  // dense stretch
            | 4 | 5 | 6 -> rng.Next(1, 5)         // ordinary sparsity
            | 7 | 8 -> rng.Next(5, 60)            // thin tape
            | _ -> rng.Next(60, 600)              // halt-sized hole (NOT classified)
        struct (g, rng.NextDouble() * 1000.0))

printfn "1. TimeSumMa oracle — random sparse stream, every step, windows 60/300/1200"
for w in [60; 300; 1200] do
    let t = TimeSumMa w
    let hist = ResizeArray<struct (int * float)>()   // (stamp, value)
    let mutable clock = 0
    let mutable worst = 0.0
    let mutable warmBad, countBad = 0, 0
    for struct (g, v) in stream do
        clock <- clock + 1 + g
        hist.Add(struct (clock, v))
        t.Push(v, g)
        let inside = hist |> Seq.filter (fun (struct (c, _)) -> c > clock - w)
        let want = inside |> Seq.sumBy (fun (struct (_, x)) -> x)
        let wantN = inside |> Seq.length
        if t.Clock <> clock then warmBad <- warmBad + 1
        if (clock >= w) <> t.State.IsSome then warmBad <- warmBad + 1
        if t.Count <> wantN then countBad <- countBad + 1
        if t.State.IsSome then
            let e = abs (vv t.State - want) / max 1e-12 (abs want)
            if e > worst then worst <- e
    check (sprintf "W=%-5d worst relerr %.2e, warmth/clock mismatches %d, Count mismatches %d" w worst warmBad countBad)
          (worst <= 1e-9 && warmBad = 0 && countBad = 0)

printfn "2. TimeLagMa oracle — Lagged = newest value stamped <= clock − L, lags 60/300"
for lag in [60; 300] do
    let t = TimeLagMa lag
    let hist = ResizeArray<struct (int * float)>()
    let mutable clock = 0
    let mutable bad = 0
    for struct (g, v) in stream do
        clock <- clock + 1 + g
        hist.Add(struct (clock, v))
        t.Push(v, g)
        let want =
            hist |> Seq.filter (fun (struct (c, _)) -> c <= clock - lag)
                 |> Seq.tryLast |> Option.map (fun (struct (_, x)) -> x)
        let got = match t.Lagged with ValueSome x -> Some x | ValueNone -> None
        if got <> want then bad <- bad + 1
    check (sprintf "L=%-5d mismatches %d / %d" lag bad stream.Length) (bad = 0)

printfn "3. Dense equivalence — gapCount = 0 ⇒ TimeSumMa == SumMa, TimeLagMa == LagMa"
let denseVals = Array.init 500 (fun _ -> rng.NextDouble() * 1000.0)
let ts, bs = TimeSumMa 60, SumMa 60
let tl, bl = TimeLagMa 60, LagMa<float> 60
let mutable sumBad, lagBad = 0, 0
for v in denseVals do
    ts.Push(v, 0)
    bs.Push v
    tl.Push(v, 0)
    bl.Push v
    let bFull = if bs.Count = bs.WindowSize then bs.State else ValueNone
    match ts.State, bFull with
    | ValueSome a, ValueSome b when abs (a - b) > 1e-9 * max 1.0 (abs b) -> sumBad <- sumBad + 1
    | ValueSome _, ValueNone | ValueNone, ValueSome _ -> sumBad <- sumBad + 1
    | _ -> ()
    if tl.Lagged <> bl.Lagged then lagBad <- lagBad + 1
check (sprintf "sum mismatches %d, lag mismatches %d / %d" sumBad lagBad denseVals.Length)
      (sumBad = 0 && lagBad = 0)

printfn "4. DecaySumMa.Mean == EmaHlMa fed g explicit zeros then x (num = α·Sum, den = α·w)"
let hl = 1200.0
let a2, b2 = DecaySumMa hl, EmaHlMa hl
let mutable worstE = 0.0
for struct (g, v) in stream do
    a2.Push(v, g)
    for _ in 1 .. g do b2.Push 0.0
    b2.Push v
    let e = abs (vv a2.Mean - vv b2.State) / max 1e-12 (abs (vv b2.State))
    if e > worstE then worstE <- e
check (sprintf "worst relerr Mean vs explicit-zero EmaHlMa %.2e" worstE) (worstE <= 1e-12)
let c1, c2 = EmaHlMa 40.0, DecaySumMa 40.0
for v in denseVals |> Array.take 100 do c1.Push v; c2.Push(v, 0)
approx "dense Mean == per-push EmaHlMa after 100 pushes" (vv c2.Mean) (vv c1.State) 1e-12

printfn "5. Rate semantics — V=900 every (g+1)=10 secs at hl=1200: Mean ≈ 90/sec, not 900"
// reading right after the impulse overweights it by ~(g/2)·α (phase bias), so
// the read is ≈ 90 × 1.0026 at hl=1200 — loose 1% tolerance; the point is 90 vs 900.
let r = DecaySumMa 1200.0
for _ in 1 .. 20000 do r.Push(900.0, 9)
approx "steady-state rate" (vv r.Mean) 90.0 1e-2

printfn "6. DecaySumMa oracle — brute-force decayed sum on the sparse stream, hl 60/120"
for hl in [60.0; 120.0] do
    let d = DecaySumMa hl
    let hist = ResizeArray<struct (int * float)>()
    let mutable clock = 0
    let mutable worstD = 0.0
    for struct (g, v) in stream do
        clock <- clock + 1 + g
        hist.Add(struct (clock, v))
        d.Push(v, g)
        let want = hist |> Seq.sumBy (fun (struct (c, x)) -> x * 0.5 ** (float (clock - c) / hl))
        let e = abs (vv d.Sum - want) / max 1e-12 (abs want)
        if e > worstD then worstD <- e
    check (sprintf "hl=%-5.0f worst relerr %.2e" hl worstD) (worstD <= 1e-9)

printfn "7. Difference-of-exponentials speed — S120−S60 positive; prior-window vwap sane"
// constant price stream: any positive kernel must reproduce the price exactly.
let dv60, dv120 = DecaySumMa 60.0, DecaySumMa 120.0
let vl60, vl120 = DecaySumMa 60.0, DecaySumMa 120.0
let mutable negDiff = 0
let mutable worstPx = 0.0
let px = 3.5
for i, struct (g, v) in Seq.indexed stream do
    dv60.Push(px * v, g); dv120.Push(px * v, g)
    vl60.Push(v, g); vl120.Push(v, g)
    let dd = vv dv120.Sum - vv dv60.Sum
    let dl = vv vl120.Sum - vv vl60.Sum
    if dl < -1e-9 then negDiff <- negDiff + 1
    if i > 0 && dl > 1e-12 then          // first push: difference is exactly 0
        let e = abs (dd / dl - px) / px
        if e > worstPx then worstPx <- e
check (sprintf "negative vol-differences %d; worst prior-window vwap relerr %.2e" negDiff worstPx)
      (negDiff = 0 && worstPx <= 1e-9)

if failed then exit 1 else printfn "ALL OK"
