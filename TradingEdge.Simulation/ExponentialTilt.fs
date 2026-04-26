module TradingEdge.Simulation.ExponentialTilt

/// Given centroids (value, weight) and a target mean,
/// finds lambda via Newton's method and returns tilted weights.
let tiltWeights (centroids: (float * float) array) (targetMean: float) =
    let values = centroids |> Array.map fst
    let weights = centroids |> Array.map snd

    // Tilted mean and its derivative (= tilted variance) for a given lambda
    let tiltedMeanAndVariance lambda =
        let shifted = values |> Array.map (fun v -> lambda * v)
        let maxShift = Array.max shifted
        let unnormalized = Array.map2 (fun w s -> w * exp (s - maxShift)) weights shifted
        let z = Array.sum unnormalized
        let tiltedWeights = unnormalized |> Array.map (fun u -> u / z)
        let mean = Array.map2 (fun tw v -> tw * v) tiltedWeights values |> Array.sum
        let variance =
            Array.map2 (fun tw v -> tw * (v - mean) * (v - mean)) tiltedWeights values
            |> Array.sum
        mean, variance

    // Newton's method: solve tiltedMean(lambda) = targetMean
    let rec newton lambda iter =
        if iter > 100 then failwith "The newton iterations failed."
        else
            let mean, variance = tiltedMeanAndVariance lambda
            let error = mean - targetMean
            if abs error < 1e-10 then lambda
            elif variance < 1e-15 then lambda
            else newton (lambda - error / variance) (iter + 1)

    let lambda = newton 0.0 0

    // Compute final tilted weights
    let shifted = values |> Array.map (fun v -> lambda * v)
    let maxShift = Array.max shifted
    let unnormalized = Array.map2 (fun w s -> w * exp (s - maxShift)) weights shifted
    let z = Array.sum unnormalized
    let tiltedWeights = unnormalized |> Array.map (fun u -> u / z)

    lambda, tiltedWeights
