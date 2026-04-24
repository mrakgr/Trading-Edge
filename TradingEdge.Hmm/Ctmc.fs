module TradingEdge.Hmm.Ctmc

open MathNet.Numerics.LinearAlgebra
open MathNet.Numerics.LinearAlgebra.Double

/// Matrix exponential via scaling-and-squaring + order-6 Padé.
/// MathNet 5.0 does not ship a matrix exponential, so this is a compact
/// implementation of the standard algorithm.
///
/// The idea: e^A = (e^{A / 2^s})^{2^s}. We pick s so that ||A / 2^s||_∞ ≤ 0.5,
/// at which size the order-6 Padé approximation has relative error near
/// machine epsilon. Then we square the result s times to undo the scaling.
let matrixExp (a: Matrix<float>) : Matrix<float> =
    let n = a.RowCount
    if a.ColumnCount <> n then invalidArg "a" "must be square"
    let normInf = a.InfinityNorm()
    let s =
        if normInf < 0.5 then 0
        else int (ceil (log normInf / log 2.0)) + 1
    let scale = pown 2.0 s
    let b = a / scale

    let i = DenseMatrix.identity<float> n
    let b2 = b * b
    let b4 = b2 * b2
    let b6 = b4 * b2

    // Pade(6,6) numerator/denominator coefficients.
    let c0 = 1.0
    let c1 = 1.0 / 2.0
    let c2 = 5.0 / 44.0
    let c3 = 1.0 / 66.0
    let c4 = 1.0 / 792.0
    let c5 = 1.0 / 15840.0
    let c6 = 1.0 / 665280.0

    let even = c0 * i + c2 * b2 + c4 * b4 + c6 * b6
    let oddBase = c1 * i + c3 * b2 + c5 * b4
    let odd = b * oddBase
    let num = even + odd
    let den = even - odd
    let mutable r = den.Solve(num)
    for _ in 1 .. s do r <- r * r
    r

/// A rate matrix Q for a continuous-time Markov chain.
/// Off-diagonals ≥ 0 (transition rates). Diagonals = -row sum (so rows sum to 0).
type RateMatrix = {
    Q: Matrix<float>
    K: int
}

/// Build a rate matrix from a 2D array of off-diagonal rates.
/// offDiag.[i, j] for i ≠ j is the rate of i -> j. offDiag.[i, i] is ignored.
/// Throws if any off-diagonal is negative.
let fromOffDiagonals (offDiag: float[,]) : RateMatrix =
    let k = Array2D.length1 offDiag
    if Array2D.length2 offDiag <> k then invalidArg "offDiag" "must be square"
    let q = DenseMatrix.zero k k
    for i in 0 .. k - 1 do
        let mutable rowSum = 0.0
        for j in 0 .. k - 1 do
            if i <> j then
                let r = offDiag.[i, j]
                if r < 0.0 then invalidArg "offDiag" (sprintf "negative rate at [%d,%d]" i j)
                q.[i, j] <- r
                rowSum <- rowSum + r
        q.[i, i] <- -rowSum
    { Q = q; K = k }

/// Transition matrix over elapsed time dt: P(dt) = exp(Q * dt).
/// Each row is a probability distribution over destination states.
/// This goes through the general Padé path — fine for occasional use.
/// For per-trade evaluation over a long sequence, use TransitionCache.
let transitionMatrix (rm: RateMatrix) (dt: float) : Matrix<float> =
    matrixExp (rm.Q * dt)

/// Log-transition matrix, suitable for combining with log-emissions in
/// forward-backward.
let logTransitionMatrix (rm: RateMatrix) (dt: float) : Matrix<float> =
    (transitionMatrix rm dt).PointwiseLog()

/// Precomputed eigendecomposition of Q. Given Q = V · Λ · V⁻¹ with Λ diagonal
/// of eigenvalues and V the matrix of right eigenvectors, then
///
///     exp(Q·dt)  =  V · diag(e^{λᵢ·dt}) · V⁻¹
///
/// and each per-trade evaluation is O(K²) (two K×K complex matrix multiplies
/// and one diagonal exp) rather than the O(K³) full matrix exponential.
///
/// Q is real but not necessarily diagonalizable over the reals — CTMC
/// generators can have complex-conjugate eigenvalue pairs. We carry complex
/// types throughout and take the real part at the end; the imaginary parts
/// cancel exactly in exact arithmetic, and are near machine epsilon in
/// floating point.
type TransitionCache = {
    EigenValues: MathNet.Numerics.LinearAlgebra.Vector<System.Numerics.Complex>
    V: MathNet.Numerics.LinearAlgebra.Matrix<System.Numerics.Complex>
    VInv: MathNet.Numerics.LinearAlgebra.Matrix<System.Numerics.Complex>
    K: int
}

/// Build the transition cache from a rate matrix by eigendecomposing Q.
/// Falls back to the general matrixExp path if the eigendecomposition is
/// ill-conditioned (indicated by a large reconstruction error).
let buildCache (rm: RateMatrix) : TransitionCache option =
    let evd = rm.Q.Evd()
    let k = rm.K
    let eigVals = evd.EigenValues
    // Lift real eigenvectors to complex. When Q has complex-conjugate pairs
    // of eigenvalues, MathNet's EigenVectors matrix stores (realPart, imagPart)
    // in adjacent columns. We need the *true* complex eigenvectors for
    // V · diag(e^{λ·dt}) · V⁻¹ to give the right answer.
    let realV = evd.EigenVectors
    let complexV =
        MathNet.Numerics.LinearAlgebra.Complex.DenseMatrix.Create(k, k, fun _ _ ->
            System.Numerics.Complex.Zero)
    let mutable j = 0
    while j < k do
        let λ = eigVals.[j]
        if λ.Imaginary = 0.0 then
            for i in 0 .. k - 1 do
                complexV.[i, j] <- System.Numerics.Complex(realV.[i, j], 0.0)
            j <- j + 1
        else
            // Columns j and j+1 hold the real and imaginary parts of the
            // eigenvector for the conjugate pair (λ, conj λ).
            for i in 0 .. k - 1 do
                let re = realV.[i, j]
                let im = realV.[i, j + 1]
                complexV.[i, j]     <- System.Numerics.Complex(re,  im)
                complexV.[i, j + 1] <- System.Numerics.Complex(re, -im)
            j <- j + 2
    try
        let vinv = complexV.Inverse()
        // Sanity: reconstruct Q from V · Λ · V⁻¹ and check error.
        let diag =
            MathNet.Numerics.LinearAlgebra.Complex.DenseMatrix.CreateDiagonal(
                k, k, fun i -> eigVals.[i])
        let qRecon = complexV * diag * vinv
        let mutable err = 0.0
        for r in 0 .. k - 1 do
            for c in 0 .. k - 1 do
                let d = qRecon.[r, c].Real - rm.Q.[r, c]
                err <- max err (abs d)
        if err > 1e-9 then None
        else Some { EigenValues = eigVals; V = complexV; VInv = vinv; K = k }
    with _ -> None

/// Evaluate exp(Q·dt) via the cached eigendecomposition. Output is a real
/// K×K stochastic matrix.
let transitionFromCache (c: TransitionCache) (dt: float) : Matrix<float> =
    let k = c.K
    // diag( e^{λᵢ · dt} )
    let expDiag =
        MathNet.Numerics.LinearAlgebra.Complex.DenseMatrix.CreateDiagonal(
            k, k, fun i -> System.Numerics.Complex.Exp(c.EigenValues.[i] * System.Numerics.Complex(dt, 0.0)))
    let complexResult = c.V * expDiag * c.VInv
    let result = DenseMatrix.zero k k
    for i in 0 .. k - 1 do
        for j in 0 .. k - 1 do
            result.[i, j] <- complexResult.[i, j].Real
    result

/// Log-transition matrix via the cached eigendecomposition.
let logTransitionFromCache (c: TransitionCache) (dt: float) : Matrix<float> =
    (transitionFromCache c dt).PointwiseLog()
