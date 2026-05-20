module TradingEdge.ReplaySimulatorV2.Play

// play(t) — the single entry point that returns the simulator state at any
// time t in the loaded day. Three execution tiers:
//
//   1. exact-hit cache: t == last call → return memoized PlayResult.
//   2. forward-extend:  t > last call AND same 5-min bucket → advance the
//      scratch state from lastTime to t, build a fresh PlayResult.
//   3. rebuild:         t in a different bucket, or t < last call → restore
//      from the nearest snapshot ≤ t, replay records (snapshot, t], build.
//
// Books are immutable in V2: scratch state holds an L3Book per venue but
// "restore" is just copying the snapshot's Map into a Dictionary for O(1)
// per-venue lookup during forward replay.
//
// play() is NOT thread-safe. Caller (GUI dispatcher) must serialize.

open System
open System.Collections.Generic
open System.Collections.Immutable
open TradingEdge.ReplaySimulatorV2.MboReader
open TradingEdge.ReplaySimulatorV2.Trades
open TradingEdge.ReplaySimulatorV2.Bars
open TradingEdge.ReplaySimulatorV2.Book
open TradingEdge.ReplaySimulatorV2.Snapshots

let private ACTION_T : byte = byte 'T'

type PlayResult = {
    Time: int64
    Books: Map<int, L3Book>
    TradeBuckets: ImmutableList<ImmutableArray<TradeMsg>>
    TradeTail:    ImmutableArray<TradeMsg>
    BarBuckets:   ImmutableList<ImmutableArray<Bar>>
    BarTail:      ImmutableArray<Bar>
    FormingBar:   Bar option
}

type Player(store: SnapshotStore) =
    // Scratch state. Books Dictionary is just an O(1) lookup wrapper around
    // immutable L3Book values.
    let scratchBooks = Dictionary<int, L3Book>()
    let scratchAgg = BarAggregator()
    let scratchTradeTail = ImmutableArray.CreateBuilder<TradeMsg>()
    let scratchBarTail = ImmutableArray.CreateBuilder<Bar>()
    let mutable cursor : int64 = Int64.MinValue
    let mutable recordIdx : int = 0
    let mutable currentBucketIdx : int = -1

    let mutable lastResult : PlayResult option = None

    let bucketIndexOf (t: int64) : int =
        if t < store.SessionAnchorNs then 0
        else int ((t - store.SessionAnchorNs) / BUCKET_NS)

    /// Reset scratch state to the start of `bucketIdx`. Books are dropped in
    /// straight from the snapshot's immutable Map.
    let resetToBucket (bucketIdx: int) =
        let snap = store.Snapshots.[bucketIdx]
        scratchBooks.Clear()
        for kv in snap.Books do
            scratchBooks.[kv.Key] <- kv.Value
        scratchAgg.Hydrate snap.AggState
        scratchTradeTail.Clear()
        scratchBarTail.Clear()
        cursor <- snap.BucketStartNs
        currentBucketIdx <- bucketIdx
        let bucketStart = snap.BucketStartNs
        let mutable i = 0
        while i < store.Records.Length && store.Records.[i].TsEvent < bucketStart do
            i <- i + 1
        recordIdx <- i

    /// Advance scratch state forward from `cursor` up to (and including) any
    /// records with ts_event <= t. Each book mutation returns a new immutable
    /// L3Book; we update the Dictionary in place to point at the new value.
    let advanceTo (t: int64) =
        while recordIdx < store.Records.Length && store.Records.[recordIdx].TsEvent <= t do
            let m = store.Records.[recordIdx]
            let publisherKey = int m.PublisherId
            let book =
                match scratchBooks.TryGetValue publisherKey with
                | true, b -> b
                | false, _ -> Book.empty
            let book' = applyToBook book m
            if not (obj.ReferenceEquals(book, book')) then
                scratchBooks.[publisherKey] <- book'
            if m.Action = ACTION_T then
                scratchTradeTail.Add (Trades.fromMbo m)
            match scratchAgg.Feed m with
            | NoChange -> ()
            | Forming _ -> ()
            | Closed (c, _) -> scratchBarTail.Add c
            recordIdx <- recordIdx + 1
        cursor <- t

    let buildResult (t: int64) (bucketIdx: int) : PlayResult =
        let tradeBuckets = ImmutableList.CreateBuilder<ImmutableArray<TradeMsg>>()
        let barBuckets   = ImmutableList.CreateBuilder<ImmutableArray<Bar>>()
        for i in 0 .. bucketIdx - 1 do
            let s = store.Snapshots.[i]
            tradeBuckets.Add s.Trades
            barBuckets.Add s.ClosedBars

        let books =
            scratchBooks
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Map.ofSeq

        {
            Time = t
            Books = books
            TradeBuckets = tradeBuckets.ToImmutable()
            TradeTail = scratchTradeTail.ToImmutable()
            BarBuckets = barBuckets.ToImmutable()
            BarTail = scratchBarTail.ToImmutable()
            FormingBar = scratchAgg.Current
        }

    member _.Store = store

    member _.Play (t: int64) : PlayResult =
        match lastResult with
        | Some r when r.Time = t -> r
        | _ ->
            let targetBucket = bucketIndexOf t
            let clampedBucket =
                if targetBucket >= store.Snapshots.Count then store.Snapshots.Count - 1
                else targetBucket
            let needsReset =
                currentBucketIdx <> clampedBucket
                || t < cursor
                || cursor = Int64.MinValue
            if needsReset then
                resetToBucket clampedBucket
            advanceTo t
            let result = buildResult t clampedBucket
            lastResult <- Some result
            result
