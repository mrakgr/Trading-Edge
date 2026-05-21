module TradingEdge.ReplaySimulatorV2.Snapshots

// Eager build pass: walk the merged MBO stream once and freeze the state at
// every 5-minute bucket boundary into an immutable SnapshotStore.
//
// Snapshots are aligned to a session anchor (04:00 ET of the day). The first
// snapshot's BucketStartNs equals the anchor; subsequent snapshots are at
// anchor + N * BUCKET_NS for N >= 1.
//
// Each snapshot captures the cumulative bar list and per-venue books AS OF
// the bucket's start, plus the trade tape WITHIN the bucket. To seek to a
// time t, find the snapshot whose bucket contains t, then replay records
// from that bucket forward to t.

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

/// One 5-min snapshot. Captures the cumulative state AT the start of the
/// bucket plus the trade tape THAT OCCURRED WITHIN the bucket.
type Snapshot = {
    /// Wall-clock anchor for this bucket (anchor + N * BUCKET_NS, UTC ns).
    BucketStartNs: int64
    /// Per-venue immutable book state AT the start of the bucket. Structurally
    /// shared with the prior snapshot's Books for venues that didn't change.
    Books: Map<int, L3Book>
    /// Cumulative bar list AT the start of the bucket (head = most-recent bar,
    /// which may be partially-formed if the prior bucket had in-progress trades).
    /// Structurally shared with the prior snapshot's Bars; only the head differs
    /// when bars updated during the prior bucket.
    Bars: Bar list
    /// Trades that occurred during this bucket [BucketStartNs, BucketStartNs+BUCKET_NS).
    Trades: ImmutableArray<TradeMsg>
}

type SnapshotStore = {
    /// UTC ns of the first bucket (typically 04:00 ET).
    SessionAnchorNs: int64
    /// Immutable list of snapshots in time order. Indexable by
    /// (t - SessionAnchorNs) / BUCKET_NS.
    Snapshots: ImmutableList<Snapshot>
    /// All MBO records sorted by ts_event, retained so play() can replay
    /// forward from a snapshot to an arbitrary t.
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
let buildAsync (merged: IAsyncEnumerable<MboMsg>) : Task<SnapshotStore> = task {
    // Buffer all records up-front so we can index in (and so the build pass
    // doesn't have to be async-aware). ~13M * 56 bytes = ~730MB transient on a
    // busy day; acceptable for desktop.
    let recordList = ImmutableArray.CreateBuilder()
    for m in merged do recordList.Add m
    let records = recordList.ToImmutableArray()

    if records.Length = 0 then
        return {
            SessionAnchorNs = 0L
            Snapshots = ImmutableList.Empty
            Records = records
        }
    else

    let anchor = sessionAnchorForDay records.[0].TsEvent

    // Live state advanced through the records. Books is a Dictionary for O(1)
    // per-venue lookup; the L3Book values are immutable. Bars and the per-venue
    // book values are what we snapshot.
    let books = Dictionary<int, L3Book>()
    let getBook (publisherId: int) =
        match books.TryGetValue publisherId with
        | true, b -> b
        | false, _ -> Book.empty
    let mutable bars : Bar list = []

    let snapshots = ImmutableList.CreateBuilder<Snapshot>()
    let tradeBuilder = ImmutableArray.CreateBuilder<TradeMsg>()

    // The snapshot for bucket N captures state AS OF the start of bucket N
    // (before any of bucket N's records are applied) and accumulates that
    // bucket's trades. We freeze state at boundary crossings.
    let mutable currentBucketStart = anchor
    let mutable preBucketBooks : Map<int, L3Book> = Map.empty
    let mutable preBucketBars : Bar list = []

    let freezeBucketStart () =
        // Capture state right now, BEFORE applying the first record of the
        // bucket we're about to start. The Map.ofSeq is O(venues) ≈ 13 entries.
        preBucketBooks <-
            books
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Map.ofSeq
        preBucketBars <- bars

    let emitSnapshot () =
        // Flush the just-closed bucket's payload as a Snapshot.
        snapshots.Add({
            BucketStartNs = currentBucketStart
            Books = preBucketBooks
            Bars = preBucketBars
            Trades = tradeBuilder.ToImmutable()
        })
        tradeBuilder.Clear()

    for m in records do
        let recordBucket =
            if m.TsEvent < anchor then anchor   // pre-anchor traffic folds into bucket 0
            else anchor + ((m.TsEvent - anchor) / BUCKET_NS) * BUCKET_NS
        // Cross zero or more bucket boundaries. Sparse records may skip whole
        // buckets; emit empty snapshots for those so bucket-index lookup stays
        // a simple division.
        while recordBucket > currentBucketStart do
            emitSnapshot ()
            currentBucketStart <- currentBucketStart + BUCKET_NS
            freezeBucketStart ()

        let publisherKey = int m.PublisherId
        let book = getBook publisherKey
        let book' = applyToBook book m
        if not (obj.ReferenceEquals(book, book')) then
            books.[publisherKey] <- book'
        if m.Action = ACTION_T then
            tradeBuilder.Add (Trades.fromMbo m)
        bars <- Bars.feed bars m

    // Final partial bucket.
    emitSnapshot ()

    return {
        SessionAnchorNs = anchor
        Snapshots = snapshots.ToImmutable()
        Records = records
    }
}
