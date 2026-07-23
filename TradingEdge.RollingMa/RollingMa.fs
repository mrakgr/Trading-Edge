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