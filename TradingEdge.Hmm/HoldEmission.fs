module TradingEdge.Hmm.HoldEmission

open TradingEdge.Hmm.LogMath

/// Per-state emission parameters for the bar-level hold detector.
///
/// A bar emits four observables:
///   1. log rel_stddev   ~ Normal(MuLogSd, SdLogSd)
///   2. log rel_dur      ~ Normal(MuLogDur, SdLogDur)
///   3. (k, n)           ~ symmetric mixture
///                         0.5·Binomial(n, P) + 0.5·Binomial(n, 1-P)
///   4. price drift      hard gate (Hold only — see PriceDriftMax)
///
/// The symmetric Binomial mixture lets a single Hold state cover one-sided
/// flow in *either* direction (squeezes from above and below). P encodes
/// "how one-sided" — Hold has P near 0.85, Trend ~0.7, Fakeout ~0.55.
///
/// PriceDriftMax is applied externally to zero out Hold's likelihood when the
/// bar's range exceeds the gate.
type StateParams = {
    MuLogSd: float
    SdLogSd: float
    MuLogDur: float
    SdLogDur: float
    P: float
    PriceDriftMax: float    // ignored unless gate is enabled for this state
    UsePriceGate: bool
}

let private gaussianLogPdf (mu: float) (sd: float) (x: float) =
    let z = (x - mu) / sd
    -0.5 * z * z - log sd - 0.5 * log (2.0 * System.Math.PI)

/// log of the symmetric Binomial mixture
///   log( 0.5·Binom(n, k; p) + 0.5·Binom(n, k; 1-p) )
/// dropping the `log C(n,k)` constant (state-independent, cancels in posteriors).
let private logSymBinom (p: float) (n: int) (k: int) =
    let logP = log p
    let log1mP = log (1.0 - p)
    let arm1 = float k * logP + float (n - k) * log1mP
    let arm2 = float (n - k) * logP + float k * log1mP
    logSumExp [| arm1; arm2 |] - log 2.0

/// Per-bar observation tuple: log relative stddev, log relative duration,
/// trade count n, buyer-aggressive count k, and the bar's price drift
/// (relative range, e.g. (High - Low) / VWAP).
type Obs = {
    LogRelSd: float
    LogRelDur: float
    N: int
    K: int
    PriceDrift: float
}

let logEmission (p: StateParams) (o: Obs) =
    if p.UsePriceGate && o.PriceDrift > p.PriceDriftMax then
        System.Double.NegativeInfinity
    else
        gaussianLogPdf p.MuLogSd p.SdLogSd o.LogRelSd
        + gaussianLogPdf p.MuLogDur p.SdLogDur o.LogRelDur
        + logSymBinom p.P o.N o.K
