#r "../TradingEdge.Parsing/bin/Debug/net10.0/TradingEdge.Parsing.dll"
open TradingEdge.Parsing.VwapSystem

let positionSize = 30000.0
let referenceVol = 5.82e-4
let lossLimitPct = 0.085
let lossLimit = positionSize * lossLimitPct
let basePct = 0.005
let decay = 0.9
let exponents = [| -14; -7; -9; -18 |]
let pcts = exponents |> Array.map (fun i -> basePct * (decay ** float i))
let bandVol = 0.2
let commissionPerShare = 0.0035
let delayMs = 100.0
let percentile = 0.1
let fillParams = { Percentile = percentile; DelayMs = delayMs; CommissionPerShare = commissionPerShare }
