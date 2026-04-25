module TradingEdge.Hmm.BtcModel

open MathNet.Numerics.LinearAlgebra
open TradingEdge.Hmm.BinanceLoader
open TradingEdge.Hmm.Ctmc
open TradingEdge.Hmm.Emission
open TradingEdge.Hmm.ForwardBackward

/// Three-state trend model. States 0/1/2 = Up / Consol / Down.
[<Literal>]
let K = 3

/// Hand-picked v0 parameters for BTCUSDT. The Bernoulli emission has only
/// three knobs: λ (per-unit-volume log-odds weight) and the mean dwell
/// times of the CTMC. See docs/hmm_bernoulli_emission.md for the
/// derivation and how to pick λ from a known-trending window.
type Params = {
    Lambda: float
    Emission: StateParams[]      // length K
    OffDiag: float[,]            // K×K rate matrix off-diagonal entries (1/s)
    InitialLogPi: float[]        // length K
}

let defaultParams : Params =
    let lambda = 0.15
    let emission = [|
        { D = +1.0 }   // Up
        { D =  0.0 }   // Consol
        { D = -1.0 }   // Down
    |]
    // 2-minute mean dwell across all three states.
    let meanDwellSec = 120.0
    let leaveRate = 1.0 / meanDwellSec
    let offDiag =
        Array2D.init K K (fun i j ->
            if i = j then 0.0 else leaveRate / 2.0)
    let initial = [| log (1.0 / 3.0); log (1.0 / 3.0); log (1.0 / 3.0) |]
    { Lambda = lambda; Emission = emission; OffDiag = offDiag; InitialLogPi = initial }

/// Per-trade sequence: inter-trade time in seconds, sign of aggression, and
/// volume. The first trade has DtSec = 0 (no prior trade) — we drop it from
/// the sequence so all returned arrays have length trades.Length - 1.
type Sequence = {
    DtSec: float[]
    Sign: float[]
    Volume: float[]
}

let buildSequence (trades: Trade[]) : Sequence =
    let n = trades.Length - 1
    if n < 1 then invalidArg "trades" "need at least 2 trades"
    let dt = Array.zeroCreate<float> n
    let sn = Array.zeroCreate<float> n
    let v = Array.zeroCreate<float> n
    for k in 0 .. n - 1 do
        let prev = trades.[k]
        let cur = trades.[k + 1]
        // 1 microsecond = 1e-6 seconds.
        let dtK = float (cur.TimestampUs - prev.TimestampUs) * 1.0e-6
        dt.[k] <- max dtK 1.0e-9   // guard against zero-duration ticks
        sn.[k] <- cur.Sign
        v.[k] <- cur.Quantity
    { DtSec = dt; Sign = sn; Volume = v }

/// Run forward-backward over a sequence with the given model parameters.
let infer (p: Params) (seq: Sequence) : Output =
    let t = seq.DtSec.Length
    let rm = fromOffDiagonals p.OffDiag
    let logTrans =
        match buildCache rm with
        | Some cache ->
            Array.init (t - 1) (fun k -> logTransitionFromCache cache seq.DtSec.[k + 1])
        | None ->
            Array.init (t - 1) (fun k -> logTransitionMatrix rm seq.DtSec.[k + 1])
    let emissionMat = Matrix<float>.Build.Dense(K, t)
    for step in 0 .. t - 1 do
        for j in 0 .. K - 1 do
            emissionMat.[j, step] <-
                logEmission p.Emission.[j] p.Lambda seq.Volume.[step] seq.Sign.[step]
    let inp = {
        LogPi = p.InitialLogPi
        LogTrans = logTrans
        LogEmission = emissionMat
    }
    run inp
