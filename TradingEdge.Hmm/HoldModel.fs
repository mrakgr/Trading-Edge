module TradingEdge.Hmm.HoldModel

open MathNet.Numerics.LinearAlgebra
open TradingEdge.Hmm.VolumeBar
open TradingEdge.Hmm.HoldEmission
open TradingEdge.Hmm.ForwardBackward

/// Three-state bar-level hold detector. States 0/1/2 = Hold / Fakeout / Trend.
///
/// Constrained transition graph (hard zeros, see defaultTransition):
///     Hold     → {Hold, Fakeout, Trend}
///     Fakeout  → {Hold, Fakeout}            (cannot become Trend)
///     Trend    → {Hold, Trend}              (cannot become Fakeout)
///
/// Hold acts as the hub. A failed hold collapses through Fakeout back to Hold
/// (or whatever); a successful hold fires through to Trend. Fakeout / Trend
/// don't connect directly — that's the structural distinction between them.
[<Literal>]
let K = 3
[<Literal>]
let HOLD = 0
[<Literal>]
let FAKEOUT = 1
[<Literal>]
let TREND = 2

/// Rolling-baseline window: each bar's stddev / duration is divided by the
/// mean of the prior `BaselineWindow` bars before being log-transformed.
[<Literal>]
let DefaultBaselineWindow = 100

type Params = {
    Emission: StateParams[]              // length K
    LogTransition: Matrix<float>         // K×K, with -inf in forbidden cells
    InitialLogPi: float[]                // length K
    BaselineWindow: int
}

/// Build the constrained transition matrix from per-state mean dwells (in
/// bars). Self-transition probability is 1 - 1/D. Off-diagonals are split
/// evenly across the *allowed* outgoing transitions for each row.
///
/// Allowed exits:
///   Hold     →  Fakeout, Trend
///   Fakeout  →  Hold
///   Trend    →  Hold
let defaultTransition (dwellBarsHold: float)
                      (dwellBarsFakeout: float)
                      (dwellBarsTrend: float) : Matrix<float> =
    let m = Matrix<float>.Build.Dense(K, K, System.Double.NegativeInfinity)
    let setRow (i: int) (probs: (int * float) list) =
        for (j, p) in probs do
            m.[i, j] <- log p
    // Hold
    let pStayH = 1.0 - 1.0 / dwellBarsHold
    let pLeaveH = (1.0 - pStayH) / 2.0
    setRow HOLD [ HOLD, pStayH; FAKEOUT, pLeaveH; TREND, pLeaveH ]
    // Fakeout (only exit is back to Hold)
    let pStayF = 1.0 - 1.0 / dwellBarsFakeout
    let pLeaveF = 1.0 - pStayF
    setRow FAKEOUT [ HOLD, pLeaveF; FAKEOUT, pStayF ]
    // Trend (only exit is back to Hold)
    let pStayT = 1.0 - 1.0 / dwellBarsTrend
    let pLeaveT = 1.0 - pStayT
    setRow TREND [ HOLD, pLeaveT; TREND, pStayT ]
    m

/// Hand-picked v0 parameters for BTCUSDT 18-BTC bars.
///   Hold:    volatility collapsed (~0.4× baseline), bars print fast
///            (~0.5× baseline duration), flow ~85% one-sided, price glued
///            to within ~5 bps.
///   Trend:   volatility ~normal, duration ~normal, flow ~70% one-sided,
///            no price-drift gate.
///   Fakeout: volatility slightly elevated, duration ~normal, flow ~55%
///            one-sided (mostly noise).
/// All scales (SdLogSd, SdLogDur) start at 0.5 — log-space σ of 0.5 means
/// ratios within roughly e^±0.5 ≈ [0.6, 1.65] are within 1σ of the mean.
let defaultEmission : StateParams[] = [|
    // 0: Hold
    { MuLogSd = -1.0; SdLogSd = 0.5
      MuLogDur = -0.7; SdLogDur = 0.5
      P = 0.85
      PriceDriftMax = 5.0e-4   // 5 bps — Hold must be glued
      UsePriceGate = true }
    // 1: Fakeout
    { MuLogSd = 0.2; SdLogSd = 0.5
      MuLogDur = 0.0; SdLogDur = 0.5
      P = 0.55
      PriceDriftMax = 0.0
      UsePriceGate = false }
    // 2: Trend
    { MuLogSd = 0.0; SdLogSd = 0.5
      MuLogDur = 0.0; SdLogDur = 0.5
      P = 0.70
      PriceDriftMax = 0.0
      UsePriceGate = false }
|]

let defaultParams : Params =
    {
        Emission = defaultEmission
        LogTransition = defaultTransition 30.0 5.0 300.0
        InitialLogPi = [| log (1.0 / 3.0); log (1.0 / 3.0); log (1.0 / 3.0) |]
        BaselineWindow = DefaultBaselineWindow
    }

/// Per-bar observation sequence built from a VolumeBar[]. The first
/// `BaselineWindow` bars are dropped — we need a baseline to compute
/// relative stddev / duration. Bars with degenerate stddev (=0) get a tiny
/// floor before taking the log.
type Sequence = {
    Obs: Obs[]
    BarIndex: int[]    // index into the original VolumeBar[] for each Obs
}

let private floor x = max x 1.0e-12

/// Compute relative-baseline observations. Each bar's stddev / duration is
/// divided by the mean of the prior `window` bars (excluding itself), then
/// log-transformed. The first `window` bars have no baseline so they're
/// dropped — we return both the Obs[] and the original bar indices for
/// alignment.
let buildSequence (window: int) (bars: VolumeBar[]) : Sequence =
    if bars.Length <= window then
        invalidArg "bars" (sprintf "need >%d bars, got %d" window bars.Length)
    let durs = bars |> Array.map (fun b -> float (b.EndUs - b.StartUs) * 1.0e-6)
    let sds = bars |> Array.map (fun b -> b.StdDev)
    let n = bars.Length - window
    let obs = Array.zeroCreate<Obs> n
    let idx = Array.zeroCreate<int> n
    // Use a running sum for the baseline window (rolls over each step).
    let mutable sumDur = 0.0
    let mutable sumSd = 0.0
    for i in 0 .. window - 1 do
        sumDur <- sumDur + durs.[i]
        sumSd <- sumSd + sds.[i]
    for k in 0 .. n - 1 do
        let i = window + k
        let bar = bars.[i]
        let baselineDur = sumDur / float window
        let baselineSd = sumSd / float window
        let dur = floor durs.[i]
        let sd = floor sds.[i]
        let drift = (bar.High - bar.Low) / bar.VWAP
        obs.[k] <- {
            LogRelSd = log (sd / floor baselineSd)
            LogRelDur = log (dur / floor baselineDur)
            N = bar.TradeCount
            K = bar.BuyCount
            PriceDrift = drift
        }
        idx.[k] <- i
        // Roll the window forward.
        sumDur <- sumDur + durs.[i] - durs.[i - window]
        sumSd <- sumSd + sds.[i] - sds.[i - window]
    { Obs = obs; BarIndex = idx }

/// Run forward-backward over a bar-level sequence with the given parameters.
let infer (p: Params) (seq: Sequence) : Output =
    let t = seq.Obs.Length
    if t < 1 then invalidArg "seq" "empty observation sequence"
    // Discrete HMM: bar steps are uniform → identical transition matrix
    // at every step.
    let logTrans = Array.create (t - 1) p.LogTransition
    let emissionMat = Matrix<float>.Build.Dense(K, t)
    for step in 0 .. t - 1 do
        for j in 0 .. K - 1 do
            emissionMat.[j, step] <- logEmission p.Emission.[j] seq.Obs.[step]
    let inp = {
        LogPi = p.InitialLogPi
        LogTrans = logTrans
        LogEmission = emissionMat
    }
    run inp
