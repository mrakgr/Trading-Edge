module TradingEdge.ReplaySimulator.ReplayEngine

// Replay clock: wakes every TICK_MS wall-clock and advances `multiplier * TICK_MS`
// worth of stream time per tick. Records whose ts_event falls in the new window
// are fed into a BarAggregator; the chart receives a snapshot (current bar +
// any bars that just closed) per tick via a Channel.
//
// Multiplier scaling: 1x → 15ms window (~67 FPS), 5x → 75ms, 30x → 450ms,
// 300x → 4500ms. The chart only redraws once per tick regardless of how many
// trades arrive in that window.

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open TradingEdge.ReplaySimulator.Dbn
open TradingEdge.ReplaySimulator.Bars

/// Wall-clock tick cadence. 15ms ≈ 66.6 FPS.
let TICK_MS : int = 15

/// One frame of replay output. The UI consumes these on its dispatcher thread.
type ReplayTick = {
    /// Stream time at the end of the current window (UTC ns).
    StreamTimeNs: int64
    /// Bars that closed during this tick window (in close order, may be empty).
    ClosedBars: Bar list
    /// Currently-forming bar after this tick (None if no T records seen yet).
    Current: Bar option
    /// Total T records ingested so far (handy for status display).
    TradesTotal: int64
    /// True once the underlying merged stream is exhausted.
    EndOfStream: bool
}

type ReplayState() =
    let speed = ref 1.0
    let paused = ref true   // start paused so the UI controls the initial play
    member _.Speed with get () = speed.Value and set v = speed.Value <- max 0.0 v
    member _.Paused with get () = paused.Value and set v = paused.Value <- v

/// Start the replay producer. The merged enumerable is walked once; the
/// returned ChannelReader yields one ReplayTick per wall-clock tick (15ms).
/// The channel is bounded(1) with DropOldest — if the UI falls behind, ticks
/// are coalesced but closed bars within them are preserved by the channel
/// reader merging them (so visual smoothness drops, data fidelity does not).
///
/// `startCursorNs` (optional) skips the merged stream forward to that UTC ns
/// before the first tick. Use this to begin replay at 09:30 ET instead of the
/// file's first record (which is usually deep premarket).
let start
        (merged: seq<MboMsg>)
        (state: ReplayState)
        (startCursorNs: int64 option)
        (ct: CancellationToken)
        : ChannelReader<ReplayTick> * Task =
    let opts = BoundedChannelOptions(1, FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true)
    let channel = Channel.CreateBounded<ReplayTick>(opts)
    let writer = channel.Writer

    let producer =
        task {
            let enumerator = merged.GetEnumerator()
            let agg = BarAggregator()
            let mutable streamCursor : int64 = Int64.MinValue   // set on first record
            let mutable pendingRec : MboMsg voption = ValueNone
            let mutable trades = 0L
            let mutable eos = false

            // Prime the cursor. If a startCursorNs was given, fast-forward past any
            // records strictly before it (premarket skip). Otherwise anchor at the
            // first record's ts_event so the first tick doesn't dump everything.
            let mutable primed = false
            while not primed do
                if enumerator.MoveNext() then
                    let m = enumerator.Current
                    match startCursorNs with
                    | Some t when m.TsEvent < t -> ()   // skip — before our start
                    | _ ->
                        pendingRec <- ValueSome m
                        streamCursor <-
                            match startCursorNs with
                            | Some t -> t
                            | None -> m.TsEvent
                        primed <- true
                else
                    eos <- true
                    primed <- true

            try
                while not ct.IsCancellationRequested && not eos do
                    if state.Paused then
                        // Still emit an idle frame so the UI can show the current state.
                        let tick = { StreamTimeNs = streamCursor; ClosedBars = []; Current = agg.Current; TradesTotal = trades; EndOfStream = false }
                        let! _ = writer.WaitToWriteAsync(ct)
                        let _ = writer.TryWrite(tick)
                        do! Task.Delay(TICK_MS, ct)
                    else
                        let advanceNs = int64 (state.Speed * float TICK_MS) * 1_000_000L
                        let targetNs = streamCursor + advanceNs
                        let closedBars = ResizeArray<Bar>()

                        // Drain all records with ts_event ≤ targetNs.
                        let mutable keepDraining = true
                        while keepDraining do
                            match pendingRec with
                            | ValueSome m when m.TsEvent <= targetNs ->
                                match agg.Feed m with
                                | NoChange -> ()
                                | Forming _ ->
                                    if m.Action = byte 'T' then trades <- trades + 1L
                                | Closed (c, _) ->
                                    if m.Action = byte 'T' then trades <- trades + 1L
                                    closedBars.Add c
                                if enumerator.MoveNext() then
                                    pendingRec <- ValueSome enumerator.Current
                                else
                                    pendingRec <- ValueNone
                                    eos <- true
                                    keepDraining <- false
                            | ValueSome _ -> keepDraining <- false  // next record is past the window
                            | ValueNone   -> keepDraining <- false; eos <- true

                        streamCursor <- targetNs
                        let tick = {
                            StreamTimeNs = streamCursor
                            ClosedBars = List.ofSeq closedBars
                            Current = agg.Current
                            TradesTotal = trades
                            EndOfStream = eos
                        }
                        let! _ = writer.WaitToWriteAsync(ct)
                        let _ = writer.TryWrite(tick)
                        do! Task.Delay(TICK_MS, ct)

                // Final flush on EOS.
                let finalTick = {
                    StreamTimeNs = streamCursor
                    ClosedBars = []
                    Current = agg.Current
                    TradesTotal = trades
                    EndOfStream = true
                }
                writer.TryWrite(finalTick) |> ignore
            finally
                enumerator.Dispose()
                writer.TryComplete() |> ignore
        }
    channel.Reader, producer
