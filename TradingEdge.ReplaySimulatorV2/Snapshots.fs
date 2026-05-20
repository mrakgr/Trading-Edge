module TradingEdge.ReplaySimulatorV2.Snapshots

// Eager build pass: walk the merged MBO stream once, maintain mutable per-venue
// books + a trade builder + a bar aggregator, and freeze the state at every
// 5-minute bucket boundary into an immutable SnapshotStore.
//
// Snapshots are aligned to a session anchor (04:00 ET of the day). The first
// snapshot's BucketStartNs equals the anchor; subsequent snapshots are at
// anchor + N * BUCKET_NS for N >= 1.

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Threading.Tasks
open FSharp.Control
open TradingEdge.ReplaySimulatorV2.MboReader
open TradingEdge.ReplaySimulatorV2.Trades
open TradingEdge.ReplaySimulatorV2.Bars
open TradingEdge.ReplaySimulatorV2.Book

/// 5-minute snapshot cadence in ns.
let BUCKET_NS : int64 = 5L * 60L * 1_000_000_000L

let private ACTION_T : byte = byte 'T'

/// One 5-min snapshot. Captures every venue's book + the trades & bars that
/// occurred during the bucket.
type Snapshot = {
    /// Wall-clock anchor for this bucket (anchor + N * BUCKET_NS, UTC ns).
    BucketStartNs: int64
    /// Per-venue immutable book state AT the start of the bucket. (Frozen *before*
    /// the bucket's records are applied — so when seeking, we hydrate this and
    /// then replay forward.)
    Books: Map<int, L3Book>
    /// Trades that occurred during this bucket [BucketStartNs, BucketStartNs+BUCKET_NS).
    Trades: ImmutableArray<TradeMsg>
    /// 1-min bars that closed during this bucket.
    ClosedBars: ImmutableArray<Bar>
    /// BarAggregator state AT the start of the bucket (matches Books).
    AggState: BarAggregatorSnapshot
}

type SnapshotStore = {
    /// UTC ns of the first bucket (typically 04:00 ET).
    SessionAnchorNs: int64
    /// Immutable list of snapshots in time order. Each extends the previous.
    Snapshots: ImmutableList<Snapshot>
    /// Per-venue MBO records sorted by ts_event, retained so play() can replay
    /// forward from a snapshot. Currently a flat array per venue for binary
    /// search; could be optimized later.
    Records: ImmutableArray<MboMsg>
}

/// Compute the session anchor for a day given any UTC ns within that day.
/// Returns the UTC ns of 04:00 America/New_York on that day's NY-local date.
let sessionAnchorForDay (utcNs: int64) : int64 =
    let nyTz =
        try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
        with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    let ny = TimeZoneInfo.ConvertTimeFromUtc(utc, nyTz)
    let anchorNy = DateTime(ny.Year, ny.Month, ny.Day, 4, 0, 0, DateTimeKind.Unspecified)
    let anchorUtc = TimeZoneInfo.ConvertTimeToUtc(anchorNy, nyTz)
    let epoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    (anchorUtc.Ticks - epoch.Ticks) * 100L

/// Build a SnapshotStore by streaming through the merged MBO records once.
/// Currently single-pass and synchronous-feeling but driven by IAsyncEnumerable.
let buildAsync (merged: IAsyncEnumerable<MboMsg>) : Task<SnapshotStore> = task {
    // We need to know the anchor before processing any records, but the anchor
    // depends on the first record's date. So: buffer all records into an array
    // up-front (already what v0 effectively does via in-memory streams), then
    // do the build pass against the array.
    //
    // This costs ~13M * 56 bytes = ~730MB transient for a busy day. Acceptable
    // for desktop; would need work for production.
    let recordList = ResizeArray<MboMsg>()
    for m in merged do recordList.Add m
    let records = ImmutableArray.CreateRange(recordList :> seq<MboMsg>)

    if records.Length = 0 then
        return {
            SessionAnchorNs = 0L
            Snapshots = ImmutableList.Empty
            Records = records
        }
    else

    let anchor = sessionAnchorForDay records.[0].TsEvent

    // Scratch state for the build pass. The Dictionary is just an O(1) lookup
    // table; the L3Book values themselves are immutable.
    let books = Dictionary<int, L3Book>()
    let aggregator = BarAggregator()
    let getBook (publisherId: int) =
        match books.TryGetValue publisherId with
        | true, b -> b
        | false, _ -> Book.empty

    let snapshots = ImmutableList.CreateBuilder<Snapshot>()
    let tradeBuilder = ImmutableArray.CreateBuilder<TradeMsg>()
    let barBuilder = ImmutableArray.CreateBuilder<Bar>()

    // Freeze the current state and start a new bucket. `bucketStart` is the
    // start of the bucket we are CLOSING (the snapshot represents state at its
    // own start; bucket payloads are the events within it).
    //
    // We capture two snapshots: pre-bucket (the immutable book + agg-state from
    // BEFORE the bucket's records were applied) is what we want for the
    // Snapshot record. So we have to defer the freeze: see below.
    //
    // Simplified approach: walk records, and at each boundary crossing, write
    // a Snapshot whose state was captured BEFORE the crossing's events but
    // whose Trades/ClosedBars are the events of the bucket being closed.
    //
    // To avoid the bookkeeping headache, we freeze at the START of each new
    // bucket (so Snapshots[i].Books represents "state when bucket i began,
    // ready to be advanced through bucket i's records").

    let mutable currentBucketStart = anchor   // the bucket we're currently filling
    let mutable preBucketBooks = Map.empty<int, L3Book>
    let mutable preBucketAggState = aggregator.Freeze()

    let freezeBucketStart () =
        // Called when we cross into a new bucket. Capture the state right now,
        // BEFORE applying the first record of the new bucket — this becomes the
        // snapshot for the NEW bucket. Books are already immutable so we just
        // copy the Dictionary into a Map (O(venues), 13 entries).
        preBucketBooks <-
            books
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Map.ofSeq
        preBucketAggState <- aggregator.Freeze()

    let emitSnapshot () =
        // Flush the current bucket's payloads into a Snapshot entry. Uses the
        // preBucketBooks / preBucketAggState that were frozen at this bucket's
        // start.
        snapshots.Add({
            BucketStartNs = currentBucketStart
            Books = preBucketBooks
            Trades = tradeBuilder.ToImmutable()
            ClosedBars = barBuilder.ToImmutable()
            AggState = preBucketAggState
        })
        tradeBuilder.Clear()
        barBuilder.Clear()

    // Initial snapshot for the very first bucket: empty books, freshly-default
    // aggregator. Already set in preBucket* via the let-bindings above.

    for m in records do
        let recordBucket =
            if m.TsEvent < anchor then anchor   // any pre-anchor traffic gets folded into bucket 0
            else anchor + ((m.TsEvent - anchor) / BUCKET_NS) * BUCKET_NS
        // Bucket boundary crossing(s): emit the closing bucket(s) and freeze
        // state for the new one(s). We may skip buckets entirely if records are
        // sparse; emit empty snapshots so the bucket index stays a simple
        // division.
        while recordBucket > currentBucketStart do
            emitSnapshot ()
            currentBucketStart <- currentBucketStart + BUCKET_NS
            freezeBucketStart ()

        // Apply the record:
        //   * immutable book update for A/C/M/R/F
        //   * trade-tape append for T
        //   * bar-aggregator feed for T (we feed via aggregator.Feed which itself filters)
        let publisherKey = int m.PublisherId
        let book = getBook publisherKey
        let book' = applyToBook book m
        if not (obj.ReferenceEquals(book, book')) then
            books.[publisherKey] <- book'
        if m.Action = ACTION_T then
            tradeBuilder.Add (Trades.fromMbo m)
        match aggregator.Feed m with
        | NoChange -> ()
        | Forming _ -> ()
        | Closed (c, _) -> barBuilder.Add c

    // Emit the final partial bucket.
    emitSnapshot ()

    return {
        SessionAnchorNs = anchor
        Snapshots = snapshots.ToImmutable()
        Records = records
    }
}
