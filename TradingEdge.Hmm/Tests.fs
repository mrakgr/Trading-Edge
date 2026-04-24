module TradingEdge.Hmm.Tests

open System
open MathNet.Numerics.Distributions
open MathNet.Numerics.LinearAlgebra
open MathNet.Numerics.LinearAlgebra.Double
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

/// Check: for every column t, Σ_j exp(gamma[j, t]) = 1.
let private assertPosteriorNormalized (gamma: Matrix<float>) =
    let buf = Array.zeroCreate gamma.RowCount
    for t in 0 .. gamma.ColumnCount - 1 do
        for j in 0 .. gamma.RowCount - 1 do buf.[j] <- gamma.[j, t]
        let s = logSumExp buf
        if abs s > 1e-9 then
            failwithf "posterior at t=%d does not normalize: logsumexp = %g" t s

/// Check: log-likelihood read from alpha_T-1 equals the "beta-mediated" one
/// at step 0.  log p(x) = logsumexp_j( logPi[j] + logEmission[j,0] + beta[j,0] ).
let private assertLogLikConsistent (inp: Inputs) (out: Output) =
    let k = inp.LogPi.Length
    let buf = Array.zeroCreate k
    for j in 0 .. k - 1 do
        buf.[j] <- inp.LogPi.[j] + inp.LogEmission.[j, 0] + out.LogBeta.[j, 0]
    let alt = logSumExp buf
    if abs (alt - out.LogLikelihood) > 1e-6 then
        failwithf "loglik inconsistent: forward=%g backward-at-0=%g" out.LogLikelihood alt

/// Sample a discrete distribution given log-probabilities.
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

/// Synthetic 2-state test: well-separated volume-scaled Gaussians, constant
/// Δt = 1s, CTMC with ~60s dwell. Recover >90% of the true states via the
/// posterior argmax.
let runSyntheticTwoStateTest () =
    let rng = Random(42)
    let trueParams = [|
        { Mu =  0.0004; Sigma = 0.0008 }   // "up"
        { Mu = -0.0004; Sigma = 0.0008 }   // "down"
    |]
    let offDiag = Array2D.init 2 2 (fun i j -> if i = j then 0.0 else 1.0 / 60.0)
    let rm = fromOffDiagonals offDiag
    let dt = 1.0
    let trans = transitionMatrix rm dt
    let logTrans = trans.PointwiseLog()

    let n = 2000
    let trueStates = Array.zeroCreate<int> n
    let dlogps = Array.zeroCreate<float> n
    let volumes = Array.init n (fun _ -> 100.0 + rng.NextDouble() * 900.0)

    // Sample ground truth sequence.
    trueStates.[0] <- if rng.NextDouble() < 0.5 then 0 else 1
    for t in 1 .. n - 1 do
        let prev = trueStates.[t - 1]
        let row = [| log trans.[prev, 0]; log trans.[prev, 1] |]
        trueStates.[t] <- sampleLog rng row
    for t in 0 .. n - 1 do
        let s = trueStates.[t]
        let v = volumes.[t]
        let mean = trueParams.[s].Mu * v
        let std = trueParams.[s].Sigma * sqrt v
        dlogps.[t] <- Normal.Sample(rng, mean, std)

    // Run inference with the true parameters.
    let k = 2
    let emissionMat = Matrix<float>.Build.Dense(k, n)
    for t in 0 .. n - 1 do
        for j in 0 .. k - 1 do
            emissionMat.[j, t] <- logEmission trueParams.[j] volumes.[t] dlogps.[t]

    let logPi = [| log 0.5; log 0.5 |]
    let logTransArr = Array.create (n - 1) logTrans

    let inp = {
        LogPi = logPi
        LogTrans = logTransArr
        LogEmission = emissionMat
    }
    let out = run inp

    assertPosteriorNormalized out.LogGamma
    assertLogLikConsistent inp out

    let mutable matched = 0
    for t in 0 .. n - 1 do
        if argmaxCol out.LogGamma t = trueStates.[t] then
            matched <- matched + 1
    let accuracy = float matched / float n
    printfn "[test] 2-state synthetic: %d / %d states recovered (%.3f), loglik = %g"
        matched n accuracy out.LogLikelihood
    if accuracy < 0.90 then
        failwithf "recovery accuracy too low: %.3f" accuracy
