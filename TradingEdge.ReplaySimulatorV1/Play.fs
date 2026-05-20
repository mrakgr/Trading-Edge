module TradingEdge.ReplaySimulatorV1.Play

// play(t) — the single entry point that returns the simulator state at any
// time t in the loaded day. Three execution tiers:
//
//   1. exact-hit cache: t == last call → return memoized PlayResult.
//   2. forward-extend:  t > last call AND same 5-min bucket → advance the
//      scratch state from lastTime to t, build a fresh PlayResult.
//   3. rebuild:         t in a different bucket, or t < last call → hydrate
//      from the nearest snapshot ≤ t, replay records (snapshot, t], build.
//
// play() is NOT thread-safe. Caller (GUI dispatcher) must serialize.

open System
open System.Collections.Generic
open System.Collections.Immutable
open TradingEdge.ReplaySimulatorV1.MboReader
open TradingEdge.ReplaySimulatorV1.Trades
open TradingEdge.ReplaySimulatorV1.Bars
open TradingEdge.ReplaySimulatorV1.Book
open TradingEdge.ReplaySimulatorV1.Snapshots

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

/// Long-lived player state. Owns the scratch mutable books / aggregator /
/// builders that get hydrated from snapshots and advanced forward.
type Player(store: SnapshotStore) =
    // Scratch state — represents the simulator at `cursor`.
    let scratchBooks = Dictionary<int, MutableBook>()
    let scratchAgg = BarAggregator()
    let scratchTradeTail = ImmutableArray.CreateBuilder<TradeMsg>()
    let scratchBarTail = ImmutableArray.CreateBuilder<Bar>()
    let mutable cursor : int64 = Int64.MinValue   // ts_event up to which scratch state has consumed
    let mutable recordIdx : int = 0               // next record index to feed
    let mutable currentBucketIdx : int = -1       // bucket the scratch state currently belongs to

    // Memoization of the last produced result. Cache hit returns it verbatim.
    let mutable lastResult : PlayResult option = None

    let bucketIndexOf (t: int64) : int =
        if t < store.SessionAnchorNs then 0
        else int ((t - store.SessionAnchorNs) / BUCKET_NS)

    let bucketStartOf (bucketIdx: int) : int64 =
        store.SessionAnchorNs + int64 bucketIdx * BUCKET_NS

    /// Reset scratch state to the start of `bucketIdx`. Hydrates per-venue
    /// books from the snapshot, hydrates the bar aggregator, clears the tail
    /// builders, and positions recordIdx at the first record in the bucket.
    let resetToBucket (bucketIdx: int) =
        let snap = store.Snapshots.[bucketIdx]
        // Hydrate books: drop any venues no longer present, hydrate the ones in
        // the snapshot.
        scratchBooks.Clear()
        for kv in snap.Books do
            let mb = MutableBook()
            mb.Hydrate kv.Value
            scratchBooks.[kv.Key] <- mb
        scratchAgg.Hydrate snap.AggState
        scratchTradeTail.Clear()
        scratchBarTail.Clear()
        cursor <- snap.BucketStartNs
        currentBucketIdx <- bucketIdx
        // Position recordIdx at the first record with ts_event >= bucket start.
        // Linear scan — could binary-search but build-time index is already paid.
        let bucketStart = snap.BucketStartNs
        let mutable i = 0
        while i < store.Records.Length && store.Records.[i].TsEvent < bucketStart do
            i <- i + 1
        recordIdx <- i

    /// Advance scratch state forward from `cursor` up to (and including) any
    /// records with ts_event <= t.
    let advanceTo (t: int64) =
        while recordIdx < store.Records.Length && store.Records.[recordIdx].TsEvent <= t do
            let m = store.Records.[recordIdx]
            let publisherKey = int m.PublisherId
            let book =
                match scratchBooks.TryGetValue publisherKey with
                | true, b -> b
                | false, _ ->
                    let b = MutableBook()
                    scratchBooks.[publisherKey] <- b
                    b
            applyToBook book m |> ignore
            if m.Action = ACTION_T then
                scratchTradeTail.Add (Trades.fromMbo m)
            match scratchAgg.Feed m with
            | NoChange -> ()
            | Forming _ -> ()
            | Closed (c, _) -> scratchBarTail.Add c
            recordIdx <- recordIdx + 1
        cursor <- t

    /// Build a PlayResult from the current scratch state, sharing the prefix
    /// of bucket lists from the snapshot store.
    let buildResult (t: int64) (bucketIdx: int) : PlayResult =
        // TradeBuckets = ImmutableList of arrays from snapshots[0..bucketIdx-1].Trades.
        // BarBuckets   = ImmutableList of arrays from snapshots[0..bucketIdx-1].ClosedBars.
        // ImmutableList<T>.AddRange supports structural sharing, so building the
        // prefix once per result is cheap relative to the work we've already done.
        let tradeBuckets = ImmutableList.CreateBuilder<ImmutableArray<TradeMsg>>()
        let barBuckets   = ImmutableList.CreateBuilder<ImmutableArray<Bar>>()
        for i in 0 .. bucketIdx - 1 do
            let s = store.Snapshots.[i]
            tradeBuckets.Add s.Trades
            barBuckets.Add s.ClosedBars

        // Books: per-venue, derived from scratch.
        let books =
            scratchBooks
            |> Seq.map (fun kv -> kv.Key, kv.Value.Freeze())
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

    /// Compute the simulator state at time `t`.
    member _.Play (t: int64) : PlayResult =
        // Cache hit.
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
