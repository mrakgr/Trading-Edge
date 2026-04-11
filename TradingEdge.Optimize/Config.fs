module TradingEdge.Optimize.Config

open TradingEdge.Parsing.VwapSystem

/// Fixed VWAP-system parameters used by the evaluation server. These
/// mirror scripts/config.fsx verbatim -- they're expected to be edited in
/// source (recompile to change) since the optimizer treats them as the
/// backdrop against which the swept parameters are evaluated.
let positionSize = 30000.0
let referenceVol = 5.82e-4
let lossLimitPct = 0.085
let lossLimit = positionSize * lossLimitPct
let basePct = 0.005
let decay = 0.9

/// Baseline exponents / pcts used when a client doesn't provide its own.
// TODO: drop the exponent parameterization entirely and work with raw pcts
// directly. The `basePct * decay ** exponent` scheme was originally useful
// for CMA-ES to explore an exponentially-spaced region, but it's indirect
// and now just obscures what the actual percentages are. A direct pcts
// bounds list (e.g. [0.005..0.05]) with CMA-ES operating in log space
// would be cleaner. Applies here AND in optimize_cma.py (exponents_to_pcts,
// start-exponents CLI flag) AND in the /eval/exponents server endpoint.
let exponents = [| -8.69; -1.10; -16.27; -16.73 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** i))

let bandVol = 0.0
let commissionPerShare = 0.0035
let delayMs = 100.0
let percentile = 0.05
let rejectionRate = 0.30

let fillParams =
    { Percentile = percentile
      DelayMs = delayMs
      CommissionPerShare = commissionPerShare
      RejectionRate = rejectionRate
      Rng = None }
