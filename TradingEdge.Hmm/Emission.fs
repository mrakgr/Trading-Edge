module TradingEdge.Hmm.Emission

/// Per-state directional propensity for the Bernoulli-on-sign emission.
///
///     P(sign = +1 | state, v)  =  σ( λ · D · v )
///
/// where σ is the logistic, sign ∈ {+1, -1} is the trade aggressor direction
/// (+1 = buyer-aggressive, -1 = seller-aggressive), v is the trade's volume,
/// and λ is a single global scale parameter.
///
///   D = +1   →  Up regime — favors +1 signs in proportion to volume
///   D =  0   →  Consol — uninformative; emission is log 0.5 regardless of sign
///   D = -1   →  Down regime — favors -1 signs in proportion to volume
///
/// The doc at docs/hmm_bernoulli_emission.md derives this in detail.
type StateParams = {
    D: float
}

/// Numerically stable softplus: log(1 + exp x). For large positive x the naive
/// form overflows; for large negative x it underflows. The branch below keeps
/// both arms within representable range.
let private softplus (x: float) =
    if x >= 0.0 then x + log (1.0 + exp (-x))
    else log (1.0 + exp x)

/// log P(sign | state, v).
///
/// Identity used: log σ(z) = -softplus(-z), where z = sign · D · λ · v.
let logEmission (p: StateParams) (lambda: float) (v: float) (sign: float) =
    let z = sign * p.D * lambda * v
    -softplus (-z)
