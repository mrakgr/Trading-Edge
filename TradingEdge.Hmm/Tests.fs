module TradingEdge.Hmm.Tests

open System
open MathNet.Numerics.LinearAlgebra
open TradingEdge.Hmm.ForwardBackward
open TradingEdge.Hmm.Ctmc
open TradingEdge.Hmm.Emission
open TradingEdge.Hmm.LogMath

let private argmaxCol (m: Matrix<float>) (col: int) =
    let mutable best = 0
    let mutable bestV = m.[0, col]
    for i in 1 .. m.RowCount - 1 do
        if m.[i, col] > bestV then
            best <- i
            bestV <- m.[i, col]
    best

/// Σ_j exp(γ[j, t]) = 1 at every t (in non-log space).
let private assertPosteriorNormalized (gamma: Matrix<float>) =
    let buf = Array.zeroCreate gamma.RowCount
    for t in 0 .. gamma.ColumnCount - 1 do
        for j in 0 .. gamma.RowCount - 1 do buf.[j] <- gamma.[j, t]
        let s = logSumExp buf
        if abs s > 1e-9 then
            failwithf "posterior at t=%d does not normalize: logsumexp = %g" t s

/// Loglik computed at step T-1 (forward) should equal the loglik computed at
/// step 0 using the backward β values (the "alternate route").
let private assertLogLikConsistent (inp: Inputs) (out: Output) =
    let k = inp.LogPi.Length
    let buf = Array.zeroCreate k
    for j in 0 .. k - 1 do
        buf.[j] <- inp.LogPi.[j] + inp.LogEmission.[j, 0] + out.LogBeta.[j, 0]
    let alt = logSumExp buf
    if abs (alt - out.LogLikelihood) > 1e-6 then
        failwithf "loglik inconsistent: forward=%g backward-at-0=%g" out.LogLikelihood alt

/// Sample a category from a row of log-probabilities.
let private sampleLog (rng: Random) (logp: float[]) =
    let m = Array.max logp
    let probs = logp |> Array.map (fun x -> exp (x - m))
    let total = Array.sum probs
    let u = rng.NextDouble() * total
    let mutable acc = 0.0
    let mutable idx = 0
    let mutable found = false
    while not found && idx < probs.Length do
        acc <- acc + probs.[idx]
        if u <= acc then found <- true else idx <- idx + 1
    min idx (probs.Length - 1)

/// Synthetic 3-state test: Up / Consol / Down with the Bernoulli-on-sign
/// emission. We generate a sequence by sampling state transitions according
/// to a CTMC (constant Δt = 1s), then for each state sample a sign with
/// probability σ(D · λ · v). Recovery is measured against the true state
/// sequence on the directional steps only — because Consol's sign is
/// uniform ±1, no inference can distinguish Consol from "wrong call" on a
/// per-trade basis. Aggregate accuracy across the whole sequence is the
/// metric: we want > 70% of state labels recovered (random would give
/// ~33%).
let runSyntheticBernoulliTest () =
    let rng = Random(42)
    let lambda = 1.0
    let trueParams = [|
        { D = +1.0 }   // Up
        { D =  0.0 }   // Consol
        { D = -1.0 }   // Down
    |]
    // 60-second mean dwell, equally split between two destination states.
    let leaveRate = 1.0 / 60.0
    let offDiag = Array2D.init 3 3 (fun i j -> if i = j then 0.0 else leaveRate / 2.0)
    let rm = fromOffDiagonals offDiag
    let dt = 1.0
    let trans = transitionMatrix rm dt
    let logTrans = trans.PointwiseLog()

    let n = 3000
    let trueStates = Array.zeroCreate<int> n
    let signs = Array.zeroCreate<float> n
    let volumes = Array.init n (fun _ -> 1.0 + rng.NextDouble() * 5.0)   // 1-6 BTC

    // Sample state sequence from the CTMC.
    let logPi = [| log (1.0 / 3.0); log (1.0 / 3.0); log (1.0 / 3.0) |]
    trueStates.[0] <- sampleLog rng logPi
    for t in 1 .. n - 1 do
        let prev = trueStates.[t - 1]
        let row = [| log trans.[prev, 0]; log trans.[prev, 1]; log trans.[prev, 2] |]
        trueStates.[t] <- sampleLog rng row

    // Sample signs from the Bernoulli emission.
    for t in 0 .. n - 1 do
        let s = trueStates.[t]
        let v = volumes.[t]
        let pPlus = 1.0 / (1.0 + exp (-(trueParams.[s].D * lambda * v)))
        signs.[t] <- if rng.NextDouble() < pPlus then +1.0 else -1.0

    // Run inference with the true parameters.
    let k = 3
    let emissionMat = Matrix<float>.Build.Dense(k, n)
    for t in 0 .. n - 1 do
        for j in 0 .. k - 1 do
            emissionMat.[j, t] <- logEmission trueParams.[j] lambda volumes.[t] signs.[t]

    let logTransArr = Array.create (n - 1) logTrans
    let inp = {
        LogPi = logPi
        LogTrans = logTransArr
        LogEmission = emissionMat
    }
    let out = run inp
    assertPosteriorNormalized out.LogGamma
    assertPosteriorNormalized out.LogGammaFiltered
    assertLogLikConsistent inp out

    let mutable matched = 0
    for t in 0 .. n - 1 do
        if argmaxCol out.LogGamma t = trueStates.[t] then
            matched <- matched + 1
    let accuracy = float matched / float n
    printfn "[test] 3-state Bernoulli: %d / %d recovered (%.3f), loglik = %g"
        matched n accuracy out.LogLikelihood
    if accuracy < 0.70 then
        failwithf "recovery accuracy too low: %.3f" accuracy
