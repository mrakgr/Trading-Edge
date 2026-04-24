module TradingEdge.Hmm.Emission

open MathNet.Numerics.Distributions

/// State-specific drift and unit-volume variance for the volume-scaled
/// Gaussian emission:
///
///     dlog p | s, v  ~  Normal( mu_s * v,  sigma_s^2 * v )
///
/// where dlog p is the log-return across a single trade and v is that trade's
/// volume. Drift and variance both scale linearly with volume — no dependence
/// on inter-trade time (the transition handles time via the CTMC).
type StateParams = {
    Mu: float
    Sigma: float
}

/// log N( x ; mu * v, sigma^2 * v ).
let logEmission (p: StateParams) (v: float) (dlogp: float) =
    let mean = p.Mu * v
    let std = p.Sigma * sqrt v
    Normal.PDFLn(mean, std, dlogp)
