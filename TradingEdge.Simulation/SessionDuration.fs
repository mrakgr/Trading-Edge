module TradingEdge.Simulation.SessionDuration

open System
open MathNet.Numerics.Distributions

// =============================================================================
// Distribution Utilities
// =============================================================================

module Distribution =
    /// Distribution specification for positive values (median/mean parameterization)
    type Params =
        | LogNormal of median: float * mean: float

    /// Convert median/mean to log-normal mu/sigma parameters
    let logNormalParams (median: float) (mean: float) : float * float =
        let mu = log median
        let sigma = sqrt(2.0 * log(mean / median))
        (mu, sigma)

    /// Get the standard deviation of the distribution
    let stdDev (dist: Params) : float =
        match dist with
        | LogNormal (median, mean) ->
            let (mu, sigma) = logNormalParams median mean
            MathNet.Numerics.Distributions.LogNormal(mu, sigma).StdDev

    /// Compute log-likelihood for a value under the given distribution
    let logLikelihood (dist: Params) (value: float) : float =
        match dist with
        | LogNormal (median, mean) ->
            if value <= 0.0 then
                Double.NegativeInfinity
            else
                let (mu, sigma) = logNormalParams median mean
                let d = MathNet.Numerics.Distributions.LogNormal(mu, sigma)
                d.DensityLn(value)

    /// Sample a value from the given distribution
    let sample (rng: Random) (dist: Params) : float =
        match dist with
        | LogNormal (median, mean) ->
            let (mu, sigma) = logNormalParams median mean
            MathNet.Numerics.Distributions.LogNormal(mu, sigma, rng).Sample()

// =============================================================================
// Core Types
// =============================================================================

/// A single trade
type Trade = {
    Time: float
    Price: float
    Size: int
    TargetMean: float
    TargetSigma: float
    Label : string list
}

/// Episode as a generative process
type Episode<'t> = {
    Label: 't
    DurationParam: Distribution.Params
}

/// Episode instance with sampled duration
type EpisodeInstance<'t> = {
    Episode: Episode<'t>
    Duration: float
}

// =============================================================================
// Generic MCMC Module
// =============================================================================

module MCMC =
    type Config = {
        Iterations: int
        TransferDurationProb: float
    }

    let defaultConfig = {
        Iterations = 10000
        TransferDurationProb = 0.7
    }

    /// Pick two distinct random indices from 0 to n-1
    let pickTwoDistinctIndices (rng: Random) (n: int) : int * int =
        let idx1 = rng.Next(n)
        let idx2 = (idx1 + 1 + rng.Next(n - 1)) % n
        (idx1, idx2)

    /// Transfer duration between two episode instances
    let transferDuration (rng: Random) (state: EpisodeInstance<'t>[]) : EpisodeInstance<'t>[] =
        let idx1, idx2 = pickTwoDistinctIndices rng state.Length
        let proposalStdDev = min (Distribution.stdDev state.[idx1].Episode.DurationParam) (Distribution.stdDev state.[idx2].Episode.DurationParam)
        let delta = Normal.Sample(rng, 0.0, proposalStdDev)
        let newState = Array.copy state
        newState.[idx1] <- { state.[idx1] with Duration = state.[idx1].Duration + delta }
        newState.[idx2] <- { state.[idx2] with Duration = state.[idx2].Duration - delta }
        newState

    /// Run MCMC to sample episode instances that fill a target duration
    let run
        (config: Config)
        (episodes: Episode<'t>[])
        (targetDuration: float)
        (rng: Random)
        : EpisodeInstance<'t>[] =

        // Generate initial state
        let initial =
            episodes |> Array.map (fun ep ->
                let dur = Distribution.sample rng ep.DurationParam
                { Episode = ep; Duration = dur })

        // Scale durations to match target exactly
        let totalDur = initial |> Array.sumBy (fun i -> i.Duration)
        let scale = targetDuration / totalDur
        let mutable current = initial |> Array.map (fun i -> { i with Duration = i.Duration * scale })

        // Log-likelihood function
        let logLikelihood (state: EpisodeInstance<'t>[]) =
            state |> Array.sumBy (fun i ->
                Distribution.logLikelihood i.Episode.DurationParam i.Duration)

        let mutable currentLL = logLikelihood current

        // MCMC loop
        for _ in 1 .. config.Iterations do
            let proposed =
                if current.Length >= 2 then
                    transferDuration rng current
                else
                    current

            let proposedLL = logLikelihood proposed
            let logAcceptRatio = proposedLL - currentLL

            if log(rng.NextDouble()) < logAcceptRatio then
                current <- proposed
                currentLL <- proposedLL

        current


// =============================================================================
// Session Level Episodes
// =============================================================================

module SessionLevel =
    type Session =
        | Morning
        | Mid
        | Close

    let episodes : Episode<Session>[] = [|
        {
            Label = Morning
            DurationParam = Distribution.LogNormal (45.0, 60.0)
        }
        {
            Label = Mid
            DurationParam = Distribution.LogNormal (180.0, 270.0)
        }
        {
            Label = Close
            DurationParam = Distribution.LogNormal (45.0, 60.0)
        }
    |]

