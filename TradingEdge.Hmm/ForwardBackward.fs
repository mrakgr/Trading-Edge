module TradingEdge.Hmm.ForwardBackward

open MathNet.Numerics.LinearAlgebra
open TradingEdge.Hmm.LogMath

/// Per-timestep inputs and outputs for a conditional HMM where the transition
/// between step t-1 and step t depends on observed covariates (in our case,
/// inter-trade Δt via the CTMC).
///
/// LogPi       : length-K log prior over the initial state (for step 0).
/// LogTrans    : length T-1 array of K×K log-transition matrices. LogTrans.[k]
///               is the log of P( s_{k+1} | s_k ) for the gap between step k
///               and step k+1. Rows are the "from" state, columns the "to".
/// LogEmission : K×T matrix; LogEmission.[j, t] = log p( x_t | s_t = j ).
type Inputs = {
    LogPi: float[]
    LogTrans: Matrix<float>[]
    LogEmission: Matrix<float>
}

/// Forward messages (log-alpha) and backward messages (log-beta), each K×T.
/// Posterior gamma.[j, t] = P( s_t = j | x_{1:T} ) in log-space.
type Output = {
    LogAlpha: Matrix<float>
    LogBeta: Matrix<float>
    LogGamma: Matrix<float>
    LogLikelihood: float
}

let private validate (inp: Inputs) =
    let k = inp.LogPi.Length
    let t = inp.LogEmission.ColumnCount
    if inp.LogEmission.RowCount <> k then
        invalidArg "LogEmission" "row count must equal state count"
    if t = 0 then invalidArg "LogEmission" "need at least one observation"
    if inp.LogTrans.Length <> t - 1 then
        invalidArg "LogTrans" (sprintf "expected %d matrices, got %d" (t - 1) inp.LogTrans.Length)
    for m in inp.LogTrans do
        if m.RowCount <> k || m.ColumnCount <> k then
            invalidArg "LogTrans" "each matrix must be K×K"
    k, t

/// Forward-backward in log-space. Returns per-step posteriors and the total
/// log-likelihood log p( x_{1:T} ).
let run (inp: Inputs) : Output =
    let k, t = validate inp
    let alpha = Matrix<float>.Build.Dense(k, t)
    let beta = Matrix<float>.Build.Dense(k, t)

    // Forward pass: alpha[j, 0] = logPi[j] + logEmission[j, 0]
    for j in 0 .. k - 1 do
        alpha.[j, 0] <- inp.LogPi.[j] + inp.LogEmission.[j, 0]

    let buf = Array.zeroCreate<float> k
    for step in 1 .. t - 1 do
        let trans = inp.LogTrans.[step - 1]
        for j in 0 .. k - 1 do
            for i in 0 .. k - 1 do
                buf.[i] <- alpha.[i, step - 1] + trans.[i, j]
            alpha.[j, step] <- logSumExp buf + inp.LogEmission.[j, step]

    // Backward pass: beta[j, T-1] = 0 (log 1).
    for j in 0 .. k - 1 do
        beta.[j, t - 1] <- 0.0

    for step in t - 2 .. -1 .. 0 do
        let trans = inp.LogTrans.[step]
        for i in 0 .. k - 1 do
            for j in 0 .. k - 1 do
                buf.[j] <- trans.[i, j] + inp.LogEmission.[j, step + 1] + beta.[j, step + 1]
            beta.[i, step] <- logSumExp buf

    // Posterior: gamma[j, t] = alpha[j, t] + beta[j, t] - logLik(t).
    // Total log-likelihood can be read off at any column; use column 0 for
    // consistency with textbook presentations:
    //   log p(x_{1:T}) = logsumexp_j( alpha[j, T-1] )
    //                  = logsumexp_j( alpha[j, t] + beta[j, t] )  for any t.
    let colBuf = Array.zeroCreate<float> k
    for j in 0 .. k - 1 do colBuf.[j] <- alpha.[j, t - 1]
    let logLik = logSumExp colBuf

    let gamma = Matrix<float>.Build.Dense(k, t)
    for step in 0 .. t - 1 do
        for j in 0 .. k - 1 do
            gamma.[j, step] <- alpha.[j, step] + beta.[j, step] - logLik

    {
        LogAlpha = alpha
        LogBeta = beta
        LogGamma = gamma
        LogLikelihood = logLik
    }
