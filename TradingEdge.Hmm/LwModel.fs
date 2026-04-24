module TradingEdge.Hmm.LwModel

open MathNet.Numerics.LinearAlgebra
open TradingEdge.Hmm.Ctmc
open TradingEdge.Hmm.Emission
open TradingEdge.Hmm.ForwardBackward
open TradingEdge.Orb.TradeLoader

/// Three-state trend model. States 0/1/2 = Up / Consol / Down.
[<Literal>]
let K = 3

/// Hand-picked v0 parameters for LW. All drift / variance values are per
/// unit of traded share (the emission is (mu * v, sigma^2 * v) on log-returns).
///
/// Numbers below are order-of-magnitude guesses — they WILL be adjusted after
/// looking at the first posterior plot. Keep the up/down drifts symmetric and
/// the consol drift at zero; let volatility in consol be smaller than the
/// directional states.
///
/// Dwell times set the CTMC rates: mean dwell ≈ 1 / |Q[i,i]|, in seconds.
type Params = {
    Emission: StateParams[]      // length K
    OffDiag: float[,]            // K×K rate matrix (off-diagonal rates, 1/s)
    InitialLogPi: float[]        // length K
}

let defaultParams : Params =
    let upDown = 2.0e-6
    let sigmaDir = 1.0e-5
    let sigmaCon = 5.0e-6
    let emission = [|
        { Mu =  upDown; Sigma = sigmaDir }   // Up
        { Mu =  0.0;    Sigma = sigmaCon }   // Consol
        { Mu = -upDown; Sigma = sigmaDir }   // Down
    |]
    // Expected dwell times (seconds): Up/Down ~ 300s, Consol ~ 600s.
    // Split leaving rate evenly between the two destination states.
    let rUp = 1.0 / 300.0
    let rCon = 1.0 / 600.0
    let rDn = 1.0 / 300.0
    let offDiag =
        Array2D.init K K (fun i j ->
            if i = j then 0.0
            else
                match i with
                | 0 -> rUp / 2.0      // Up -> {Consol, Down}
                | 1 -> rCon / 2.0     // Consol -> {Up, Down}
                | 2 -> rDn / 2.0      // Down -> {Up, Consol}
                | _ -> 0.0)
    let initial = [| log (1.0 / 3.0); log (1.0 / 3.0); log (1.0 / 3.0) |]
    { Emission = emission; OffDiag = offDiag; InitialLogPi = initial }

/// Per-trade sequence extracted from a binary Trade[]: inter-trade gap in
/// seconds (first entry is 0), log-return vs previous trade (first entry is 0),
/// and the trade's volume (float).
type Sequence = {
    DtSec: float[]
    DlogP: float[]
    Volume: float[]
}

/// Build the per-trade Δt / dlogP / v sequence from the loaded Trade[].
/// Trades are assumed sorted ascending in time. Sequences start from index 1
/// because both Δt and dlogP are pairwise quantities — the returned arrays all
/// have length trades.Length - 1.
let buildSequence (trades: Trade[]) : Sequence =
    let n = trades.Length - 1
    if n < 1 then invalidArg "trades" "need at least 2 trades"
    let dt = Array.zeroCreate<float> n
    let dlp = Array.zeroCreate<float> n
    let v = Array.zeroCreate<float> n
    // 1 KiloTick = 1000 ticks = 1e-4 seconds (tick = 100 ns).
    let kiloTickToSec = 1.0e-4
    for k in 0 .. n - 1 do
        let prev = trades.[k]
        let cur = trades.[k + 1]
        let dtK =
            float (cur.KiloTicksFromBase - prev.KiloTicksFromBase) * kiloTickToSec
        dt.[k] <- max dtK 1.0e-6   // guard against zero-duration bursts
        dlp.[k] <- log cur.Price - log prev.Price
        v.[k] <- float cur.Volume
    { DtSec = dt; DlogP = dlp; Volume = v }

/// Run forward-backward over a sequence with the given model parameters.
/// Returns the raw Output — per-trade posteriors are `exp(output.LogGamma)`
/// viewed column by column, each column of length K.
let infer (p: Params) (seq: Sequence) : Output =
    let t = seq.DtSec.Length
    let rm = fromOffDiagonals p.OffDiag
    // One-time eigendecomposition of Q; fall back to the Padé path if the
    // eigendecomposition is ill-conditioned.
    let logTrans =
        match buildCache rm with
        | Some cache ->
            Array.init (t - 1) (fun k -> logTransitionFromCache cache seq.DtSec.[k + 1])
        | None ->
            Array.init (t - 1) (fun k -> logTransitionMatrix rm seq.DtSec.[k + 1])
    // Log-emission matrix K × T.
    let emissionMat = Matrix<float>.Build.Dense(K, t)
    for step in 0 .. t - 1 do
        for j in 0 .. K - 1 do
            emissionMat.[j, step] <-
                logEmission p.Emission.[j] seq.Volume.[step] seq.DlogP.[step]
    let inp = {
        LogPi = p.InitialLogPi
        LogTrans = logTrans
        LogEmission = emissionMat
    }
    run inp
