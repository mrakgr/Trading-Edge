module TradingEdge.Simulation.EpisodeMCMC

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
    TargetMeanAndVariances: (float * float) list
    Label : string list
}

/// Episode as a generative process
type Episode<'t> = {
    Label: 't
    RateMean: float
    VolumeMean: float
    DurationParam: Distribution.Params
}

type EpisodeSeries<'a,'b> =
    | RandomlySampled of ('a -> (Episode<'b> * float)[])
    | FixedOrder of ('a -> Episode<'b>[])

/// Episode instance with sampled duration
type EpisodeInstance<'t> = {
    Episode: Episode<'t>
    Duration: float
    Weight: float
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

    /// Change the episode of a random instance to a different episode from the pool
    let changeEpisode (rng: Random) (availableEpisodes: (Episode<'t> * float)[]) (state: EpisodeInstance<'t>[]) : EpisodeInstance<'t>[] =
        let idx = rng.Next(state.Length)
        let newEpisode, weight = availableEpisodes.[rng.Next(availableEpisodes.Length)]
        let newState = Array.copy state
        newState.[idx] <- { Episode = newEpisode; Duration = state.[idx].Duration; Weight = weight }
        newState

    /// Sample a label from weighted options
    let sampleWeighted (rng: Random) (weights: seq<'a * float>) : 'a * float =
        let labels, probs = weights |> Seq.toArray |> Array.unzip
        let dist = Categorical(probs, rng)
        let i = dist.Sample()
        labels.[i], probs.[i]

    /// Run MCMC to sample episode instances that fill a target duration
    let run
        (config: Config)
        (series: EpisodeSeries<'a,'b>)
        (label: 'a)
        (targetDuration: float)
        (rng: Random)
        : EpisodeInstance<'b>[] =

        // Generate initial state based on series type
        let initial =
            match series with
            | FixedOrder getEpisodes ->
                let episodes = getEpisodes label
                episodes |> Array.map (fun ep ->
                    let dur = Distribution.sample rng ep.DurationParam
                    { Episode = ep; Duration = dur; Weight = 1.0 })
            | RandomlySampled getWeightedEpisodes ->
                let weighted_episodes = getWeightedEpisodes label
                Array.unfold (fun totalDur ->
                    if totalDur < targetDuration then
                        let ep, weight = sampleWeighted rng weighted_episodes
                        let dur = Distribution.sample rng ep.DurationParam
                        Some({ Episode = ep; Duration = dur; Weight = weight }, totalDur + dur)
                    else
                        None) 0.0

        // Scale durations to match target exactly
        let totalDur = initial |> Array.sumBy (fun i -> i.Duration)
        let scale = targetDuration / totalDur
        let mutable current = initial |> Array.map (fun i -> { i with Duration = i.Duration * scale })

        // Log-likelihood function
        let logLikelihood (state: EpisodeInstance<'b>[]) =
            state |> Array.sumBy (fun i ->
                let durLL = Distribution.logLikelihood i.Episode.DurationParam i.Duration
                let weightLL = log i.Weight
                durLL + weightLL)

        let mutable currentLL = logLikelihood current

        // MCMC loop with proposals based on series type
        for _ in 1 .. config.Iterations do
            let proposed =
                match series with
                | FixedOrder _ ->
                    // Only transfer duration for fixed order
                    if current.Length >= 2 then
                        transferDuration rng current
                    else
                        current
                | RandomlySampled getWeightedEpisodes ->
                    // Both transfer duration and change episode
                    if rng.NextDouble() <= config.TransferDurationProb && current.Length >= 2 then
                        transferDuration rng current
                    else
                        changeEpisode rng (getWeightedEpisodes label) current

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
    type Config = {
        MorningParams: Distribution.Params
        MidParams: Distribution.Params
        CloseParams: Distribution.Params
    }

    type Session =
        | Morning
        | Mid
        | Close

    let episodes =
        FixedOrder (fun _ -> [|
            {
                Label = Morning
                DurationParam = Distribution.LogNormal (45.0, 60.0)
                VolumeMean = sqrt 4.
                RateMean = sqrt 4.
            }
            {
                Label = Mid
                DurationParam = Distribution.LogNormal (240.0, 270.0)
                VolumeMean = 1.
                RateMean = 1.
            }
            {
                Label = Close
                DurationParam = Distribution.LogNormal (45.0, 60.0)
                VolumeMean = sqrt 4.
                RateMean = sqrt 4.
            }
        |])

// =============================================================================
// Subepisode Level Episodes
// =============================================================================

module TrendLevel =
    type Trend =
        | Generic
        
    let episodes =
        RandomlySampled (fun _ -> [|
            {
                Label = Generic
                DurationParam = Distribution.LogNormal (8.0, 10.0)
                VolumeMean = 1.0
                RateMean = 1.0
            }, 1.0
        |])

