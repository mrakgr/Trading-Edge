module TradingEdge.Simulation.EpisodeMCMC

open System
open MathNet.Numerics.Distributions

// =============================================================================
// Core Types
// =============================================================================

/// Generic episode with a label and duration
type Episode<'label> = {
    Label: 'label
    Duration: float
}

/// Day session types
type DaySession =
    | Morning
    | Mid
    | Close

/// Which side of the book the hold is pinned to
type HoldSide =
    | Bid
    | Ask
    | Neutral

type Direction = Up | Down

type Intensity = Strong | Mid | Weak

type HoldDuration = Short | Medium | Long

/// Trend types within sessions
type Trend =
    | Move of Direction * Intensity
    | Hold of HoldSide * Intensity * HoldDuration
    | Consolidation

// =============================================================================
// Generic MCMC Module
// =============================================================================

module MCMC =
    type Config = {
        Iterations: int
    }

    let defaultConfig = { Iterations = 10000 }

    /// Pick two distinct random indices from 0 to n-1
    let pickTwoDistinctIndices (rng: Random) (n: int) : int * int =
        let idx1 = rng.Next(n)
        let idx2 = (idx1 + 1 + rng.Next(n - 1)) % n
        (idx1, idx2)

    /// Transfer duration between two episodes, using min stddev of the pair as proposal scale
    let transferDuration (stddev : 'a -> float) (rng: Random) (state: Episode<'a>[]) : Episode<'a>[] =
        let idx1, idx2 = pickTwoDistinctIndices rng state.Length
        let proposalStdDev = min (stddev state.[idx1].Label) (stddev state.[idx2].Label)
        let delta = Normal.Sample(rng, 0.0, proposalStdDev)
        let newState = Array.copy state
        newState.[idx1] <- { state.[idx1] with Duration = state.[idx1].Duration + delta }
        newState.[idx2] <- { state.[idx2] with Duration = state.[idx2].Duration - delta }
        newState

    /// Change the label of a random episode to a uniformly sampled label from the given array
    let changeLabel (rng: Random) (allLabels: 'a[]) (state: Episode<'a>[]) : Episode<'a>[] =
        let idx = rng.Next(state.Length)
        let newLabel = allLabels.[rng.Next(allLabels.Length)]

        let newState = Array.copy state
        newState.[idx] <- { state.[idx] with Label = newLabel }
        newState

    /// Sample a label from weighted options
    let sampleWeighted (rng: Random) (weights: seq<'a * float>) : 'a =
        let labels, probs = weights |> Seq.toArray |> Array.unzip
        let dist = Categorical(probs, rng)
        labels.[dist.Sample()]

    /// Run Metropolis-Hastings MCMC sampler
    /// Returns a single sample from the posterior after running for the specified iterations
    let run
        (config: Config)
        (logLikelihood: 'state -> float)
        (propose: Random -> 'state -> 'state option)
        (initial: 'state)
        (rng: Random)
        : 'state =

        let mutable current = initial
        let mutable currentLL = logLikelihood current

        for _ in 1 .. config.Iterations do
            match propose rng current with
            | Some proposed ->
                let proposedLL = logLikelihood proposed
                let logAcceptRatio = proposedLL - currentLL

                if log(rng.NextDouble()) < logAcceptRatio then
                    current <- proposed
                    currentLL <- proposedLL
            | None ->
                () // Invalid move, reject

        current

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
// Session Level (Day -> Sessions)
// =============================================================================

module SessionLevel =
    type Config = {
        MorningParams: Distribution.Params
        MidParams: Distribution.Params
        CloseParams: Distribution.Params
    }

    let defaultConfig = {
        MorningParams = Distribution.LogNormal (45.0, 60.0)
        MidParams = Distribution.LogNormal (240.0, 270.0)
        CloseParams = Distribution.LogNormal (45.0, 60.0)
    }

    /// State for session-level MCMC: array of (session, duration) pairs
    type State = Episode<DaySession>[]

    let private getParams (config: Config) (session: DaySession) : Distribution.Params =
        match session with
        | Morning -> config.MorningParams
        | DaySession.Mid -> config.MidParams
        | Close -> config.CloseParams

    /// Compute log-likelihood for a session state
    let logLikelihood (config: Config) (state: State) : float =
        state
        |> Array.sumBy (fun ep ->
            let p = getParams config ep.Label
            Distribution.logLikelihood p ep.Duration)

    /// Propose a move by transferring duration between two sessions
    let propose (config: Config) (rng: Random) (state: State) : State option =
        let stddev session = Distribution.stdDev (getParams config session)
        Some (MCMC.transferDuration stddev rng state)

    /// Create initial state for a given total duration
    let initialState (totalDuration: float) : State =
        let ratio = totalDuration / 390.0
        [|
            { Label = Morning; Duration = 60.0 * ratio }
            { Label = DaySession.Mid; Duration = 270.0 * ratio }
            { Label = Close; Duration = 60.0 * ratio }
        |]

    /// Sample session subdivision for a day
    let sample
        (config: Config)
        (mcmcConfig: MCMC.Config)
        (rng: Random)
        (dayDuration: float)
        : State =

        let initial = initialState dayDuration
        MCMC.run mcmcConfig (logLikelihood config) (propose config) initial rng

// =============================================================================
// Trend Level (Session -> Trends) - Weighted table compositional patterns
// =============================================================================

module TrendLevel =
    /// A flat weighted table of options, sampled via Categorical distribution.
    type WeightedTable<'t> = ('t * float)[]

    /// A single selection from the weighted table: trend episodes with durations
    type Selection = 
        {
            Probability: float
            Episodes: Episode<Trend>[]
        }

        member s.Copy() = { s with Episodes = Array.copy s.Episodes }

    type Config = {
        PatternTrees: Map<DaySession, WeightedTable<Trend[]>>
        DurationParams: Map<Trend, Distribution.Params>
    }

    /// Sample a random entry from the weighted table, returning the trend sequence and its probability
    let private samplePath (rng: Random) (tree: WeightedTable<Trend[]>) : Trend[] * float =
        let idx = Categorical.Sample(rng, Array.map snd tree)
        tree.[idx]

    let private logLikelihoodSelection (config: Config) (tree: WeightedTable<Trend[]>) (sel: Selection) : float =
        let pathLL = log sel.Probability
        let durationLL =
            sel.Episodes |> Array.sumBy (fun ep ->
                Distribution.logLikelihood config.DurationParams.[ep.Label] ep.Duration)
        pathLL + durationLL

    /// Compute log-likelihood for the full state
    let logLikelihood (config: Config) (parentSession: DaySession) (state: Selection[]) : float =
        let tree = config.PatternTrees.[parentSession]
        state |> Array.sumBy (logLikelihoodSelection config tree)

    /// Flatten state to Episode<Trend>[] for downstream consumption
    let flatten (state: Selection[]) : Episode<Trend>[] =
        state |> Array.collect _.Episodes

    /// Gets the number of episodes in a state.
    let length (state: Selection[]) : int =
        state |> Array.sumBy _.Episodes.Length

    // Map flat indices back to (selection, episode) pairs
    let createMappings (state: Selection[]) =
        let mapping = ResizeArray()
        for si in 0 .. state.Length - 1 do
            for ei in 0 .. state.[si].Episodes.Length - 1 do
                mapping.Add(si, ei)
        mapping.ToArray()

    /// Propose: transfer duration between two random episodes across all selections
    let private proposeTransferDuration (rng: Random) (config: Config) (state: Selection[]) : Selection[] option =
        let mapping = createMappings state
        if mapping.Length < 2 then None
        else
            let idx1, idx2 = MCMC.pickTwoDistinctIndices rng mapping.Length
            let si1, ei1 = mapping.[idx1]
            let si2, ei2 = mapping.[idx2]
            let stddev trend = Distribution.stdDev config.DurationParams.[trend]
            let proposalStdDev = min (stddev state.[si1].Episodes.[ei1].Label) (stddev state.[si2].Episodes.[ei2].Label)
            let delta = Normal.Sample(rng, 0.0, proposalStdDev)
            let newState = state |> Array.map _.Copy()
            newState.[si1].Episodes.[ei1] <- { newState.[si1].Episodes.[ei1] with Duration = newState.[si1].Episodes.[ei1].Duration + delta }
            newState.[si2].Episodes.[ei2] <- { newState.[si2].Episodes.[ei2] with Duration = newState.[si2].Episodes.[ei2].Duration - delta }
            Some newState

    /// Propose: replace one selection with a new random sample from the tree
    let private proposeChangeSelection (rng: Random) (tree: WeightedTable<Trend[]>) (config: Config) (state: Selection[]) : Selection[] option =
        if state.Length = 0 then None
        else
            let idx = rng.Next(state.Length)
            let oldSel = state.[idx]
            let totalDuration = oldSel.Episodes |> Array.sumBy _.Duration
            let trends, prob = samplePath rng tree
            // Distribute duration proportionally using duration priors
            let rawDurations = trends |> Array.map (fun t -> Distribution.sample rng config.DurationParams.[t])
            let rawTotal = rawDurations |> Array.sum
            let scale = totalDuration / rawTotal
            let episodes = Array.init trends.Length (fun i ->
                { Label = trends.[i]; Duration = rawDurations.[i] * scale })
            let newState = Array.copy state
            newState.[idx] <- { Probability = prob; Episodes = episodes }
            Some newState

    let propose (config: Config) (parentSession: DaySession) (rng: Random) (state: Selection[]) : Selection[] option =
        let tree = config.PatternTrees.[parentSession]
        let proposals = [
            (fun () -> proposeTransferDuration rng config state), if length state >= 2 then 0.7 else 0.0
            (fun () -> proposeChangeSelection rng tree config state), 0.3
        ]
        MCMC.sampleWeighted rng proposals ()

    /// Create initial state by sampling selections until total duration is filled
    let initialState (config: Config) (parentSession: DaySession) (rng: Random) (sessionDuration: float) : Selection[] =
        let tree = config.PatternTrees.[parentSession]
        let selections = ResizeArray<Selection>()
        let mutable total = 0.0
        while total < sessionDuration do
            let trends, prob = samplePath rng tree
            let episodes = trends |> Array.map (fun t ->
                let dur = Distribution.sample rng config.DurationParams.[t]
                { Label = t; Duration = dur })
            selections.Add { Probability = prob; Episodes = episodes }
            total <- total + (episodes |> Array.sumBy _.Duration)
        // Scale all durations to sum exactly to sessionDuration
        let result = selections.ToArray()
        let actualTotal = result |> Array.sumBy (fun s -> s.Episodes |> Array.sumBy _.Duration)
        let scale = sessionDuration / actualTotal
        result |> Array.map (fun s ->
            { s with Episodes = s.Episodes |> Array.map (fun e -> { e with Duration = e.Duration * scale }) })

    /// Sample trend subdivision for a session
    let sample
        (config: Config)
        (mcmcConfig: MCMC.Config)
        (rng: Random)
        (parentSession: DaySession)
        (sessionDuration: float)
        : Episode<Trend>[] =

        let initial = initialState config parentSession rng sessionDuration
        let ll = logLikelihood config parentSession
        let prop = propose config parentSession
        let result = MCMC.run mcmcConfig ll prop initial rng
        flatten result

    // =========================================================================
    // Default Configuration
    // =========================================================================

    let private defaultDurationParams : Map<Trend, Distribution.Params> =
        Map.ofList [
            Move (Up, Strong),   Distribution.LogNormal (4.0, 5.0)
            Move (Up, Mid),      Distribution.LogNormal (7.0, 10.0)
            Move (Up, Weak),     Distribution.LogNormal (12.0, 15.0)
            Consolidation,       Distribution.LogNormal (25.0, 35.0)
            Move (Down, Weak),   Distribution.LogNormal (12.0, 15.0)
            Move (Down, Mid),    Distribution.LogNormal (7.0, 10.0)
            Move (Down, Strong), Distribution.LogNormal (4.0, 5.0)
            Hold (Bid, Strong, Short),   Distribution.LogNormal (0.2, 0.3)
            Hold (Ask, Strong, Short),   Distribution.LogNormal (0.2, 0.3)
            Hold (Bid, Strong, Medium),  Distribution.LogNormal (2.0, 3.0)
            Hold (Ask, Strong, Medium),  Distribution.LogNormal (2.0, 3.0)
            Hold (Bid, Strong, Long),    Distribution.LogNormal (20.0, 30.0)
            Hold (Ask, Strong, Long),    Distribution.LogNormal (20.0, 30.0)
            Hold (Bid, Mid, Short),      Distribution.LogNormal (0.2, 0.3)
            Hold (Ask, Mid, Short),      Distribution.LogNormal (0.2, 0.3)
            Hold (Bid, Mid, Medium),     Distribution.LogNormal (2.0, 3.0)
            Hold (Ask, Mid, Medium),     Distribution.LogNormal (2.0, 3.0)
            Hold (Bid, Mid, Long),       Distribution.LogNormal (20.0, 30.0)
            Hold (Ask, Mid, Long),       Distribution.LogNormal (20.0, 30.0)
            Hold (Neutral, Mid, Short),  Distribution.LogNormal (0.2, 0.3)
            Hold (Neutral, Mid, Medium), Distribution.LogNormal (2.0, 3.0)
            Hold (Neutral, Mid, Long),   Distribution.LogNormal (20.0, 30.0)
            Hold (Bid, Weak, Short),     Distribution.LogNormal (0.2, 0.3)
            Hold (Ask, Weak, Short),     Distribution.LogNormal (0.2, 0.3)
            Hold (Bid, Weak, Medium),    Distribution.LogNormal (2.0, 3.0)
            Hold (Ask, Weak, Medium),    Distribution.LogNormal (2.0, 3.0)
            Hold (Bid, Weak, Long),      Distribution.LogNormal (20.0, 30.0)
            Hold (Ask, Weak, Long),      Distribution.LogNormal (20.0, 30.0)
        ]

    let private defaultPatternTrees : Map<DaySession, WeightedTable<Trend[]>> =
        // Normalizes the weights to sum to 1.
        let normalize (x : WeightedTable<Trend[]>) : WeightedTable<Trend[]> =
            let total = x |> Array.sumBy snd
            x |> Array.map (fun (tree, w) -> tree, w / total)

        // Flattens a nested weighted table into a single level, multiplying through probabilities.
        let flatten (x : WeightedTable<WeightedTable<Trend[]>>) : WeightedTable<Trend[]> =
            x |> Array.collect (fun (node, node_prob) ->
                node |> Array.map (fun (leaf, leaf_prob) -> leaf, node_prob * leaf_prob)
                )

        let node x = x |> flatten |> normalize

        let intensityProb = function
            | Weak -> 0.5
            | Mid -> 0.35
            | Strong -> 0.15
        let durationProb = function
            | Short -> 0.5
            | Medium -> 0.35
            | Long -> 0.15

        // Hold patterns sit next to their corresponding bare moves.
        let leaf (x : Trend[]) : WeightedTable<Trend[]> = [|x, 1.0|]

        let holds (intensity_duration : (Intensity * HoldDuration) list) move = node [|
            for side in [Bid; Ask] do
                for intensity, duration in intensity_duration do
                    leaf [|
                        Hold(side,intensity,duration)
                        move
                    |], durationProb duration * intensityProb intensity
        |]

        let strongHolds direction = holds [Strong, Short; Mid, Medium; Weak, Long] (Move(direction, Strong))
        let midHolds direction = holds [Mid, Short; Weak, Medium] (Move(direction, Mid))
        let weakHolds direction = holds [Weak, Short] (Move(direction, Weak))

        let strongUp = node [|
            leaf [| Move (Up, Strong) |],    1
            strongHolds Up,                  4
        |]
        let strongDown = node [|
            leaf [| Move (Down, Strong) |],  1
            strongHolds Down,                4
        |]
        let midUp = node [|
            leaf [| Move (Up, Mid) |],       1
            midHolds Up,                     2
        |]
        let midDown = node [|
            leaf [| Move (Down, Mid) |],     1
            midHolds Down,                   2
        |]
        let weakUp = node [|
            leaf [| Move (Up, Weak) |],   1
            weakHolds Up,                 1
        |]
        let weakDown = node [|
            leaf [| Move (Down, Weak) |], 1
            weakHolds Down,               1
        |]
        let consolidation = leaf [| Consolidation |]

        let morningCloseTree = node [|
            strongUp,                        0.15
            midUp,                           0.15
            weakUp,                          0.10
            consolidation,                   0.20
            weakDown,                        0.10
            midDown,                         0.15
            strongDown,                      0.15
        |]

        let midTree = node [|
            strongUp,                        0.02
            midUp,                           0.08
            weakUp,                          0.15
            consolidation,                   0.50
            weakDown,                        0.15
            midDown,                         0.08
            strongDown,                      0.02
        |]

        Map.ofList [
            Morning, morningCloseTree
            DaySession.Mid, midTree
            Close, morningCloseTree
        ]

    let defaultConfig = {
        PatternTrees = defaultPatternTrees
        DurationParams = defaultDurationParams
    }

// =============================================================================
// Composition: Full Day Generation
// =============================================================================

/// Result of generating a full day of episodes
type DayResult = {
    Sessions: Episode<DaySession>[]
    Trends: Episode<Trend>[][]
}

/// Generate a complete day with sessions and trends
let generateDay
    (sessionConfig: SessionLevel.Config)
    (trendConfig: TrendLevel.Config)
    (mcmcConfig: MCMC.Config)
    (rng: Random)
    (dayDuration: float)
    : DayResult =

    // Level 1: Sample sessions
    let sessions = SessionLevel.sample sessionConfig mcmcConfig rng dayDuration

    // Level 2: Sample trends for each session
    let trends =
        sessions
        |> Array.map (fun session ->
            TrendLevel.sample trendConfig mcmcConfig rng session.Label session.Duration)

    { Sessions = sessions; Trends = trends }

// =============================================================================
// Display Utilities
// =============================================================================

let printEpisodes (label: string) (episodes: Episode<'a>[]) (showLabel: 'a -> string) : unit =
    let total = episodes |> Array.sumBy (fun e -> e.Duration)
    printfn "%s (total %.1f):" label total
    let mutable t = 0.0
    for ep in episodes do
        printfn "  %6.1f - %6.1f: %-12s (%.1f)" t (t + ep.Duration) (showLabel ep.Label) ep.Duration
        t <- t + ep.Duration

let showSession (s: DaySession) : string = sprintf "%A" s

let showTrend (t: Trend) : string =
    match t with
    | Move (Up, Strong) -> "StrongUp"
    | Move (Up, Mid) -> "MidUp"
    | Move (Up, Weak) -> "WeakUp"
    | Consolidation -> "Consol"
    | Move (Down, Weak) -> "WeakDown"
    | Move (Down, Mid) -> "MidDown"
    | Move (Down, Strong) -> "StrongDown"
    | Hold (side, intensity, duration) ->
        let s = match side with Bid -> "Bid" | Ask -> "Ask" | Neutral -> "Neutral"
        let i = match intensity with Strong -> "Strong" | Mid -> "Mid" | Weak -> "Weak"
        let d = match duration with Short -> "Short" | Medium -> "Medium" | Long -> "Long"
        sprintf "Hold%s%s%s" s i d

let printDayResult (result: DayResult) : unit =
    printEpisodes "Sessions" result.Sessions showSession
    printfn ""
    for i in 0 .. result.Sessions.Length - 1 do
        let session = result.Sessions.[i]
        let trends = result.Trends.[i]
        printEpisodes (sprintf "%s Trends" (showSession session.Label)) trends showTrend
        printfn ""
