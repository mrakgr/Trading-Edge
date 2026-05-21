module TradingEdge.ReplaySimulatorV3.Play

// play(t) — the single entry point that returns the simulator state at any
// time t in the loaded day. Returns a Snapshot whose BucketStartNs field holds
// `t` (not bucket-aligned) but is otherwise identical in shape to the eager
// bucket-boundary snapshots: cumulative Bars, Books, and rolling 1m trade queue.
//
// Three execution tiers:
//   1. exact-hit cache: t == last call → return memoized Snapshot.
//   2. forward-extend:  t > last call AND same 5-min bucket → advance the
//      scratch state from lastTime to t.
//   3. rebuild:         t in a different bucket, or t < last call → restore
//      from the nearest bucket snapshot ≤ t, replay records (snapshot, t].
//
// play() is NOT thread-safe. Caller (GUI dispatcher) must serialize.

open System
open System.Collections.Generic
open System.Collections.Immutable
open TradingEdge.ReplaySimulatorV3.MboReader
open TradingEdge.ReplaySimulatorV3.Trades
open TradingEdge.ReplaySimulatorV3.Bars
open TradingEdge.ReplaySimulatorV3.Book
open TradingEdge.ReplaySimulatorV3.Snapshots

let private ACTION_T : byte = byte 'T'
let private SIDE_ASK : byte = byte 'A'
let private SIDE_BID : byte = byte 'B'
let private SIDE_NONE : byte = byte 'N'

type Player(store: SnapshotStore) =
    // Scratch state mirrors what Snapshots.buildAsync maintains: an O(1) book
    // lookup (immutable L3Book values), an immutable Bar list, a rolling 1m
    // trade queue, and the four price-keyed accumulator dicts that feed the
    // ladder. The dicts are immutable references — updates allocate new trie
    // nodes only along the touched path.
    let scratchBooks = Dictionary<int, L3Book>()
    let mutable scratchBars : Bar list = []
    let mutable scratchTradeQueue : ImmutableQueue<TradeMsg> = ImmutableQueue.Create()
    let mutable scratchVolumeAtPrice : ImmutableDictionary<int64, uint64> =
        ImmutableDictionary.Create<int64, uint64>()
    let mutable scratchBidTrade : TradeAtPrice = emptyTradeAtPrice
    let mutable scratchAskTrade : TradeAtPrice = emptyTradeAtPrice
    let mutable scratchMidTrade : TradeAtPrice = emptyTradeAtPrice
    let mutable cursor : int64 = Int64.MinValue
    let mutable recordIdx : int = 0
    let mutable currentBucketIdx : int = -1

    let mutable lastResult : Snapshot option = None

    let bucketIndexOf (t: int64) : int =
        if t < store.SessionAnchorNs then 0
        else int ((t - store.SessionAnchorNs) / BUCKET_NS)

    let trimTradeQueue (asOfNs: int64) =
        let cutoff = asOfNs - TRADE_WINDOW_NS
        let mutable q = scratchTradeQueue
        while not q.IsEmpty && q.Peek().TsEvent < cutoff do
            q <- q.Dequeue()
        scratchTradeQueue <- q

    /// Reset scratch state to the start of `bucketIdx`. Snapshot fields are
    /// already immutable values, so this is just pointer copies.
    let resetToBucket (bucketIdx: int) =
        let snap = store.Snapshots.[bucketIdx]
        scratchBooks.Clear()
        for kv in snap.Books do
            scratchBooks.[kv.Key] <- kv.Value
        scratchBars <- snap.Bars
        scratchTradeQueue <- snap.Trades
        scratchVolumeAtPrice <- snap.VolumeAtPrice
        scratchBidTrade <- snap.BidTradeAtPrice
        scratchAskTrade <- snap.AskTradeAtPrice
        scratchMidTrade <- snap.MidTradeAtPrice
        cursor <- snap.BucketStartNs
        currentBucketIdx <- bucketIdx
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
                | false, _ -> Book.empty
            let book' = applyToBook book m
            if not (obj.ReferenceEquals(book, book')) then
                scratchBooks.[publisherKey] <- book'
            if m.Action = ACTION_T then
                trimTradeQueue m.TsEvent
                scratchTradeQueue <- scratchTradeQueue.Enqueue(Trades.fromMbo m)
                let prevVol =
                    match scratchVolumeAtPrice.TryGetValue(m.Price) with
                    | true, v -> v
                    | false, _ -> 0UL
                scratchVolumeAtPrice <- scratchVolumeAtPrice.SetItem(m.Price, prevVol + uint64 m.Size)
                match m.Side with
                | s when s = SIDE_ASK ->
                    scratchAskTrade <- applyTradeAtPrice scratchAskTrade m.Price m.Size m.TsEvent
                | s when s = SIDE_BID ->
                    scratchBidTrade <- applyTradeAtPrice scratchBidTrade m.Price m.Size m.TsEvent
                | s when s = SIDE_NONE ->
                    scratchMidTrade <- applyTradeAtPrice scratchMidTrade m.Price m.Size m.TsEvent
                | _ -> ()
            scratchBars <- Bars.feed scratchBars m
            recordIdx <- recordIdx + 1
        cursor <- t
        trimTradeQueue t

    let buildResult (t: int64) : Snapshot =
        let books =
            scratchBooks
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Map.ofSeq
        {
            BucketStartNs = t
            Books = books
            Bars = scratchBars
            Trades = scratchTradeQueue
            VolumeAtPrice = scratchVolumeAtPrice
            BidTradeAtPrice = scratchBidTrade
            AskTradeAtPrice = scratchAskTrade
            MidTradeAtPrice = scratchMidTrade
        }

    member _.Store = store

    member _.Play (t: int64) : Snapshot =
        match lastResult with
        | Some r when r.BucketStartNs = t -> r
        | _ ->
            let targetBucket = bucketIndexOf t
            let clampedBucket =
                if targetBucket >= store.Snapshots.Length then store.Snapshots.Length - 1
                else targetBucket
            let needsReset =
                currentBucketIdx <> clampedBucket
                || t < cursor
                || cursor = Int64.MinValue
            if needsReset then
                resetToBucket clampedBucket
            advanceTo t
            let result = buildResult t
            lastResult <- Some result
            result
