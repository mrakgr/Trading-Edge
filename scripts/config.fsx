#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"
open TradingEdge.Parsing.VwapSystem

let positionSize = 30000.0
let referenceVol = 5.82e-4
let lossLimitPct = 0.085
let lossLimit = positionSize * lossLimitPct
let basePct = 0.005
let decay = 0.9
let exponents = [| -8.69; -1.10; -16.27; -16.73 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** i))
let bandVol = 0.0
let commissionPerShare = 0.0035
let delayMs = 100.0
let percentile = 0.05
let rejectionRate = 0.30
let fillParams = { Percentile = percentile; DelayMs = delayMs; CommissionPerShare = commissionPerShare; RejectionRate = rejectionRate; Rng = None }
