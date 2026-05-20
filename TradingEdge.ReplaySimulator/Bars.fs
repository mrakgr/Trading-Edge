module TradingEdge.ReplaySimulator.Bars

// Static-load path: take a directory of per-venue .dbn.zst MBO files, k-way merge
// the T (trade) records across venues by ts_event, fold into 1-minute OHLCV bars
// and an accumulating VWAP series.
//
// For tape-reading bars we read T only (one row per trade). F records describe
// individual resting legs filled by the same aggressor and would double-count.

open System
open System.IO
open System.Collections.Generic
open TradingEdge.ReplaySimulator.Dbn
open TradingEdge.ReplaySimulator.MboReader

let private ACTION_T : byte = byte 'T'

/// Price field unit. DBN MBO prices are 1e-9 USD (nanodollars).
let priceToUsd (p: int64) : float = float p * 1e-9

/// Bar bucket size in nanoseconds (1 minute).
let private BAR_NS : int64 = 60L * 1_000_000_000L

type Bar = {
    /// Bucket start, ns since epoch, UTC, aligned to BAR_NS.
    BucketStartNs: int64
    Open: float
    High: float
    Low: float
    Close: float
    /// Sum of trade sizes (shares) in the bucket.
    Volume: int64
    /// Number of T records that fell into this bucket.
    TradeCount: int
    /// Bucket-level VWAP (volume-weighted avg trade price within the bucket).
    BarVwap: float
    /// Session-accumulated VWAP through the close of this bucket; None before RTH open.
    SessionVwap: float option
}

let private NY_TZ_BARS =
    try TimeZoneInfo.FindSystemTimeZoneById("America/New_York")
    with _ -> TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")

/// True if the UTC ts (nanos since epoch) falls at or after 09:30 NY on its NY-local date.
let private isAtOrAfterRthOpen (utcNs: int64) : bool =
    let utc = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(utcNs / 100L)
    let ny = TimeZoneInfo.ConvertTimeFromUtc(utc, NY_TZ_BARS)
    let minuteOfDay = ny.Hour * 60 + ny.Minute
    minuteOfDay >= 9 * 60 + 30

/// K-way merge of a list of sorted MBO record sequences, ordered by ts_event.
/// Each input must already be in non-decreasing ts_event order (true for raw
/// per-venue DBN streams). Uses a min-heap keyed on ts_event.
let mergeByTsEvent (streams: seq<MboMsg> list) : seq<MboMsg> =
    seq {
        let enumerators =
            streams
            |> List.map (fun s -> s.GetEnumerator())
        try
            // Priority queue keyed on (ts_event, source index) so ties are stable.
            let pq = PriorityQueue<int, struct (int64 * int)>()
            for i in 0 .. enumerators.Length - 1 do
                let e = enumerators.[i]
                if e.MoveNext() then
                    pq.Enqueue(i, struct (e.Current.TsEvent, i))
            while pq.Count > 0 do
                let i = pq.Dequeue()
                let e = enumerators.[i]
                yield e.Current
                if e.MoveNext() then
                    pq.Enqueue(i, struct (e.Current.TsEvent, i))
        finally
            for e in enumerators do e.Dispose()
    }

/// Fold a merged MBO stream into 1-minute bars. Only T records contribute.
let aggregateBars (merged: seq<MboMsg>) : Bar list =
    let bars = ResizeArray<Bar>()
    let mutable currentBucket = Int64.MinValue
    let mutable bOpen = 0.0
    let mutable bHigh = Double.MinValue
    let mutable bLow = Double.MaxValue
    let mutable bClose = 0.0
    let mutable bVol = 0L
    let mutable bCount = 0
    let mutable bNotional = 0.0   // sum(price * size) within bucket
    let mutable sessNotional = 0.0
    let mutable sessVol = 0L

    let flush () =
        if bCount > 0 then
            let barVwap = bNotional / float bVol
            let sessVwap = if sessVol > 0L then Some (sessNotional / float sessVol) else None
            bars.Add({
                BucketStartNs = currentBucket
                Open = bOpen
                High = bHigh
                Low = bLow
                Close = bClose
                Volume = bVol
                TradeCount = bCount
                BarVwap = barVwap
                SessionVwap = sessVwap
            })

    for m in merged do
        if m.Action = ACTION_T then
            let price = priceToUsd m.Price
            let size = int64 m.Size
            let bucket = (m.TsEvent / BAR_NS) * BAR_NS
            if bucket <> currentBucket then
                flush ()
                currentBucket <- bucket
                bOpen <- price
                bHigh <- price
                bLow <- price
                bVol <- 0L
                bCount <- 0
                bNotional <- 0.0
            if price > bHigh then bHigh <- price
            if price < bLow then bLow <- price
            bClose <- price
            bVol <- bVol + size
            bCount <- bCount + 1
            let notional = price * float size
            bNotional <- bNotional + notional
            // Session VWAP anchors at 09:30 ET — only accumulate within RTH.
            if isAtOrAfterRthOpen m.TsEvent then
                sessNotional <- sessNotional + notional
                sessVol <- sessVol + size
    flush ()
    List.ofSeq bars

/// Load a directory of per-venue .dbn.zst files, open them, merge by ts_event,
/// and aggregate to 1-minute bars. Returns the bar list and the list of opened
/// VenueStream values (which the caller must dispose).
let loadDayBars (dayDir: string) : Bar list * VenueStream list =
    let files =
        Directory.GetFiles(dayDir, "*.dbn.zst")
        |> Array.sort
        |> Array.toList
    if files.IsEmpty then
        failwithf "No .dbn.zst files in %s" dayDir
    let venues = files |> List.map openVenue
    let merged = mergeByTsEvent (venues |> List.map (fun v -> v.Records))
    let bars = aggregateBars merged
    bars, venues
