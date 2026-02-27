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

/// Trend types within sessions
type Trend =
    | StrongUptrend
    | MidUptrend
    | WeakUptrend
    | Consolidation
    | WeakDowntrend
    | MidDowntrend
    | StrongDowntrend
    | TightHold of HoldSide

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
        | Mid -> config.MidParams
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
            { Label = Mid; Duration = 270.0 * ratio }
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
// Trend Level (Session -> Trends) - Tree-based compositional patterns
// =============================================================================

module TrendLevel =
    /// A tree of episode patterns. Nodes branch with probabilities, leaves define trend sequences.
    type EpisodeTree =
        | Node of children: (EpisodeTree * float)[]
        | Leaf of trends: Trend[]

    /// A single selection from the tree: path indices + episodes with durations
    type TreeSelection = 
        {
            Path: int[]
            Episodes: Episode<Trend>[]
        }

        member s.Copy() = { s with Episodes = Array.copy s.Episodes }

    type Config = {
        PatternTrees: Map<DaySession, EpisodeTree>
        DurationParams: Map<Trend, Distribution.Params>
    }

    /// Compute log-probability of a path through the tree
    let rec private pathLogProb (tree: EpisodeTree) (path: int[]) (depth: int) : float =
        match tree with
        | Leaf _ -> 0.0
        | Node children ->
            let idx = path.[depth]
            let child, prob = children.[idx]
            log prob + pathLogProb child path (depth + 1)

    /// Get the leaf trends for a given path
    let rec private getLeafTrends (tree: EpisodeTree) (path: int[]) (depth: int) : Trend[] =
        match tree with
        | Leaf trends -> trends
        | Node children -> getLeafTrends (fst children.[path.[depth]]) path (depth + 1)

    /// Compute path depth (number of Node levels) for a given path
    let rec private pathDepth (tree: EpisodeTree) (path: int[]) (depth: int) : int =
        match tree with
        | Leaf _ -> depth
        | Node children -> pathDepth (fst children.[path.[depth]]) path (depth + 1)

    /// Sample a random path through the tree, returning path indices and leaf trends
    let rec private samplePath (rng: Random) (tree: EpisodeTree) : int list * Trend[] =
        match tree with
        | Leaf trends -> [], trends
        | Node children ->
            let probs = children |> Array.map snd
            let idx = Categorical.Sample(rng, probs)
            let rest, trends = samplePath rng (fst children.[idx])
            idx :: rest, trends

    let private logLikelihoodSelection (config: Config) (tree: EpisodeTree) (sel: TreeSelection) : float =
        let pathLL = pathLogProb tree sel.Path 0
        let durationLL =
            sel.Episodes |> Array.sumBy (fun ep ->
                Distribution.logLikelihood config.DurationParams.[ep.Label] ep.Duration)
        pathLL + durationLL

    /// Compute log-likelihood for the full state
    let logLikelihood (config: Config) (parentSession: DaySession) (state: TreeSelection[]) : float =
        let tree = config.PatternTrees.[parentSession]
        state |> Array.sumBy (logLikelihoodSelection config tree)

    /// Flatten state to Episode<Trend>[] for downstream consumption
    let flatten (state: TreeSelection[]) : Episode<Trend>[] =
        state |> Array.collect _.Episodes

    /// Gets the number of episodes in a state.
    let length (state: TreeSelection[]) : int =
        state |> Array.sumBy _.Episodes.Length

    // Map flat indices back to (selection, episode) pairs
    let createMappings (state: TreeSelection[]) =
        let mapping = ResizeArray()
        for si in 0 .. state.Length - 1 do
            for ei in 0 .. state.[si].Episodes.Length - 1 do
                mapping.Add(si, ei)
        mapping.ToArray()

    /// Propose: transfer duration between two random episodes across all selections
    let private proposeTransferDuration (rng: Random) (config: Config) (state: TreeSelection[]) : TreeSelection[] option =
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
    let private proposeChangeSelection (rng: Random) (tree: EpisodeTree) (config: Config) (state: TreeSelection[]) : TreeSelection[] option =
        if state.Length = 0 then None
        else
            let idx = rng.Next(state.Length)
            let oldSel = state.[idx]
            let totalDuration = oldSel.Episodes |> Array.sumBy _.Duration
            let pathList, trends = samplePath rng tree
            let path = pathList |> Array.ofList
            // Distribute duration proportionally using duration priors
            let rawDurations = trends |> Array.map (fun t -> Distribution.sample rng config.DurationParams.[t])
            let rawTotal = rawDurations |> Array.sum
            let scale = totalDuration / rawTotal
            let episodes = Array.init trends.Length (fun i ->
                { Label = trends.[i]; Duration = rawDurations.[i] * scale })
            let newState = Array.copy state
            newState.[idx] <- { Path = path; Episodes = episodes }
            Some newState

    let propose (config: Config) (parentSession: DaySession) (rng: Random) (state: TreeSelection[]) : TreeSelection[] option =
        let tree = config.PatternTrees.[parentSession]
        let proposals = [
            (fun () -> proposeTransferDuration rng config state), if length state >= 2 then 0.7 else 0.0
            (fun () -> proposeChangeSelection rng tree config state), 0.3
        ]
        MCMC.sampleWeighted rng proposals ()

    /// Create initial state by sampling selections until total duration is filled
    let initialState (config: Config) (parentSession: DaySession) (rng: Random) (sessionDuration: float) : TreeSelection[] =
        let tree = config.PatternTrees.[parentSession]
        let selections = ResizeArray<TreeSelection>()
        let mutable total = 0.0
        while total < sessionDuration do
            let pathList, trends = samplePath rng tree
            let path = pathList |> Array.ofList
            let episodes = trends |> Array.map (fun t ->
                let dur = Distribution.sample rng config.DurationParams.[t]
                { Label = t; Duration = dur })
            selections.Add { Path = path; Episodes = episodes }
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
            StrongUptrend,   Distribution.LogNormal (4.0, 5.0)
            MidUptrend,      Distribution.LogNormal (7.0, 10.0)
            WeakUptrend,     Distribution.LogNormal (12.0, 15.0)
            Consolidation,   Distribution.LogNormal (25.0, 35.0)
            WeakDowntrend,   Distribution.LogNormal (12.0, 15.0)
            MidDowntrend,    Distribution.LogNormal (7.0, 10.0)
            StrongDowntrend, Distribution.LogNormal (4.0, 5.0)
            TightHold Bid,     Distribution.LogNormal (2.0, 3.0)
            TightHold Ask,     Distribution.LogNormal (2.0, 3.0)
            TightHold Neutral, Distribution.LogNormal (2.0, 3.0)
        ]

    let private defaultPatternTrees : Map<DaySession, EpisodeTree> =
        let midUp = Node [|
            Leaf [| MidUptrend |],                      0.25
            Leaf [| TightHold Ask; MidUptrend |],       0.75
        |]
        let strongUp = Node [|
            Leaf [| StrongUptrend |],                   0.10
            Leaf [| TightHold Ask; StrongUptrend |],    0.90
        |]
        let midDown = Node [|
            Leaf [| MidDowntrend |],                    0.25
            Leaf [| TightHold Bid; MidDowntrend |],     0.75
        |]
        let strongDown = Node [|
            Leaf [| StrongDowntrend |],                 0.10
            Leaf [| TightHold Bid; StrongDowntrend |],  0.90
        |]

        let morningCloseTree = Node [|
            strongUp,                    0.15
            midUp,                       0.15
            Leaf [| WeakUptrend |],      0.10
            Leaf [| Consolidation |],    0.20
            Leaf [| WeakDowntrend |],    0.10
            midDown,                     0.15
            strongDown,                  0.15
        |]

        let midTree = Node [|
            strongUp,                    0.02
            midUp,                       0.08
            Leaf [| WeakUptrend |],      0.15
            Leaf [| Consolidation |],    0.50
            Leaf [| WeakDowntrend |],    0.15
            midDown,                     0.08
            strongDown,                  0.02
        |]

        Map.ofList [
            Morning, morningCloseTree
            Mid, midTree
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
    | StrongUptrend -> "StrongUp"
    | MidUptrend -> "MidUp"
    | WeakUptrend -> "WeakUp"
    | Consolidation -> "Consol"
    | WeakDowntrend -> "WeakDown"
    | MidDowntrend -> "MidDown"
    | StrongDowntrend -> "StrongDown"
    | TightHold Bid -> "HoldBid"
    | TightHold Ask -> "HoldAsk"
    | TightHold Neutral -> "HoldNeutral"

let printDayResult (result: DayResult) : unit =
    printEpisodes "Sessions" result.Sessions showSession
    printfn ""
    for i in 0 .. result.Sessions.Length - 1 do
        let session = result.Sessions.[i]
        let trends = result.Trends.[i]
        printEpisodes (sprintf "%s Trends" (showSession session.Label)) trends showTrend
        printfn ""
