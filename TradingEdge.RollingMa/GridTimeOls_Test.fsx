// ⭐ GridTimeOlsRoll oracle (FlushFaderV2 V2-47/V2-49; user, 2026-11-08). Three checks:
//   1. ORACLE — at every push (warmup included) the online slope equals a BRUTE-FORCE recompute: the window's bars into
//      their grid slots, slot vwap = Σ vwap·vol / Σ vol, a two-pass centered OLS of ln(slot vwap) on g × slotSecs.
//      Slot counts equal at every push.
//   2. RECOVERY — a price that is ln-linear in the SLOT's time (constant within each minute) reads that slope exactly.
//   3. CONTROL — the 60s slope is NOT the 1s time slope (TimeOlsSlopeMa) on the same bars: the slot choice matters.
// Run:  dotnet build TradingEdge.RollingMa -c Release && dotnet fsi TradingEdge.RollingMa/GridTimeOls_Test.fsx
#r "/home/mrakgr/Trading-Edge/research/TradingEdge.RollingMa/bin/Release/net10.0/TradingEdge.RollingMa.dll"
open TradingEdge.RollingMa
open System

let mutable failures = 0
let check label ok =
    if not ok then failures <- failures + 1
    printfn "  %s %s" (if ok then "✓" else "✗ FAIL") label

[<Struct>]
type B = { Sec: float; Vwap: float; Vol: float }

[<Sealed>]
type Grid(w, k) =
    inherit GridTimeOlsRoll<B>(w, k)
    override _.SecOf b = b.Sec
    override _.VolOf b = b.Vol
    override _.DvOf b = b.Vwap * b.Vol

let brute (bars: B[]) (k: int) =
    let groups = bars |> Array.groupBy (fun b -> int b.Sec / k)
    let x = groups |> Array.map (fun (g, _) -> float (g * k))
    let y = groups |> Array.map (fun (_, bs) -> log (Array.sumBy (fun b -> b.Vwap * b.Vol) bs / Array.sumBy (fun b -> b.Vol) bs))
    if x.Length < 2 then nan, x.Length
    else
        let mx, my = Array.average x, Array.average y
        Array.fold2 (fun s a c -> s + (a - mx) * (c - my)) 0.0 x y / Array.fold (fun s a -> s + (a - mx) * (a - mx)) 0.0 x, x.Length

let W, K, N = 1200, 60, 20_000
let rng = Random 20261108
let bars =
    let mutable sec = 0
    let mutable p = log 5.0
    Array.init N (fun _ ->
        sec <- sec + 1 + (if rng.NextDouble() < 0.5 then rng.Next 8 else 0)
        p <- p + 0.001 * (rng.NextDouble() - 0.5)
        { Sec = float sec; Vwap = float (float32 (exp p)); Vol = float (1 + rng.Next 5000) })

printfn "1. ORACLE: online vs brute force, window %d bars, %ds slots, %d pushes (last sec %.0f)" W K N bars.[N - 1].Sec
let g = Grid(W, K)
let ring = WindowRoller<B>([| W, [| g :> IRoll<B> |] |])
let mutable worst = 0.0
let mutable slotsOk = true
let mutable countOk = true
for i in 0 .. N - 1 do
    ring.Push bars.[i]
    let lo = max 0 (i - W + 1)
    let s, ns = brute bars.[lo .. i] K
    if g.Slots <> ns then slotsOk <- false
    if g.Count <> i - lo + 1 then countOk <- false
    match g.Slope with
    | ValueSome m when not (Double.IsNaN s) -> worst <- max worst (abs (m - s) * 6e5)
    | ValueNone when Double.IsNaN s -> ()
    | _ -> slotsOk <- false
printfn "     worst |error| %.2e bp/min" worst
check "slot count = brute force at every push" slotsOk
check "Count = the bars in the window at every push" countOk
check "|error| < 1e-6 bp/min at every push" (worst < 1e-6)

printfn "2. RECOVERY: ln price = 1.6 − 3e-6 · (slot start), constant within each minute"
let r = Grid(W, K)
let ring2 = WindowRoller<B>([| W, [| r :> IRoll<B> |] |])
for b in bars do ring2.Push { b with Vwap = exp (1.6 - 3e-6 * float ((int b.Sec / K) * K)) }
printfn "     slope %.10e" r.Slope.Value
check "slope = −3e-6 to 1e-8 relative" (abs (r.Slope.Value + 3e-6) / 3e-6 < 1e-8)

printfn "3. CONTROL: the 60s slope vs the 1s time slope on the last window"
let t = TimeOlsSlopeMa W
for b in bars do t.Push(b.Sec, log b.Vwap)
printfn "     60s %.6e  vs 1s %.6e" g.Slope.Value t.Slope.Value
check "they differ" (abs (g.Slope.Value - t.Slope.Value) > 1e-9)
check "fewer than 2 slots = ValueNone" ((Grid(W, K)).Slope = ValueNone)

printfn "\n%s" (if failures = 0 then "ALL PASS" else sprintf "%d FAILED" failures)
exit (if failures = 0 then 0 else 1)
