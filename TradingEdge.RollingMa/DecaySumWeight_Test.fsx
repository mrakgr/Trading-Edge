// S35 (user, 2026-08-29): DecaySumMa GapValue-mode oracle. The gap seconds are
// real time; the mode declares what value the quantity had during them, and
// Sum/Weight must update CONSISTENTLY. Brute-force per-second oracles:
//   Zero:  value 0 at gap secs, weight 1 EVERY second   (volume/tc rates)
//   Empty: no observation at gap secs — pushes only     (bar-clock prices)
//   Locf:  last value at gap secs, weight 1 EVERY second (time-clock TWAP;
//          first push carries nothing — its gapCount is ignored)
// Also pins the S35 finding that motivated the modes: the Zero weight and the
// Empty weight are the SAME number on gap-0 streams and 17× apart on sparse
// tape — mixing them across num/den reads a "mean price" below every print.
#r "/home/mrakgr/Trading-Edge/research/TradingEdge.RollingMa/bin/Release/net10.0/TradingEdge.RollingMa.dll"
open TradingEdge.RollingMa
open System

let mutable failures = 0
let check label ok =
    if not ok then failures <- failures + 1
    printfn "  %s %s" (if ok then "✓" else "✗ FAIL") label

let vv (o: float voption) = match o with ValueSome v -> v | ValueNone -> nan

let run (label: string) (gaps: int[]) =
    printfn "%s:" label
    let rng = Random 42
    let stream = gaps |> Array.map (fun g -> struct (g, 1.0 + rng.NextDouble() * 99.0))
    let hl = 60.0
    let lam = 0.5 ** (1.0 / hl)
    for mode in [GapValue.Zero; GapValue.Empty; GapValue.Locf] do
        let m = DecaySumMa(hl, mode)
        // oracle: the declared path as an explicit (age-decayed) observation list
        let mutable oS = 0.0     // Σ λ^age · value over the declared observations
        let mutable oW = 0.0     // Σ λ^age over the declared observations
        let mutable last = 0.0
        let mutable first = true
        let mutable worstS = 0.0
        let mutable worstW = 0.0
        for struct (g, x) in stream do
            let d = lam ** (1.0 + float g)
            let gapW = Seq.init g (fun k -> lam ** float (k + 1)) |> Seq.sum   // λ¹+…+λᵍ
            match mode with
            | GapValue.Zero ->
                oS <- d * oS + x
                oW <- d * oW + gapW + 1.0
            | GapValue.Empty ->
                oS <- d * oS + x
                oW <- d * oW + 1.0
            | GapValue.Locf ->
                if first then oS <- x; oW <- 1.0
                else
                    oS <- d * oS + last * gapW + x
                    oW <- d * oW + gapW + 1.0
            m.Push(x, g)
            last <- x
            first <- false
            worstS <- max worstS (abs (vv m.Sum - oS) / max 1e-12 (abs oS))
            worstW <- max worstW (abs (vv m.Weight - oW) / max 1e-12 oW)
        check (sprintf "%-5A Sum    worst relerr %.2e" mode worstS) (worstS <= 1e-11)
        check (sprintf "%-5A Weight worst relerr %.2e" mode worstW) (worstW <= 1e-11)
    // mode-consistency invariants on this stream
    let z, e, l = DecaySumMa(hl, GapValue.Zero), DecaySumMa(hl, GapValue.Empty), DecaySumMa(hl, GapValue.Locf)
    let rng2 = Random 7
    let mutable anyGap = false
    for struct (g, _) in stream do
        let x = 1.0 + rng2.NextDouble() * 99.0
        z.Push(x, g); e.Push(x, g); l.Push(x, g)
        if g > 0 then anyGap <- true
    if not anyGap then
        check "gap-0: all three modes coincide (Sum & Weight)"
            (abs (vv z.Sum - vv e.Sum) = 0.0 && abs (vv z.Sum - vv l.Sum) = 0.0
             && abs (vv z.Weight - vv e.Weight) = 0.0 && abs (vv z.Weight - vv l.Weight) = 0.0)
    else
        printfn "  weight ratio Zero/Empty = %.4f (sparse: the two weight families diverge)"
            (vv z.Weight / vv e.Weight)
        check "sparse: Zero.Weight > Empty.Weight" (vv z.Weight > vv e.Weight)
        check "sparse: Locf.Weight == Zero.Weight (both weight every second)"
            (abs (vv l.Weight - vv z.Weight) / vv z.Weight <= 1e-12)
    // LOCF sanity: constant price + any gaps → Mean must be exactly that price
    let c = DecaySumMa(hl, GapValue.Locf)
    for struct (g, _) in stream do c.Push(42.0, g)
    check (sprintf "Locf constant-price Mean == 42 (got %.12f)" (vv c.Mean))
        (abs (vv c.Mean - 42.0) <= 1e-9)

run "DENSE (gap=0 always)" (Array.create 500 0)
run "SPARSE (gaps 0..30)" (let r = Random 1 in Array.init 500 (fun _ -> r.Next 31))
run "HALT-STYLE (rare 300s holes)" (let r = Random 2 in Array.init 500 (fun i -> if i % 97 = 0 then 300 else r.Next 3))

printfn ""
if failures = 0 then printfn "ALL PASS" else printfn "%d FAILURES" failures; exit (min failures 1)
