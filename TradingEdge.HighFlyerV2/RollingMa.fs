module TradingEdge.HighFlyerV2.RollingMa

open System
open System.Collections.Generic
open Nito.Collections

// =============================================================================
// Rolling-window aggregates (copied from TradingEdge.CryptoBacktest.RollingMa,
// trimmed to the three primitives this project needs, and changed so that
// reading `.State` on an EMPTY window throws instead of returning NaN — we
// don't want NaN sentinels leaking through the v1 indicator surface).
// =============================================================================
//
//   - RollingMa<'Bar,'State> — abstract base; subclass provides Add/Remove.
//   - SumMa — rolling sum.
//   - MaxMa / MinMa — sliding-window max/min via a monotonic deque,
//     amortized O(1) per push. Non-invertible.

[<AbstractClass>]
type RollingMa<'Bar, 'State>(initState: 'State, windowSize: int) =
    let q = Queue<'Bar>(windowSize)
    let mutable state = initState
    abstract member Add    : 'Bar * 'State -> 'State
    abstract member Remove : 'Bar * 'State -> 'State
    member _.Count = q.Count
    member _.WindowSize = windowSize
    /// Current aggregate, or ValueNone when the window is empty.
    member _.State = if q.Count = 0 then ValueNone else ValueSome state
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

/// Rolling average over a fixed-length window of floats. The base State holds
/// the running sum; this shadows it to expose the mean instead.
[<Sealed>]
type AvgMa(windowSize) =
    inherit RollingMa<float, float>(0.0, windowSize)
    override _.Add    (v, s) = s + v
    override _.Remove (v, s) = s - v
    member t.State = base.State |> ValueOption.map (fun sum -> sum / float t.Count)

// =============================================================================
// Sliding-window MaxMa / MinMa via a monotonic deque
// =============================================================================
//
// Maintain a deque of (value, barIdx) pairs in DECREASING value order (for
// max). On Push(x): evict the front if its barIdx fell out of the window, pop
// the back while back.value <= x, push (x, barIdx) at the back. The front is
// then always the current max. Mirror for min. Amortized O(1) per Push.

[<Sealed>]
type MaxMa(windowSize: int) =
    let dq = Deque<struct (float * int)>()
    let mutable barIdx = 0
    let mutable count = 0
    member _.Count = count
    member _.WindowSize = windowSize
    /// Current window max, or ValueNone when the window is empty.
    member _.State =
        if dq.Count = 0 then ValueNone
        else let struct (v, _) = dq.[0] in ValueSome v
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
    /// Current window min, or ValueNone when the window is empty.
    member _.State =
        if dq.Count = 0 then ValueNone
        else let struct (v, _) = dq.[0] in ValueSome v
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

/// Rolling MEAN over a CALENDAR-day interval (not a fixed bar count), matching
/// v0's `stock_volume_4w` window: `RANGE BETWEEN INTERVAL <days> DAYS PRECEDING
/// AND INTERVAL 1 DAY PRECEDING`. Bars are evicted by date, so the number of
/// bars in the window floats with holidays/weekends (≈19-20 over 28 days).
///
/// Usage mirrors the other structures: read `.State` BEFORE `Push`-ing the
/// current bar, so the mean covers strictly-prior days (no lookahead). The
/// `RANGE ... 1 DAY PRECEDING` upper bound is automatic here because the
/// current bar is only added after the snapshot read.
[<Sealed>]
type CalendarMeanMa(days: int) =
    let q = System.Collections.Generic.Queue<struct (DateOnly * float)>()
    let mutable sum = 0.0
    /// Count of bars currently in the window (after the last Evict).
    member _.Count = q.Count
    /// Current window mean, or ValueNone when the window is empty.
    member _.State =
        if q.Count = 0 then ValueNone else ValueSome (sum / float q.Count)
    /// Drop bars older than `days` calendar days before `asOf` (exclusive of
    /// `asOf` itself — the current bar hasn't been pushed yet).
    member _.Evict (asOf: DateOnly) =
        let cutoff = asOf.AddDays(-days)
        let mutable go = true
        while go && q.Count > 0 do
            let struct (d, v) = q.Peek()
            // keep bars with d > cutoff (strictly inside the 28-day lookback);
            // RANGE ... 28 DAYS PRECEDING is inclusive of the boundary day, so
            // evict only those strictly older than the cutoff.
            if d < cutoff then sum <- sum - v; q.Dequeue() |> ignore
            else go <- false
    member _.Push (d: DateOnly, v: float) =
        q.Enqueue(struct (d, v)); sum <- sum + v