module TradingEdge.CryptoBacktest.RollingMa

open System.Collections.Generic
open Nito.Collections

// `Queue<'T>` is used by the RollingMa base; `Deque<'T>` from Nito by
// the monotonic-deque MaxMa / MinMa.

// =============================================================================
// Rolling-window aggregates
// =============================================================================
//
// Invertible-aggregate base + four sealed primitives:
//   - RollingMa<'Bar, 'State> — abstract base; subclass provides Add /
//     Remove. Use for sums and sum-of-squares.
//   - SumMa  — rolling sum.
//   - StdMa  — rolling sample standard deviation (Welford via ΣX, ΣX²).
//   - MaxMa / MinMa — sliding-window max / min via a monotonic deque,
//     amortized O(1) per push. Non-invertible.

[<AbstractClass>]
type RollingMa<'Bar, 'State>(initState: 'State, windowSize: int) =
    let q = Queue<'Bar>(windowSize)
    let mutable state = initState
    abstract member Add    : 'Bar * 'State -> 'State
    abstract member Remove : 'Bar * 'State -> 'State
    member _.Count = q.Count
    member _.WindowSize = windowSize
    member _.State = state
    member this.Push (x: 'Bar) =
        if q.Count = windowSize then
            state <- this.Remove (q.Dequeue(), state)
        q.Enqueue x
        state <- this.Add (x, state)

/// Rolling sum over a fixed-length window of floats. State IS the sum.
[<Sealed>]
type SumMa(windowSize) =
    inherit RollingMa<float, float>(0.0, windowSize)
    override _.Add    (v, s) = s + v
    override _.Remove (v, s) = s - v

/// Rolling sample-std over a fixed-length window of floats. State holds
/// (ΣX, ΣX²) as a struct tuple to avoid heap allocation per Push. The
/// SampleStd reader collapses that into the sample standard deviation
/// using Welford's algebraic identity:
///     var = (ΣX² − m·mean²) / (m − 1)
[<Sealed>]
type StdMa(windowSize) =
    inherit RollingMa<float, struct (float * float)>(struct (0.0, 0.0), windowSize)
    override _.Add    (v, struct (sx, sx2)) = struct (sx + v, sx2 + v * v)
    override _.Remove (v, struct (sx, sx2)) = struct (sx - v, sx2 - v * v)
    member this.SampleStd =
        let m = this.Count
        if m < 2 then 0.0
        else
            let struct (sumX, sumX2) = this.State
            let mean = sumX / float m
            let v = (sumX2 - float m * mean * mean) / float (m - 1)
            if v <= 0.0 then 0.0 else sqrt v

// =============================================================================
// Sliding-window MaxMa / MinMa via a monotonic deque
// =============================================================================
//
// The RollingMa primitive above requires an invertible aggregate (sum,
// sum-of-squares) — which doesn't fit max/min, since you can't "subtract"
// the maximum.
//
// Algorithm: maintain a deque of (value, barIdx) pairs in DECREASING value
// order (for max). On Push(x):
//   1. Evict the front if its barIdx has fallen out of the window.
//   2. Pop from the back while the back's value <= x — those candidates
//      can never be the max of any window containing x.
//   3. Push (x, barIdx) at the back.
// The front of the deque is then always the current max. Mirror for min.
//
// Cost: amortized O(1) per Push (each value enters and leaves at most once).
// Backed by Nito.Collections.Deque<T>, a circular-buffer deque with no
// per-node heap allocation.

[<Sealed>]
type MaxMa(windowSize: int) =
    let dq = Deque<struct (float * int)>()
    let mutable barIdx = 0
    let mutable count = 0
    member _.Count = count
    member _.WindowSize = windowSize
    member _.State =
        if dq.Count = 0 then nan
        else let struct (v, _) = dq.[0] in v
    member _.Push (x: float) =
        let cutoff = barIdx - windowSize + 1
        while dq.Count > 0 &&
              (let struct (_, i) = dq.[0] in i < cutoff) do
            dq.RemoveFromFront() |> ignore
        while dq.Count > 0 &&
              (let struct (v, _) = dq.[dq.Count - 1] in v <= x) do
            dq.RemoveFromBack() |> ignore
        dq.AddToBack(struct (x, barIdx))
        barIdx <- barIdx + 1
        count <- min windowSize (count + 1)

[<Sealed>]
type MinMa(windowSize: int) =
    let dq = Deque<struct (float * int)>()
    let mutable barIdx = 0
    let mutable count = 0
    member _.Count = count
    member _.WindowSize = windowSize
    member _.State =
        if dq.Count = 0 then nan
        else let struct (v, _) = dq.[0] in v
    member _.Push (x: float) =
        let cutoff = barIdx - windowSize + 1
        while dq.Count > 0 &&
              (let struct (_, i) = dq.[0] in i < cutoff) do
            dq.RemoveFromFront() |> ignore
        while dq.Count > 0 &&
              (let struct (v, _) = dq.[dq.Count - 1] in v >= x) do
            dq.RemoveFromBack() |> ignore
        dq.AddToBack(struct (x, barIdx))
        barIdx <- barIdx + 1
        count <- min windowSize (count + 1)
