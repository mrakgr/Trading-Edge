module TradingEdge.RollingMa

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
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

/// CUMULATIVE weighted-mean accumulator: `.State = Σnum / Σden`, with `num`/`den`
/// supplied per push. NOT a fixed window — it accumulates over the whole episode
/// (like EmaMa/CalendarMeanMa, no windowSize), so it never evicts. The motivating
/// use is session VWAP: push `(typical·volume, volume)` each bar, read `.State` for
/// `Σ(tp·v)/Σv`. `.State` is ValueNone until a POSITIVE denominator has accumulated
/// (Σden > 0), matching "no VWAP before any volume". Read `.State` BEFORE pushing the
/// current bar for the strictly-prior value, or AFTER for the live/inclusive value —
/// same convention as the other structures here.
[<Sealed>]
type RatioMa() =
    let mutable num = 0.0    // Σ numerator
    let mutable den = 0.0    // Σ denominator
    /// Current ratio Σnum/Σden, or ValueNone until Σden > 0.
    member _.State = if den > 0.0 then ValueSome (num / den) else ValueNone
    /// Accumulate one (numerator, denominator) contribution.
    member _.Push (n: float, d: float) =
        num <- num + n
        den <- den + d
    /// Reset both accumulators to zero (see RollingMa.Reset).
    member _.Reset () =
        num <- 0.0
        den <- 0.0

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

// =============================================================================
// MaxMaMeta — MaxMa carrying a per-value METADATA payload
// =============================================================================
//
// ⭐ S43ai (user): a plain MaxMa returns the window max but DISCARDS everything
// else about the bar that set it. Carrying a payload alongside each value lets
// us recover the feature state AS OF the extreme — e.g. eff_20m / eff_10m at
// the ARMING HIGH, which measures the smoothness of the trend INTO the high
// (the drop out of it is already covered by olsSinceHigh / effSinceHigh).
//
// Identical monotonic-deque mechanics to MaxMa; the deque holds
// (value, barIdx, meta). ⚠ TIE CONVENTION: the back-pop test is `<=`, so among
// EQUAL maxima the LATEST bar's metadata survives — the same tie-break MaxMa
// already uses for its own barIdx.

[<Sealed>]
type MaxMaMeta<'M>(windowSize: int) =
    let dq = Deque<struct (float * int * 'M)>()
    let mutable barIdx = 0
    let mutable count = 0
    member _.Count = count
    member _.WindowSize = windowSize
    /// Current window max, or ValueNone when the window is empty.
    member _.State =
        if dq.Count = 0 then ValueNone
        else let struct (v, _, _) = dq.[0] in ValueSome v
    /// Metadata of the bar that SET the current window max.
    member _.StateMeta =
        if dq.Count = 0 then ValueNone
        else let struct (_, _, m) = dq.[0] in ValueSome m
    member _.Push (x: float, meta: 'M) =
        let cutoff = barIdx - windowSize + 1
        while dq.Count > 0 &&
              (let struct (_, i, _) = dq.[0] in i < cutoff) do
            dq.RemoveFromFront() |> ignore
        while dq.Count > 0 &&
              (let struct (v, _, _) = dq.[dq.Count - 1] in v <= x) do
            dq.RemoveFromBack() |> ignore
        dq.AddToBack(struct (x, barIdx, meta))
        barIdx <- barIdx + 1
        count <- min windowSize (count + 1)
    /// Clear the window (see MaxMa.Reset — barIdx must reset too).
    member _.Reset () =
        dq.Clear()
        barIdx <- 0
        count <- 0

/// The MIN twin of MaxMaMeta — identical deque mechanics with the comparison
/// flipped (back-pop test `>=`, so among EQUAL minima the LATEST bar's metadata
/// survives, the same tie-break MinMa uses). Added 2026-08-26 for SpikeFader's
/// arming-LOW eff snapshot (the mirror of FlushFader's arming-high one).
[<Sealed>]
type MinMaMeta<'M>(windowSize: int) =
    let dq = Deque<struct (float * int * 'M)>()
    let mutable barIdx = 0
    let mutable count = 0
    member _.Count = count
    member _.WindowSize = windowSize
    /// Current window min, or ValueNone when the window is empty.
    member _.State =
        if dq.Count = 0 then ValueNone
        else let struct (v, _, _) = dq.[0] in ValueSome v
    /// Metadata of the bar that SET the current window min.
    member _.StateMeta =
        if dq.Count = 0 then ValueNone
        else let struct (_, _, m) = dq.[0] in ValueSome m
    member _.Push (x: float, meta: 'M) =
        let cutoff = barIdx - windowSize + 1
        while dq.Count > 0 &&
              (let struct (_, i, _) = dq.[0] in i < cutoff) do
            dq.RemoveFromFront() |> ignore
        while dq.Count > 0 &&
              (let struct (v, _, _) = dq.[dq.Count - 1] in v >= x) do
            dq.RemoveFromBack() |> ignore
        dq.AddToBack(struct (x, barIdx, meta))
        barIdx <- barIdx + 1
        count <- min windowSize (count + 1)
    /// Clear the window (see MaxMa.Reset — barIdx must reset too).
    member _.Reset () =
        dq.Clear()
        barIdx <- 0
        count <- 0

// =============================================================================
// RunMaxMa / RunMinMa — session-cumulative running extreme (NOT windowed)
// =============================================================================
//
// The running max (or min) over EVERY value pushed since the last Reset — a
// windowless MaxMa/MinMa. Replaces the plain `mutable _ voption` running-extreme
// idiom used for session highs/lows/volume-highs in the intraday engines: a
// value goes in with `.Push`, the current extreme reads from `.State`, and
// `.Reset` clears it (e.g. severing a session boundary, or a VWAP cross).
// Generic over any comparable type, so it covers both float prices and int64
// volumes. Read `.State` BEFORE pushing the current bar for the strictly-prior
// value, or AFTER for the inclusive one — same convention as the other structures.

[<Sealed>]
type RunMaxMa<'T when 'T: comparison>() =
    let mutable s : 'T voption = ValueNone
    /// The running max since the last Reset, or ValueNone before any push.
    member _.State = s
    member _.Push (x: 'T) =
        s <- match s with ValueSome c -> ValueSome (max c x) | ValueNone -> ValueSome x
    /// Clear the running extreme (see RollingMa.Reset).
    member _.Reset () = s <- ValueNone

[<Sealed>]
type RunMinMa<'T when 'T: comparison>() =
    let mutable s : 'T voption = ValueNone
    /// The running min since the last Reset, or ValueNone before any push.
    member _.State = s
    member _.Push (x: 'T) =
        s <- match s with ValueSome c -> ValueSome (min c x) | ValueNone -> ValueSome x
    /// Clear the running extreme (see RollingMa.Reset).
    member _.Reset () = s <- ValueNone

/// CUMULATIVE mean / standard-deviation accumulator (Welford's online algorithm).
/// NOT a fixed window — it accumulates over the whole session (like RatioMa/EmaMa),
/// so it never evicts. The motivating use is the VWAP-distance z-score: push
/// `close/vwap - 1` each bar, read `.Z x` for how many σ a value sits from the
/// session mean. Welford rather than Σx/Σx² because the naive form loses precision
/// catastrophically when the mean is large relative to the variance — exactly the
/// case here (dist_vwap values cluster tightly near 0).
///
/// `.Mean`/`.Std` are ValueNone until 2 values have been pushed (σ is undefined for
/// n<2). `.Std` is the SAMPLE deviation (n−1 denominator). Read BEFORE pushing the
/// current bar for the strictly-prior value, or AFTER for the inclusive one — same
/// convention as the other structures here.
[<Sealed>]
type CumStdMa() =
    let mutable n = 0
    let mutable mean = 0.0
    let mutable m2 = 0.0            // Σ (x − mean)² , maintained incrementally
    /// Count of values pushed since the last Reset.
    member _.Count = n
    /// The running mean, or ValueNone before any push.
    member _.Mean = if n > 0 then ValueSome mean else ValueNone
    /// The running SAMPLE standard deviation (n−1). ValueNone until n >= 2.
    member _.Std =
        if n >= 2 then ValueSome (sqrt (m2 / float (n - 1))) else ValueNone
    /// The z-score of `x` against the accumulated mean/σ. ValueNone until n >= 2 or
    /// when σ = 0 (a degenerate constant series — z would be infinite).
    member t.Z (x: float) : float voption =
        match t.Std with
        | ValueSome sd when sd > 0.0 -> ValueSome ((x - mean) / sd)
        | _ -> ValueNone
    member _.Push (x: float) =
        n <- n + 1
        let d = x - mean
        mean <- mean + d / float n
        m2 <- m2 + d * (x - mean)   // note: uses the UPDATED mean — this is Welford
    /// Clear the accumulator (see RollingMa.Reset).
    member _.Reset () =
        n <- 0
        mean <- 0.0
        m2 <- 0.0

/// WINDOWED mean / standard-deviation over the last `windowSize` pushed values —
/// the fixed-count sibling of CumStdMa, same Welford recurrence extended with the
/// exact REMOVAL update so eviction is O(1) (no re-scan of the window):
///   add    (n → n+1):  d = x − mean;  mean += d/(n+1);       m2 += d·(x − mean′)
///   remove (n → n−1):  d = x − mean;  mean = mean − d/(n−1); m2 −= d·(x − mean′)
/// (mean′ = the updated mean in both; the removal is the add run backwards.)
/// Welford rather than sliding Σx/Σx² for the same reason as CumStdMa — the
/// algebraic form loses precision catastrophically when the mean dwarfs the
/// variance, and the motivating feed here (ln(volume), ln(trade_count) baselines
/// for the SurgeRider z-scores) is exactly that shape. Standalone rather than a
/// RollingMa<_,_> subclass because the removal update needs the live count, which
/// the base's Add/Remove(bar, state) contract doesn't carry.
///
/// `.Std` is the SAMPLE deviation (n−1), ValueNone until 2 values are buffered;
/// equals CumStdMa exactly while fewer than `windowSize` values have been pushed.
/// Read BEFORE pushing the current bar for the strictly-prior value, or AFTER for
/// the inclusive one — same convention as the other structures here.
[<Sealed>]
type WinStdMa(windowSize: int) =
    let q = Queue<float>(windowSize)
    let mutable mean = 0.0
    let mutable m2 = 0.0            // Σ (x − mean)² over the window, maintained incrementally
    /// Count of values currently in the window.
    member _.Count = q.Count
    member _.WindowSize = windowSize
    /// The window mean, or ValueNone while the window is empty.
    member _.Mean = if q.Count > 0 then ValueSome mean else ValueNone
    /// The window SAMPLE standard deviation (n−1). ValueNone until n >= 2.
    /// m2 can drift a hair negative from float cancellation on near-constant
    /// feeds — clamp so the sqrt never NaNs.
    member _.Std =
        if q.Count >= 2 then ValueSome (sqrt (max 0.0 m2 / float (q.Count - 1))) else ValueNone
    /// The z-score of `x` against the window mean/σ. ValueNone until n >= 2 or
    /// when σ = 0 (a degenerate constant window — z would be infinite).
    member t.Z (x: float) : float voption =
        match t.Std with
        | ValueSome sd when sd > 0.0 -> ValueSome ((x - mean) / sd)
        | _ -> ValueNone
    member _.Push (x: float) =
        if q.Count = windowSize then
            // evict the oldest value: the add update run backwards
            let old = q.Dequeue()
            if q.Count = 0 then
                mean <- 0.0
                m2 <- 0.0
            else
                let d = old - mean
                mean <- mean - d / float q.Count       // q.Count = n−1 after the dequeue
                m2 <- m2 - d * (old - mean)            // uses the UPDATED (post-removal) mean
        q.Enqueue x
        let d = x - mean
        mean <- mean + d / float q.Count               // q.Count = n+1 after the enqueue
        m2 <- m2 + d * (x - mean)                      // uses the UPDATED mean — this is Welford
    /// Drop every buffered value and return cold (see RollingMa.Reset).
    member _.Reset () =
        q.Clear()
        mean <- 0.0
        m2 <- 0.0

/// Wilder's ADX(period) + directional indicators (+DI / −DI), fed OHLC bar-by-bar.
/// Direction-AGNOSTIC trend STRENGTH: high in a strong move (either way), low in a chop.
/// `.State` (the ADX) is ValueNone until ~2·period bars have folded — `period` to warm the
/// DI smoothing, then `period` more for the ADX to average the DX. `.PlusDi`/`.MinusDi` are
/// available after `period` bars.
///
/// Wilder smoothing (NOT a plain SMA): s_t = s_{t-1} − s_{t-1}/N + x_t — the standard ADX
/// definition. Push each bar's (high, low, close); the true-range/±DM are computed against
/// the strictly-prior bar the accumulator remembers internally.
[<Sealed>]
type AdxMa(period: int) =
    let mutable prev : (float * float * float) voption = ValueNone   // prior (high, low, close)
    let mutable n = 0
    let mutable trS = 0.0        // Wilder-smoothed true range
    let mutable pdmS = 0.0       // Wilder-smoothed +DM
    let mutable mdmS = 0.0       // Wilder-smoothed -DM
    let mutable adx = 0.0
    let mutable adxN = 0
    let wilder (s: float) (x: float) = s - s / float period + x
    /// +DI (the up-pressure leg). ValueNone until `period` bars have folded.
    member _.PlusDi  = if n >= period && trS > 0.0 then ValueSome (100.0 * pdmS / trS) else ValueNone
    /// −DI (the down-pressure leg). ValueNone until `period` bars have folded.
    member _.MinusDi = if n >= period && trS > 0.0 then ValueSome (100.0 * mdmS / trS) else ValueNone
    /// The ADX itself — ValueNone until the DX average has warmed (~2·period bars).
    member _.State = if adxN >= period then ValueSome adx else ValueNone
    member _.Push (h: float, l: float, c: float) =
        match prev with
        | ValueNone -> prev <- ValueSome (h, l, c)
        | ValueSome (ph, pl, pc) ->
            let upMove = h - ph
            let downMove = pl - l
            // Directional movement: only the LARGER of the two counts, and only if positive.
            let pdm = if upMove > downMove && upMove > 0.0 then upMove else 0.0
            let mdm = if downMove > upMove && downMove > 0.0 then downMove else 0.0
            let tr = max (h - l) (max (abs (h - pc)) (abs (l - pc)))
            n <- n + 1
            if n <= period then
                // seed the smoothing with plain sums over the first `period` bars
                trS  <- trS + tr
                pdmS <- pdmS + pdm
                mdmS <- mdmS + mdm
            else
                trS  <- wilder trS tr
                pdmS <- wilder pdmS pdm
                mdmS <- wilder mdmS mdm
            if n >= period && trS > 0.0 then
                let pdi = 100.0 * pdmS / trS
                let mdi = 100.0 * mdmS / trS
                let denom = pdi + mdi
                if denom > 0.0 then
                    let dx = 100.0 * abs (pdi - mdi) / denom
                    adxN <- adxN + 1
                    adx <- if adxN = 1 then dx else (adx * float (period - 1) + dx) / float period
            prev <- ValueSome (h, l, c)
    /// Clear the accumulator (see RollingMa.Reset).
    member _.Reset () =
        prev <- ValueNone
        n <- 0
        trS <- 0.0
        pdmS <- 0.0
        mdmS <- 0.0
        adx <- 0.0
        adxN <- 0

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
    /// ⭐ S43aj (user): the OLDEST value still held, warm or not — `Lagged` once the
    /// window is full, the earliest push before that. Lets a ratio be formed over a
    /// PARTIAL window (paired with `Count` so the span is recorded, not guessed).
    member _.Oldest = if q.Count > 0 then ValueSome (q.Peek()) else ValueNone
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

/// HALF-LIFE exponential moving average with BIAS CORRECTION — the SurgeRider
/// vol-driver form (EmaMa of |slot-return|, hl = 40 slots, per the F5-F8 bake-off
/// in docs/surgerider_results.md). Two differences from EmaMa:
///
///   1. α comes from a HALF-LIFE: α = 1 − 0.5^(1/hl) — "the value `hl` pushes ago
///      carries half the weight of the newest". EmaMa's α = 2/(period+1) can't
///      express hl = 40 (period ≈ 115.4, non-integer).
///   2. NORMALIZED (bias-corrected) read: EmaMa seeds on the first value, which
///      leaves that seed (1−α)^k of the TOTAL weight after k pushes — at hl = 40,
///      still 59% of the estimate 30 pushes in. Here the state is a decayed
///      numerator/denominator pair (num = Σ decayed α·x, den = Σ decayed α), and
///      `.State = num/den` — exactly the normalized weighted mean over the pushed
///      history (Σwᵢxᵢ/Σwᵢ, wᵢ geometric), the construction the bake-off measured.
///      den → 1, so the correction fades as the support fills; it matters in the
///      first ~2 half-lives, i.e. the whole 09:45-11:00 entry window at hl = 40
///      slots. (Equivalent to Adam-style ema/(1−(1−α)^n).)
///
/// `.State` is ValueNone before the first push. Read BEFORE pushing the current
/// bar for the strictly-prior (no-lookahead) value, like the other structures.
[<Sealed>]
type EmaHlMa(halfLife: float) =
    let alpha = 1.0 - 0.5 ** (1.0 / halfLife)
    let mutable num = 0.0    // Σ decayed α·x
    let mutable den = 0.0    // Σ decayed α   (→ 1 as the support fills)
    /// The bias-corrected EWMA, or ValueNone before the first push.
    member _.State = if den > 0.0 then ValueSome (num / den) else ValueNone
    member _.Push (x: float) =
        num <- (1.0 - alpha) * num + alpha * x
        den <- (1.0 - alpha) * den + alpha
    /// Clear the accumulator (see RollingMa.Reset).
    member _.Reset () =
        num <- 0.0
        den <- 0.0

/// ⭐ Bias-corrected EWMA CENTERED VARIANCE — the decayed-triple extension of
/// EmaHlMa's num/den trick: s0/s1/s2 are Σ decayed α·{1, x, x²}, so
/// `Mean = s1/s0` and `Var = s2/s0 − Mean²` is the exact exponentially-weighted
/// centered variance of everything pushed — correct from the FIRST push (Var = 0,
/// one point has no spread) with no SMA warm-up, the same argument as EmaHlMa.
///
/// ⚠ The μ² form is SAFE here, unlike in EwmaAutoCorrMa where it leaked drift:
/// that failure was a lag-k cross product whose two series carry different
/// means; at lag 0 both means are the same series at the same time and
/// s2/s0 − (s1/s0)² IS the weighted centered variance, identically.
///
/// Inputs are shifted by the first pushed value before accumulating, so the
/// cancellation in s2/s0 − mean² happens near zero whatever the level of the
/// series. Var/Std are shift-invariant; Mean adds the origin back.
[<Sealed>]
type EwmaVarMa(halfLife: float) =
    do if halfLife <= 0.0 then invalidArg (nameof halfLife) "halfLife must be > 0"
    let alpha = 1.0 - 0.5 ** (1.0 / halfLife)
    let mutable x0 = nan
    let mutable s0 = 0.0
    let mutable s1 = 0.0
    let mutable s2 = 0.0
    member _.Mean = if s0 > 0.0 then ValueSome (x0 + s1 / s0) else ValueNone
    /// max-0-clamped: the subtraction can go −1e−18 on a constant series.
    member _.Var =
        if s0 > 0.0 then
            let m = s1 / s0
            ValueSome (max 0.0 (s2 / s0 - m * m))
        else ValueNone
    member this.Std =
        match this.Var with ValueSome v -> ValueSome (sqrt v) | ValueNone -> ValueNone
    member _.Push (x: float) =
        if Double.IsNaN x0 then x0 <- x
        let y = x - x0
        s0 <- (1.0 - alpha) * s0 + alpha
        s1 <- (1.0 - alpha) * s1 + alpha * y
        s2 <- (1.0 - alpha) * s2 + alpha * y * y
    member _.Reset () =
        x0 <- nan
        s0 <- 0.0
        s1 <- 0.0
        s2 <- 0.0

/// 30-present-bar SLOT VWAP builder — the return clock for the SurgeRider vol
/// driver (docs/surgerider_results.md F5b "How slot-EWMA works"). Accumulates
/// (Σ vwap·volume, Σ volume) over `slotBars` consecutive pushes; on the push that
/// completes the slot it EMITS the slot vwap `V = Σ(vwap·volume)/Σvolume` — the
/// exact trade-level VWAP of those bars, since each 1s bar's vwap·volume is that
/// second's dollar volume — and AUTO-RESETS for the next slot. Every other push
/// returns ValueNone. The caller chains the emissions (r = ln(V/V_prev) → EmaHlMa
/// of |r|) with a plain mutable prev or a LagMa.
///
/// A completing slot with Σvolume = 0 (can't happen on the 1s dataset — volume ≥ 1
/// on every present bar — but guard anyway) emits ValueNone and still resets.
[<Sealed>]
type SlotVwapMa(slotBars: int) =
    let mutable pv = 0.0     // Σ vwap·volume over the current partial slot
    let mutable v = 0.0      // Σ volume
    let mutable n = 0        // bars in the current partial slot
    /// Bars accumulated into the current PARTIAL slot (0 right after an emission).
    member _.Count = n
    member _.SlotBars = slotBars
    /// Fold one bar in. Returns the completed slot's vwap on every `slotBars`-th
    /// push (then starts the next slot cold), ValueNone otherwise.
    member _.Push (vwap: float, volume: float) : float voption =
        pv <- pv + vwap * volume
        v <- v + volume
        n <- n + 1
        if n = slotBars then
            let out = if v > 0.0 then ValueSome (pv / v) else ValueNone
            pv <- 0.0
            v <- 0.0
            n <- 0
            out
        else ValueNone
    /// Drop the current partial slot (see RollingMa.Reset).
    member _.Reset () =
        pv <- 0.0
        v <- 0.0
        n <- 0

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
    // ⭐ Only y is stored. x is the ABSOLUTE push index, and eviction is FIFO with
    // exactly one removal per push once full, so the departing point's x is always
    // `idx - windowSize` at the top of Push — derivable, never stored. Halves the
    // buffer (8 bytes/point instead of 16) and is bit-identical: the same ox value
    // enters the same subtractions in the same order.
    let q = Queue<float>(windowSize)
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
            let oy = q.Dequeue()
            let ox = idx - float windowSize          // the departing point's x
            sx <- sx - ox; sy <- sy - oy
            sxx <- sxx - ox * ox; sxy <- sxy - ox * oy; syy <- syy - oy * oy
        let x = idx
        idx <- idx + 1.0
        q.Enqueue y
        sx <- sx + x; sy <- sy + y
        sxx <- sxx + x * x; sxy <- sxy + x * y; syy <- syy + y * y

    /// Drop every buffered point and return cold — including the x index, so a
    /// fresh episode starts its regression at x=0 (see RollingMa.Reset).
    member _.Reset () =
        q.Clear()
        sx <- 0.0; sy <- 0.0; sxx <- 0.0; sxy <- 0.0; syy <- 0.0
        idx <- 0.0

/// Online effective-observation count via the Herfindahl–Hirschman index
/// (Rényi order 2):  N_eff = (Σv)² / Σv²
///
/// Weights heavy observations strongly — the alarm for "one print owns this
/// window". Immutable, associative, commutative, O(1) state. Zero, negative
/// and non-finite inputs are skipped.
///
/// The primary constructor is public so accumulators can be persisted as
/// (sum, sumSq, support) and rehydrated from a checkpoint.
[<Struct>]
type NEffHhi(sum: float, sumSq: float, support: int) =
    /// Monoid identity. Equal to Unchecked.defaultof<NEffHhi>.
    static member Zero = NEffHhi(0.0, 0.0, 0)

    /// Σv
    member _.Sum = sum
    /// Σv²
    member _.SumSq = sumSq
    /// Count of strictly positive observations (the Rényi order-0 count).
    member _.Support = support

    /// Fold in one observation, returning a new accumulator.
    member _.Add(v: float) =
        if v > 0.0 && Double.IsFinite v then
            NEffHhi(sum + v, sumSq + v * v, support + 1)
        else
            NEffHhi(sum, sumSq, support)

    /// Combine two independently accumulated windows.
    member _.Merge(other: NEffHhi) =
        NEffHhi(sum + other.Sum, sumSq + other.SumSq, support + other.Support)

    /// Σpᵢ² on normalised shares — the index itself, in [1/support, 1].
    /// nan when empty.
    member _.Index =
        if sum > 0.0 then sumSq / (sum * sum) else nan

    /// Effective number of equally weighted observations, in [1, support].
    /// 0.0 when empty.
    member _.Value =
        if sum > 0.0 then (sum * sum) / sumSq else 0.0

    static member (+) (a: NEffHhi, b: NEffHhi) = a.Merge b


/// Online effective-observation count via Shannon entropy (Rényi order 1,
/// i.e. perplexity):  N_eff = exp(H),  H = ln(Σv) − (Σ v·ln v)/(Σv)
///
/// Weights each observation by its own share — the gauge for "is there enough
/// distributed activity here". Same monoid properties as NEffHhi.
[<Struct>]
type NEffShannon(sum: float, sumVLogV: float, support: int) =

    /// Monoid identity. Equal to Unchecked.defaultof<NEffShannon>.
    static member Zero = NEffShannon(0.0, 0.0, 0)

    /// Σv
    member _.Sum = sum
    /// Σ v·ln v
    member _.SumVLogV = sumVLogV
    /// Count of strictly positive observations.
    member _.Support = support

    /// Fold in one observation, returning a new accumulator.
    member _.Add(v: float) =
        if v > 0.0 && Double.IsFinite v then
            NEffShannon(sum + v, sumVLogV + v * log v, support + 1)
        else
            NEffShannon(sum, sumVLogV, support)

    /// Combine two independently accumulated windows.
    member _.Merge(other: NEffShannon) =
        NEffShannon(sum + other.Sum, sumVLogV + other.SumVLogV, support + other.Support)

    /// Shannon entropy in nats, in [0, ln support]. nan when empty.
    /// Clamped at zero: H is mathematically non-negative, but the subtraction
    /// can land a few ulps below it when all mass sits on one observation.
    member _.Entropy =
        if sum > 0.0 then max 0.0 (log sum - sumVLogV / sum) else nan

    /// Effective number of equally weighted observations, in [1, support].
    /// 0.0 when empty.
    member this.Value =
        if sum > 0.0 then exp this.Entropy else 0.0

    static member (+) (a: NEffShannon, b: NEffShannon) = a.Merge b


[<RequireQualifiedAccess>]
module NEff =

    /// Fold a sequence into both accumulators in a single pass.
    let ofSeq (xs: seq<float>) =
        let mutable h = NEffHhi.Zero
        let mutable s = NEffShannon.Zero
        for v in xs do
            h <- h.Add v
            s <- s.Add v
        struct (h, s)

    /// shannon / hhi — near 1.0 when participating bars are evenly matched,
    /// large when a few dominant prints sit on an otherwise healthy base.
    let concentrationRatio (h: NEffHhi) (s: NEffShannon) =
        if h.Value > 0.0 then s.Value / h.Value else nan

/// Sliding-window aggregation over an arbitrary monoid, in amortised O(1) per
/// operation and O(w) memory, where w is the number of elements currently held.
///
/// Maintains a FIFO queue of the window's elements together with their combined
/// aggregate: Push admits the newest element, Pop evicts the oldest, Query
/// returns the aggregate of everything currently in the window.
///
/// REQUIREMENTS ON THE MONOID
///   `combine` must be associative and `zero` must be its two-sided identity.
///   Commutativity is NOT required — elements are always combined in window
///   order, oldest on the left. Crucially, no *inverse* is required, which is
///   the entire point of this structure: it serves aggregates that cannot be
///   subtracted (t-digest, HyperLogLog, top-k sketches) as well as those that
///   can. `Push` takes an already-lifted singleton aggregate rather than a raw
///   value, so the caller decides how a single observation becomes a 'T.
///
/// HOW IT WORKS
///   Two stacks stand in for one queue. `back` receives every Push; `front`
///   serves every Pop and is refilled by draining `back` into it — that
///   reversal is what turns LIFO into FIFO. Each entry carries the aggregate of
///   itself and everything below it in its own stack, which stays cheap to
///   maintain because a stack is only ever touched at the top: pushing v onto a
///   stack whose top holds A stores A ⊕ v, and popping merely exposes the entry
///   beneath, already correct. The window's aggregate is therefore just the two
///   tops combined, making Query O(1) with no traversal.
///
/// COST
///   A drain is O(n), but each element is pushed and popped at most twice in its
///   lifetime — once per stack — giving O(1) amortised per operation. The
///   worst-case hitch is bounded by window size, not by stream length.
///
/// NUMERICAL BEHAVIOUR
///   This is the reason to prefer it over the obvious subtract-the-evicted-
///   element approach for floating-point aggregates. Every stored aggregate is
///   built by accumulation upward from the identity, and each drain rebuilds
///   `front` from scratch, so nothing is ever removed from a sum. Error in a
///   reported value is bounded by that of summing w values once — a function of
///   window size, not of how many times the window has slid. Subtractive updates
///   accumulate error indefinitely and never recover.
///
/// Not thread-safe. Pop on an empty window throws.
[<Sealed>]
type SlidingAgg<'T>(zero: 'T, combine: 'T -> 'T -> 'T) =
    let back  = Stack<'T * 'T>()      // (value, aggregate of it and all below)
    let front = Stack<'T * 'T>()
    let topAgg (s: Stack<'T * 'T>) = if s.Count = 0 then zero else snd (s.Peek())

    member _.Push(v) = back.Push(v, combine (topAgg back) v)

    member _.Pop() =
        if front.Count = 0 then
            while back.Count > 0 do
                let (v, _) = back.Pop()
                front.Push(v, combine v (topAgg front))   // v is older → left
        front.Pop() |> ignore

    member _.Query = combine (topAgg front) (topAgg back)

// =============================================================================
// QUEUE SHARING — one ring buffer, many windows
// =============================================================================
//
// An engine that keeps N windows over ONE stream pays N copies of the same data,
// one Queue per window. Share the ELEMENTS in a single ring and let every window
// read the element it is evicting out of it.
//
// SCOPE: invertible aggregates only — sums, and OLS (which already subtracts the
// evicted point's exact contribution). max/min are NOT invertible: you cannot
// Remove an arbitrary value from a monotonic deque. That is a property of the
// aggregate, not a limitation here; MaxMa/MinMa keep their own structures.
//
// ⚠ THE TRADE. The ring stores ELEMENTS, so an evicting window recomputes its
// projection of the evicted element instead of reading a stored float. That pays
// only when many projections share the ring. With one projection it LOSES — a
// 1200 ring of 24-byte bars is 28.8 KB against a 1200-float queue's 9.6 KB.
//
// ⚠⚠ BIT-EXACTNESS AGAINST RollingMa/OlsSlopeMa. Two invariants, or the sums
// drift in the last bits and take every downstream ratio with them:
//   1. REMOVE BEFORE ADD. RollingMa.Push removes the dequeued element FIRST, and
//      (s - a) + b <> (s + b) - a in floating point.
//   2. ADD IS UNCONDITIONAL. A window accumulates during warmup and only starts
//      evicting once it is already full.
// Verified bit for bit by TradingEdge.Scanner/Engine/Roll_Test.fsx.

/// An aggregate that can absorb an element and give it back.
type IRoll<'T> =
    abstract Add    : 'T -> unit
    abstract Remove : 'T -> unit
    abstract Reset  : unit -> unit
    /// Evict `old` and absorb `nu` in ONE interface call — the steady state, and
    /// the only path a full window ever takes. Halves the dispatch on the hot
    /// loop. ⚠ MUST be term-for-term `Remove old` followed by `Add nu`: the
    /// engine's parity rests on `(s - a) + b`, which is not `(s + b) - a`.
    abstract Roll   : old: 'T * nu: 'T -> unit
    /// The window this aggregate expects to be driven at. WindowRoller checks it
    /// against the size it is registered under — see the constructor's note.
    abstract WindowSize : int

/// One shared ring feeding many windows. `rolls` pairs a window size with the
/// aggregates reading that size.
[<Sealed>]
type WindowRoller<'T>(rolls: (int * IRoll<'T>[])[]) =
    do if rolls.Length = 0 then invalidArg (nameof rolls) "WindowRoller needs at least one window"
    // ⚠⚠ THE MIS-REGISTRATION GUARD. With one shared ring the window size lives
    // at the REGISTRATION site, not in the aggregate's own Push, so registering
    // a 180-bar sum under 120 is a silent wrong answer rather than a crash. It
    // cannot be silent here.
    do for (w, rs) in rolls do
        for r in rs do
            if r.WindowSize <> w then
                invalidArg (nameof rolls) (sprintf "roll declared at window %d registered under %d" r.WindowSize w)
    let maxW = rolls |> Array.map fst |> Array.max
    // maxW + 1 slots: when the widest window evicts position (count-1-maxW) the
    // newest is (count-1), so maxW+1 positions must be live. Writing at
    // count % L never clobbers a needed slot because maxW is not a multiple of L.
    let buf : 'T[] = Array.zeroCreate (maxW + 1)
    let mutable count = 0

    member _.Count = count
    member _.MaxWindow = maxW

    member _.Push(b: 'T) =
        buf.[count % buf.Length] <- b
        count <- count + 1
        for (w, rs) in rolls do
            if count > w then
                // the element leaving the window: logical position count-1-w
                let a = buf.[(count - 1 - w) % buf.Length]
                for r in rs do r.Roll(a, b)   // ⚠ remove BEFORE add — RollingMa order
            else
                for r in rs do r.Add b  // warmup: accumulate, evict nothing

    /// Sever the window — the >45-day listing-gap reset (see RollingMa.Reset).
    member _.Reset() =
        count <- 0
        for (_, rs) in rolls do
            for r in rs do r.Reset()

/// Σ of a projection over a shared window. Mirrors SumMa exactly: `State` is
/// ValueNone while empty, and `Count` saturates at the window size because a full
/// window removes then adds.
///
/// ⭐ `Project` is an ABSTRACT METHOD, not a lambda field (user, 2026-08-19).
/// A closure costs an extra dereference on every call — load the `project`
/// field, then its vtable — and the ring calls the projection on EVERY Add and
/// EVERY Remove, so that is two extra objects touched per window per bar. An
/// override on `this` reuses the header the caller has already loaded. Specialise
/// by INHERITING and overriding once per projection; see Engine/Roll.fs.
[<AbstractClass>]
type SumRoll<'T>(windowSize: int) =
    let mutable sum = 0.0
    let mutable n = 0
    /// The scalar this window sums out of an element.
    abstract Project : 'T -> float
    member _.WindowSize = windowSize
    member _.Count = n
    member _.State = if n = 0 then ValueNone else ValueSome sum
    interface IRoll<'T> with
        member _.WindowSize = windowSize
        member this.Add b    = sum <- sum + this.Project b; n <- n + 1
        member this.Remove b = sum <- sum - this.Project b; n <- n - 1
        // (sum - old) + nu, exactly as Remove-then-Add produces it. n is
        // unchanged: a full window loses one and gains one.
        member this.Roll (a, b) = sum <- sum - this.Project a + this.Project b
        member _.Reset () = sum <- 0.0; n <- 0

/// Rolling OLS slope + R² over a shared window — the IRoll form of OlsSlopeMa.
///
/// ⭐ OLS is invertible: OlsSlopeMa already evicts by subtracting the oldest
/// point's exact contribution, so it fits this design unchanged in arithmetic.
/// The only thing it needs that the ring cannot supply is each point's
/// x-coordinate, which is its ABSOLUTE push index. Adds and removes each advance
/// in lock step (one remove per add once full), so two independent counters
/// reproduce the x's exactly: `addX` for the arriving point, `remX` for the
/// departing one. No index is stored, and none needs to be.
[<AbstractClass>]
type OlsRoll<'T>(windowSize: int) =
    let mutable sx  = 0.0
    let mutable sy  = 0.0
    let mutable sxx = 0.0
    let mutable sxy = 0.0
    let mutable syy = 0.0
    let mutable addX = 0.0      // x of the NEXT arriving point
    let mutable remX = 0.0      // x of the NEXT departing point
    let mutable n = 0

    /// The scalar this window regresses. See SumRoll for why it is an abstract
    /// method rather than a lambda field.
    abstract Project : 'T -> float

    member _.Count = n
    member _.WindowSize = windowSize

    /// OLS slope (y-per-push), or ValueNone with <2 points.
    member _.Slope : float voption =
        let fn = float n
        let dxx = fn * sxx - sx * sx
        if n >= 2 && dxx > 0.0 then ValueSome ((fn * sxy - sx * sy) / dxx) else ValueNone

    member this.State = this.Slope

    /// R² ∈ [0,1], or ValueNone with <2 points or a flat window.
    member _.R2 : float voption =
        let fn = float n
        let dxx = fn * sxx - sx * sx
        let dyy = fn * syy - sy * sy
        if n >= 2 && dxx > 0.0 && dyy > 0.0 then
            let dxy = fn * sxy - sx * sy
            ValueSome (dxy * dxy / (dxx * dyy))
        else ValueNone

    // ⚠⚠ ONE definition of each half of the arithmetic. Add, Remove and Roll all
    // route through these, so the three CANNOT drift apart term by term — which
    // is the whole risk, since parity rests on the exact operation ORDER rather
    // than on the algebra. Term-for-term and in the SAME ORDER as
    // OlsSlopeMa.Push, so the result is identical rather than merely equivalent.
    // `n` is deliberately left to the callers: Roll nets it out (-1 then +1).
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member private this.AddPoint (b: 'T) =
        let x = addX
        let y = this.Project b
        addX <- addX + 1.0
        sx <- sx + x; sy <- sy + y
        sxx <- sxx + x * x; sxy <- sxy + x * y; syy <- syy + y * y

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member private this.RemovePoint (b: 'T) =
        let ox = remX
        let oy = this.Project b
        remX <- remX + 1.0
        sx <- sx - ox; sy <- sy - oy
        sxx <- sxx - ox * ox; sxy <- sxy - ox * oy; syy <- syy - oy * oy

    interface IRoll<'T> with
        member _.WindowSize = windowSize
        member this.Add b    = this.AddPoint b; n <- n + 1
        member this.Remove b = this.RemovePoint b; n <- n - 1
        // ⚠ REMOVE BEFORE ADD. n nets out, so it is not touched.
        member this.Roll (a, b) = this.RemovePoint a; this.AddPoint b
        member _.Reset () =
            sx <- 0.0; sy <- 0.0; sxx <- 0.0; sxy <- 0.0; syy <- 0.0
            addX <- 0.0; remX <- 0.0; n <- 0

/// OlsRoll over a RAW FLOAT stream — the projection is identity.
///
/// ⭐ This is what lets a non-SecBar stream join the queue-sharing design. The
/// 30s-slot |log return| stream feeds TWO windows (40 -> volat_slope_20m,
/// 20 -> volat_slope_10m) where the 20 is a SUFFIX of the 40, so one
/// WindowRoller<float> holds the ring and both OLS objects read the element each
/// evicts. Reusing OlsRoll here rather than hand-rolling a second regression
/// keeps ONE parity story and inherits the mis-registration guard.
///
/// ⚠ It regresses against the ABSOLUTE push index, not position-within-window.
/// The slope is identical either way (translation of x does not move a slope);
/// the only question was float conditioning, since absolute x reaches ~780 in a
/// session and dxx = n*Sxx - (Sx)^2 becomes a difference of ~1e9 quantities.
/// MEASURED at window 40 over 800 pushes: dxx lands on exactly 213,200 (zero
/// error) and the worst absolute slope error against an O(n) recompute is
/// 8.8e-18. The concern does not bite at this scale.
[<Sealed>]
type FloatOls(windowSize: int) =
    inherit OlsRoll<float>(windowSize)
    override _.Project v = v

// ===========================================================================
// ⭐ TREND-PERSISTENCE STATISTICS (user, 2026-08-24) — autocorrelation, the
// variance ratio, and sign persistence over a return stream. All O(1) per push.
//
// ⭐⭐ ONE CLASS PER STATISTIC, TWO WINDOW POLICIES. `windowSize <= 0` means
// ANCHORED (growing from the last Reset); `windowSize > 0` means ROLLING over
// the last `windowSize` returns. They share one implementation deliberately: the
// anchored and rolling variants MUST compute the same statistic, and two
// separate implementations are one edit away from silently disagreeing — which
// is precisely how a "rolling twin control" stops being a control.
//
// ⚠⚠ FEED THESE THE SLOT-RETURN STREAM, NOT 1s BAR RETURNS. Autocorrelation of
// 1-second vwap returns measures BID-ASK BOUNCE: it is strongly negative, and
// its magnitude is a function of spread/price — a liquidity feature wearing a
// trend costume. The 30-bar slot returns (the stream volat_20m and the eff
// ratios already consume) are the honest level; sub-30s is microstructure (the
// F7 vol-lock finding).
// ===========================================================================

/// ⭐ A FIXED-CAPACITY RING BUFFER (user, 2026-08-24). Five structures in this
/// file had each hand-rolled the same `array + pos + at + (pos+1) % cap` shape,
/// which is five chances to get the wrap arithmetic wrong — and one of them
/// already had (SignPersistMa windowed over PAIRS where its twins window over
/// RETURNS, caught only by the oracle test).
///
/// ⭐ THE COUNT IS MONOTONE, NOT WRAPPED, and that is what makes the indexing
/// trivial: with `count` running free, `count - 1 - back` is non-negative for any
/// legal `back`, so a single unadorned `%` suffices. The usual
/// `((x % n) + n) % n` dance exists only to repair a wrapped cursor going
/// negative; keep the cursor unwrapped and there is nothing to repair.
///
/// ⚠⚠ THE PRECONDITIONS ARE CHECKED, ALWAYS — not behind `Debug.Assert`. Every
/// run that matters here is Release, so a debug-only assert would be decorative
/// in exactly the runs where a wrap bug would do damage. The cost is two
/// comparisons on a path called at SLOT cadence (once per 30 bars, times the lag
/// count), which is not a hot path; `At` is the per-bar form and WindowRoller
/// owns that one.
///
/// ⭐ Reading past the live region is otherwise a SILENTLY WRONG ELEMENT, not a
/// crash — `(count - 1 - back) %% capacity` happily returns a stale slot. That is
/// the failure mode this file has already produced once (SignPersistMa windowing
/// over pairs where its twins window over returns), and it was caught only
/// because an oracle test happened to exist.
[<Sealed>]
type RingBuffer<'T>(capacity: int) =
    do if capacity < 1 then invalidArg (nameof capacity) "capacity must be >= 1"
    let buf : 'T[] = Array.zeroCreate capacity
    let mutable count = 0
    member _.Capacity = capacity
    /// Total pushed since the last Reset. Monotone — NOT clamped to Capacity.
    member _.Count = count
    /// How many elements are live: min(Count, Capacity).
    member _.Live = min count capacity
    member _.IsFull = count >= capacity
    /// The element pushed `back` steps ago; 0 = the newest.
    /// ⚠ Requires `0 <= back < Live`.
    member this.Item
        with get (back: int) =
            if back < 0 || back >= this.Live then
                invalidArg (nameof back)
                    (sprintf "RingBuffer.[%d] outside [0, %d): count=%d capacity=%d"
                             back this.Live count capacity)
            buf.[(count - 1 - back) % capacity]
    /// The element at ABSOLUTE logical position `i` (0 = the first ever pushed) —
    /// the form WindowRoller's eviction is naturally written in.
    /// ⚠ Requires `Count - Capacity <= i < Count` (i.e. still live).
    member _.At (i: int) =
        if i < 0 || i >= count || i < count - capacity then
            invalidArg (nameof i)
                (sprintf "RingBuffer.At %d outside [%d, %d): capacity=%d"
                         i (max 0 (count - capacity)) count capacity)
        buf.[i % capacity]
    /// The oldest live element.
    member this.Oldest = this.[this.Live - 1]
    member _.Push (x: 'T) =
        buf.[count % capacity] <- x
        count <- count + 1
    member _.Reset () =
        Array.fill buf 0 capacity Unchecked.defaultof<'T>
        count <- 0

/// ⭐ Sample autocorrelation at lags 1..maxLag.
///
/// ⚠⚠ MEAN-CENTERED, AND THAT IS NOT OPTIONAL. The uncentered form
/// `Σ r_t·r_{t−k} / Σ r_t²` is biased upward by the series' DRIFT, so on a
/// trending name it reads high even for i.i.d. returns — it would simply
/// rediscover the efficiency ratio under a new name. Centering is what makes
/// this a statement about PERSISTENCE rather than about DIRECTION, and therefore
/// the only version that can add anything to eff_open.
///
/// Convention: the standard ACF estimator — the numerator runs over the m−k
/// available pairs, the denominator over all m points. nan below k+3 points.
[<Sealed>]
type AutoCorrMa(windowSize: int, maxLag: int) =
    do if maxLag < 1 then invalidArg (nameof maxLag) "maxLag must be >= 1"
       if windowSize > 0 && windowSize <= maxLag then
           invalidArg (nameof windowSize) "a rolling window must be longer than maxLag"
    let anchored = windowSize <= 0
    /// Anchored only needs enough history to FORM the products; rolling needs the
    /// whole window so departing terms can be removed exactly.
    let cap = if anchored then maxLag + 1 else windowSize
    /// The first maxLag values ever pushed — the anchored window's left edge,
    /// which the ring is too short to hold.
    let firstVals = Array.zeroCreate<float> maxLag
    let ck = Array.zeroCreate<float> (maxLag + 1)
    let mutable n = 0
    let mutable s1 = 0.0
    let mutable s2 = 0.0
    /// The value pushed `back` steps ago (0 = most recent).
    let ring = RingBuffer<float> cap
    let at (back: int) = ring.[back]

    member _.Count = n
    member _.WindowSize = windowSize
    member _.MaxLag = maxLag
    /// Points currently inside the active window.
    member _.Span = if anchored then n else min n windowSize

    member _.Push (r: float) =
        // ⚠ EVICT FIRST, and read every departing term out of the ring BEFORE the
        // new value overwrites a slot.
        if not anchored && n >= windowSize then
            let oldest = at (windowSize - 1)
            s1 <- s1 - oldest
            s2 <- s2 - oldest * oldest
            for k in 1 .. maxLag do
                if windowSize - 1 - k >= 0 then
                    ck.[k] <- ck.[k] - at (windowSize - 1 - k) * oldest
        if n < maxLag then firstVals.[n] <- r
        for k in 1 .. maxLag do
            if n >= k then ck.[k] <- ck.[k] + r * at (k - 1)
        ring.Push r
        n <- n + 1
        s1 <- s1 + r
        s2 <- s2 + r * r

    /// Autocorrelation at lag k, or nan while cold / on a flat window.
    member this.Rho (k: int) : float =
        if k < 1 || k > maxLag then nan else
        let m = this.Span
        if m < k + 3 then nan else
        let fm = float m
        let den = s2 - s1 * s1 / fm
        if not (den > 0.0) then nan else
        let mean = s1 / fm
        let full = not anchored && n >= windowSize
        let mutable sumFirst = 0.0
        let mutable sumLast = 0.0
        for j in 0 .. k - 1 do
            sumLast  <- sumLast  + at j
            sumFirst <- sumFirst + (if full then at (m - 1 - j) else firstVals.[j])
        // Σ(r_t−μ)(r_{t−k}−μ) = Σ r_t r_{t−k} − μ(A_k + B_k) + (m−k)μ²
        let aK = s1 - sumFirst      // Σ r_t over the k+1..m tail
        let bK = s1 - sumLast       // Σ r_{t−k} over the same pairs = the 1..m−k head
        (ck.[k] - mean * (aK + bK) + float (m - k) * mean * mean) / den

    member _.Reset () =
        ring.Reset()
        Array.fill firstVals 0 maxLag 0.0
        Array.fill ck 0 (maxLag + 1) 0.0
        n <- 0; s1 <- 0.0; s2 <- 0.0

/// ⭐ The VARIANCE RATIO (Lo-MacKinlay shape): Var(q-period return) /
/// (q · Var(1-period return)) over OVERLAPPING q-sums.
///
///   VR > 1  → positively autocorrelated / TRENDING
///   VR = 1  → random walk
///   VR < 1  → mean-reverting
///
/// ⭐ WHY THIS AND NOT JUST ρ₁: the variance ratio aggregates lags 1..q−1 into a
/// single number with weights that fall off linearly, so it is far less noisy
/// than any single-lag autocorrelation on a short session sample. It is also the
/// standard test statistic for exactly the question being asked.
///
/// ⚠ This is the UNADJUSTED overlapping estimator, using each series' own sample
/// mean. Lo-MacKinlay's heteroskedasticity-robust correction is NOT applied —
/// this is a feature to BAND on, not a hypothesis test, and the correction is a
/// monotone rescaling that would not change any ordering. Do not quote it as a
/// significance test.
[<Sealed>]
type VarianceRatioMa(windowSize: int, q: int) =
    do if q < 2 then invalidArg (nameof q) "q must be >= 2"
       if windowSize > 0 && windowSize <= q then
           invalidArg (nameof windowSize) "a rolling window must be longer than q"
    let anchored = windowSize <= 0
    let cap = if anchored then q else windowSize
    let mutable n = 0
    let mutable s1 = 0.0        // Σ r      over the active window
    let mutable s2 = 0.0        // Σ r²
    let mutable t1 = 0.0        // Σ R      over the active window's overlapping q-sums
    let mutable t2 = 0.0        // Σ R²
    let mutable mq = 0          // how many q-sums are in it
    let ring = RingBuffer<float> cap
    let at (back: int) = ring.[back]

    member _.Count = n
    member _.WindowSize = windowSize
    member _.Q = q
    member _.Span = if anchored then n else min n windowSize

    member _.Push (r: float) =
        if not anchored && n >= windowSize then
            let oldest = at (windowSize - 1)
            s1 <- s1 - oldest
            s2 <- s2 - oldest * oldest
            // the q-sum that leaves is the one ENDING q−1 steps after the oldest
            if windowSize >= q then
                let mutable rOld = 0.0
                for j in 0 .. q - 1 do rOld <- rOld + at (windowSize - 1 - j)
                t1 <- t1 - rOld
                t2 <- t2 - rOld * rOld
                mq <- mq - 1
        ring.Push r
        n <- n + 1
        s1 <- s1 + r
        s2 <- s2 + r * r
        if n >= q then
            let mutable rNew = 0.0
            for j in 0 .. q - 1 do rNew <- rNew + at j
            t1 <- t1 + rNew
            t2 <- t2 + rNew * rNew
            mq <- mq + 1

    /// nan while cold or on a degenerate window.
    member this.Value : float =
        let m = this.Span
        if m < q + 3 || mq < 2 then nan else
        let var1 = (s2 - s1 * s1 / float m) / float (m - 1)
        let varq = (t2 - t1 * t1 / float mq) / float (mq - 1)
        if not (var1 > 0.0) then nan else varq / (float q * var1)

    member _.Reset () =
        ring.Reset()
        n <- 0; s1 <- 0.0; s2 <- 0.0; t1 <- 0.0; t2 <- 0.0; mq <- 0

/// ⭐ SIGN PERSISTENCE: the fraction of consecutive return pairs that share a
/// sign — the crudest and most robust trend statistic there is, with no scale and
/// no distributional assumption.
///
/// ⚠ TIES ARE EXCLUDED FROM BOTH SIDES. A pair counts only when `r_{t−1}·r_t ≠ 0`;
/// a zero slot return is neither agreement nor disagreement. Scoring ties as
/// disagreement is the same mistake as scoring a flat trade as a loss, and the
/// tie rate is not constant across tape density (measured 0.30% dense vs 2.42%
/// sparse), so it would smuggle a liquidity gradient into a trend feature.
[<Sealed>]
type SignPersistMa(windowSize: int) =
    let anchored = windowSize <= 0
    /// ⚠ `windowSize` counts RETURNS, matching AutoCorrMa and VarianceRatioMa —
    /// so the pair window is one SHORTER, because W returns form W−1 pairs.
    /// Windowing over pairs instead silently makes this class's "last 40" a
    /// different 40 from its twins', which is exactly the drift a shared window
    /// convention exists to prevent (caught by the oracle test at 2.3e-2).
    let capPairs = if anchored then 1 else max 1 (windowSize - 1)
    /// Per-pair outcome ring: +1 concordant, -1 discordant, 0 tie.
    let outcomes = RingBuffer<sbyte> capPairs
    let mutable pairs = 0        // pairs formed since Reset
    let mutable nCon = 0
    let mutable nDis = 0
    let mutable prev = nan
    let mutable run = 0          // signed: +k an up-run of k, −k a down-run

    member _.Pairs = pairs
    member _.WindowSize = windowSize
    /// Current same-sign run length, SIGNED. 0 = no run (first return, or a tie).
    member _.Run = run

    member _.Push (r: float) =
        if not (Double.IsNaN prev) then
            let o = if prev * r > 0.0 then 1y elif prev * r < 0.0 then -1y else 0y
            if not anchored && pairs >= capPairs then
                match outcomes.Oldest with
                | 1y -> nCon <- nCon - 1
                | -1y -> nDis <- nDis - 1
                | _ -> ()
            outcomes.Push o
            pairs <- pairs + 1
            match o with
            | 1y -> nCon <- nCon + 1
            | -1y -> nDis <- nDis + 1
            | _ -> ()
            // the run tracks the RAW stream, not the window — it is a "right now"
            // reading and has no meaningful windowed form
            if o = 1y then run <- run + (if r > 0.0 then 1 else -1)
            elif o = -1y then run <- (if r > 0.0 then 1 else -1)
            else run <- 0
        elif r > 0.0 then run <- 1
        elif r < 0.0 then run <- -1
        prev <- r

    /// P(next return shares the previous one's sign), ties excluded. nan below 4 pairs.
    member _.Value : float =
        let d = nCon + nDis
        if d < 4 then nan else float nCon / float d

    member _.Reset () =
        outcomes.Reset()
        pairs <- 0; nCon <- 0; nDis <- 0; prev <- nan; run <- 0

// ===========================================================================
// ⭐⭐ EWMA FORMS OF THE TREND STATISTICS (user, 2026-08-24)
//
// The windowed versions have a warm-up cliff: eff_20m needs a FULL 40-slot
// window and is still ~89% null 29 minutes into the session, because slots count
// PRESENT bars and a typical name yields ~1 slot per minute, not 2. The EWMA
// forms have no window to fill — they are live from the first few returns and
// their effective span grows smoothly toward the half-life instead of switching
// on at a boundary.
//
// ⭐ ALL THREE CONSUME THE SIGNED SLOT-RETURN STREAM, exactly like their windowed
// twins, so a twin pair differs ONLY in its weighting. That is what makes the
// pair a control rather than two unrelated numbers.
// ===========================================================================

/// ⭐ Kaufman efficiency as an EWMA: EWMA(r) / EWMA(|r|).
///
/// ⭐⭐ THE KEY IDENTITY (user, 2026-08-24): the windowed numerator
/// `ln(V_t / V_{t−n})` is EXACTLY `Σ r_k` over the same span, because the log
/// returns telescope. Written as a sum it becomes EWMA-able; written as a
/// two-endpoint difference it cannot be. Same statistic, one representation
/// admits exponential weighting and the other does not.
///
/// ⭐ AND THE BIAS CORRECTION CANCELS. EmaHlMa reads `num/den`; here both halves
/// carry the SAME `den`, so the ratio is `num_r / num_abs` and is exactly
/// unbiased from the first push — no warm-up term at all. This is the one
/// construction where the correction costs nothing and buys everything, so the
/// denominator is not even accumulated.
///
/// Bounded to [−1,1] by the triangle inequality (the weights are positive).
/// nan below `minCount` pushes or on a zero path.
[<Sealed>]
type EwmaEffMa(halfLife: float, minCount: int) =
    let alpha = 1.0 - 0.5 ** (1.0 / halfLife)
    let mutable numR = 0.0      // Σ decayed α·r
    let mutable numA = 0.0      // Σ decayed α·|r|
    let mutable n = 0
    new(halfLife) = EwmaEffMa(halfLife, 3)
    member _.Count = n
    member _.HalfLife = halfLife
    member _.Push (r: float) =
        numR <- (1.0 - alpha) * numR + alpha * r
        numA <- (1.0 - alpha) * numA + alpha * abs r
        n <- n + 1
    member _.Value = if n < minCount || not (numA > 0.0) then nan else numR / numA
    member _.Reset () = numR <- 0.0; numA <- 0.0; n <- 0

/// ⭐ The variance ratio as an EWMA: Var_ewma(q-sum) / (q · Var_ewma(1-step)).
///
/// ⚠ UNLIKE EwmaEffMa THE BIAS CORRECTION DOES NOT CANCEL HERE — a variance is
/// `E[x²] − E[x]²`, so the normalising denominator enters the two terms
/// differently. Both moments therefore go through a full bias-corrected EmaHlMa
/// rather than a bare decayed sum.
///
/// ⚠ Two honest caveats, neither fatal for a feature you BAND on:
///   1. no Bessel correction, so each variance is biased low; at a shared
///      half-life the numerator and denominator biases largely cancel in the
///      ratio.
///   2. the q-sum stream starts q−1 pushes after the 1-step stream, so very
///      early the two moments rest on slightly different supports.
/// Condition on `.Count`. Do not quote it as a significance test.
[<Sealed>]
type EwmaVarRatioMa(halfLife: float, q: int, minCount: int) =
    do if q < 2 then invalidArg (nameof q) "q must be >= 2"
    let e1 = EmaHlMa halfLife       // E[r]
    let e2 = EmaHlMa halfLife       // E[r²]
    let f1 = EmaHlMa halfLife       // E[R]
    let f2 = EmaHlMa halfLife       // E[R²]
    let ring = RingBuffer<float> q
    let mutable n = 0
    new(halfLife, q) = EwmaVarRatioMa(halfLife, q, q + 3)
    member _.Count = n
    member _.Q = q
    member _.Push (r: float) =
        ring.Push r
        n <- n + 1
        e1.Push r
        e2.Push (r * r)
        if n >= q then
            let mutable s = 0.0
            for j in 0 .. q - 1 do s <- s + ring.[j]   // the whole ring IS the last q returns
            f1.Push s
            f2.Push (s * s)
    member _.Value =
        if n < minCount then nan else
        match e1.State, e2.State, f1.State, f2.State with
        | ValueSome m1, ValueSome m2, ValueSome n1, ValueSome n2 ->
            let var1 = m2 - m1 * m1
            let varq = n2 - n1 * n1
            if not (var1 > 0.0) then nan else varq / (float q * var1)
        | _ -> nan
    member _.Reset () =
        e1.Reset(); e2.Reset(); f1.Reset(); f2.Reset()
        ring.Reset(); n <- 0

/// ⭐ Autocorrelation as an EWMA:
///     ρ_k = ( E[r_t·r_{t−k}] − E_A[r_t]·E_B[r_{t−k}] ) / ( E[r²] − μ² )
///
/// ⚠ MEAN-CENTERED for the same reason the windowed version is — the uncentered
/// form is biased upward by drift and would rediscover the efficiency ratio.
///
/// ⚠⚠ THE TWO MEANS IN THE NUMERATOR ARE TRACKED SEPARATELY, on exactly the
/// product's own support. Collapsing them to μ² assumes the mean is the same at
/// t and at t−k — true under stationarity, and FALSE for a stock whose drift is
/// changing, which is the only kind this system trades. Measured at hl = 40:
///
///     stationary mean          shortcut vs paired   2e-4 .. 6e-4   (free)
///     drift ramping 0 -> 5%%    +0.180 vs +0.098     8e-2           (~2x)
///
/// The shortcut inflates ρ on a ramping drift — the same drift-leak that
/// centering exists to prevent, reintroduced through the back door. E_A and E_B
/// are pushed at the same instant as the product, so all three share one support
/// exactly. The DENOMINATOR keeps the full-stream variance, matching AutoCorrMa's
/// convention (numerator over the m−k pairs, denominator over all m).
[<Sealed>]
type EwmaAutoCorrMa(halfLife: float, maxLag: int, minCount: int) =
    do if maxLag < 1 then invalidArg (nameof maxLag) "maxLag must be >= 1"
    let e1 = EmaHlMa halfLife
    let e2 = EmaHlMa halfLife
    let ck = Array.init (maxLag + 1) (fun _ -> EmaHlMa halfLife)
    let mA = Array.init (maxLag + 1) (fun _ -> EmaHlMa halfLife)   // E_A[r_t]
    let mB = Array.init (maxLag + 1) (fun _ -> EmaHlMa halfLife)   // E_B[r_{t−k}]
    let ring = RingBuffer<float> (maxLag + 1)
    let mutable n = 0
    let at (back: int) = ring.[back]
    new(halfLife, maxLag) = EwmaAutoCorrMa(halfLife, maxLag, maxLag + 4)
    member _.Count = n
    member _.MaxLag = maxLag
    member _.Push (r: float) =
        for k in 1 .. maxLag do
            if n >= k then
                let lagged = at (k - 1)
                ck.[k].Push (r * lagged)
                mA.[k].Push r
                mB.[k].Push lagged
        ring.Push r
        n <- n + 1
        e1.Push r
        e2.Push (r * r)
    member _.Rho (k: int) =
        if k < 1 || k > maxLag || n < minCount then nan else
        match e1.State, e2.State, ck.[k].State, mA.[k].State, mB.[k].State with
        | ValueSome m1, ValueSome m2, ValueSome c, ValueSome a, ValueSome b ->
            let den = m2 - m1 * m1
            if not (den > 0.0) then nan else (c - a * b) / den
        | _ -> nan
    member _.Reset () =
        e1.Reset(); e2.Reset()
        for e in ck do e.Reset()
        for e in mA do e.Reset()
        for e in mB do e.Reset()
        ring.Reset(); n <- 0
