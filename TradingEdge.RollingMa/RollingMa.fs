module TradingEdge.RollingMa

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
    /// Drop every buffered bar and return to the initial aggregate — used to
    /// sever a >45-day listing gap so a recycled ticker's new episode starts cold.
    member _.Reset () =
        q.Clear()
        state <- initState

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
    /// Clear the window (see RollingMa.Reset). barIdx must reset too — the deque
    /// eviction cutoff is barIdx-windowSize+1, so a stale barIdx keeps the old horizon.
    member _.Reset () =
        dq.Clear()
        barIdx <- 0
        count <- 0

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
    /// Clear the window (see RollingMa.Reset). barIdx must reset too — the deque
    /// eviction cutoff is barIdx-windowSize+1, so a stale barIdx keeps the old horizon.
    member _.Reset () =
        dq.Clear()
        barIdx <- 0
        count <- 0

/// Fixed-DELAY line (reused verbatim from TradingEdge.LowFlyer/RollingMa.fs): a ring
/// of the last (lag+1) values — `.Lagged` is the value `lag` bars ago, `.Last` the
/// current. Push the bar, then read `lagPctChange` for an N-bar return. Empty until
/// `lag+1` values have been pushed.
[<Sealed>]
type LagMa<'T>(lag: int) =
    let q = Queue<'T>(lag + 1)
    let mutable last : 'T voption = ValueNone
    member _.Count = q.Count
    /// The most recent pushed value, or ValueNone before the first push.
    member _.Last = last
    /// The value `lag` bars ago, or ValueNone until `lag+1` values have been pushed.
    member _.Lagged = if q.Count = lag + 1 then ValueSome (q.Peek()) else ValueNone
    member _.Push (x: 'T) =
        if q.Count = lag + 1 then q.Dequeue() |> ignore
        q.Enqueue x
        last <- ValueSome x
    member _.Reset () =
        q.Clear()
        last <- ValueNone

/// %-change from a float LagMa's `lag`-bars-ago value to its most recent push
/// (curr/lagged - 1), or ValueNone until warm / when the lagged value is non-positive.
let lagPctChange (m: LagMa<float>) : float voption =
    match m.Last, m.Lagged with
    | ValueSome curr, ValueSome old when old > 0.0 -> ValueSome (curr / old - 1.0)
    | _ -> ValueNone

/// Exponential moving average — a RECURSIVE accumulator, NOT a windowed structure:
/// `ema = α·x + (1−α)·ema_prev`, with `α = 2/(period+1)` (the standard EMA smoothing).
/// Seeded with the first pushed value (the conventional cold-start; no SMA warm-up), so
/// `.State` is defined from the FIRST push. Read `.State` BEFORE `Push`-ing the current
/// bar for the strictly-prior (no-lookahead) value, exactly like the RollingMa types.
[<Sealed>]
type EmaMa(period: int) =
    let alpha = 2.0 / (float period + 1.0)
    let mutable ema : float voption = ValueNone
    /// The current EMA, or ValueNone before the first push.
    member _.State = ema
    member _.Push (x: float) =
        ema <-
            match ema with
            | ValueSome prev -> ValueSome (alpha * x + (1.0 - alpha) * prev)
            | ValueNone      -> ValueSome x     // seed with the first value
    member _.Reset () = ema <- ValueNone

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
    /// Clear the window (see RollingMa.Reset).
    member _.Reset () =
        q.Clear()
        sum <- 0.0

// =============================================================================
// OlsSlopeMa — rolling ordinary-least-squares regression slope (+ R²)
// =============================================================================
//
// The least-squares line y = m·x + b through the last `windowSize` pushed
// points, minimizing Σ(y - (m·x + b))². The x-coordinate of each point is its
// ABSOLUTE push index (0, 1, 2, …). The slope is invariant to a constant shift
// of all x (only the spacing matters), so using the absolute index — rather than
// re-basing to 0..n-1 on every slide — lets eviction be a clean subtract of the
// oldest point's exact contribution. Amortized O(1) per push.
//
// Closed form (n points, no per-point weights):
//   slope m = (n·Σxy − Σx·Σy) / (n·Σx² − (Σx)²)
//   R²      = (n·Σxy − Σx·Σy)² / [ (n·Σx² − (Σx)²)·(n·Σy² − (Σy)²) ]
// The denominators are n·Var(x) and n·Var(y) up to the same factor; both are the
// window's x/y spread. `Slope` is defined once ≥2 points span a non-degenerate x
// range (always true for distinct push indices, i.e. n≥2). `R2` additionally
// needs a non-degenerate y range (a perfectly flat window has undefined R²).
//
// UNITS: the slope is y-per-BAR (per push). Feed log-price to get a %-per-bar
// (log-return-per-bar) trend that's comparable across tickers — the intended use
// for a trend feature, same rationale as log-ATR. Read `.State`/`.R2` BEFORE
// pushing the current bar for the strictly-prior (no-lookahead) value, exactly
// like the other structures here.
[<Sealed>]
type OlsSlopeMa(windowSize: int) =
    let q = Queue<struct (float * float)>(windowSize)   // (x = absolute push index, y)
    let mutable sx  = 0.0    // Σx
    let mutable sy  = 0.0    // Σy
    let mutable sxx = 0.0    // Σx²
    let mutable sxy = 0.0    // Σxy
    let mutable syy = 0.0    // Σy²
    let mutable idx = 0.0    // next absolute push index (never reset within an episode)

    /// Count of points currently in the window.
    member _.Count = q.Count
    member _.WindowSize = windowSize

    /// Denominator n·Σx² − (Σx)² = n·(spread of x). >0 once ≥2 distinct-x points.
    member private _.Sxx = float q.Count * sxx - sx * sx

    /// OLS slope (y-per-bar), or ValueNone with <2 points (x range degenerate).
    member this.Slope : float voption =
        let n = float q.Count
        let dxx = n * sxx - sx * sx
        if q.Count >= 2 && dxx > 0.0 then ValueSome ((n * sxy - sx * sy) / dxx)
        else ValueNone

    /// The slope, exposed as `.State` to match the other RollingMa structures.
    member this.State = this.Slope

    /// Coefficient of determination R² ∈ [0,1] — the fraction of y-variance the
    /// line explains (trend cleanliness). ValueNone with <2 points or a flat
    /// window (Σy spread = 0, R² undefined).
    member _.R2 : float voption =
        let n = float q.Count
        let dxx = n * sxx - sx * sx
        let dyy = n * syy - sy * sy
        if q.Count >= 2 && dxx > 0.0 && dyy > 0.0 then
            let dxy = n * sxy - sx * sy
            ValueSome (dxy * dxy / (dxx * dyy))
        else ValueNone

    /// Push the next y (its x is the running absolute index). Evicts the oldest
    /// point when the window is full, subtracting its exact contribution.
    member _.Push (y: float) =
        if q.Count = windowSize then
            let struct (ox, oy) = q.Dequeue()
            sx <- sx - ox; sy <- sy - oy
            sxx <- sxx - ox * ox; sxy <- sxy - ox * oy; syy <- syy - oy * oy
        let x = idx
        idx <- idx + 1.0
        q.Enqueue(struct (x, y))
        sx <- sx + x; sy <- sy + y
        sxx <- sxx + x * x; sxy <- sxy + x * y; syy <- syy + y * y

    /// Drop every buffered point and return cold — including the x index, so a
    /// fresh episode starts its regression at x=0 (see RollingMa.Reset).
    member _.Reset () =
        q.Clear()
        sx <- 0.0; sy <- 0.0; sxx <- 0.0; sxy <- 0.0; syy <- 0.0
        idx <- 0.0