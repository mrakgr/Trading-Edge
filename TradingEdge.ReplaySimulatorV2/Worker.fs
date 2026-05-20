module TradingEdge.ReplaySimulatorV2.Worker

// Background worker that drives the Player off the UI thread. Every TICK_MS
// of wall-clock time it advances stream-time by `speed * TICK_MS` and writes
// the resulting PlayResult into a bounded(1) Channel with DropOldest. The
// UI thread polls the channel on its own dispatcher timer.
//
// Two locks of state shared with the UI thread:
//   * ReplayState (Speed, Paused) — written by UI button handlers, read by worker.
//   * The bounded channel — single-writer (worker), single-reader (UI).
//
// All Player.Play work happens here, not on the UI thread.

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open TradingEdge.ReplaySimulatorV2.Snapshots
open TradingEdge.ReplaySimulatorV2.Play

/// Wall-clock tick cadence in ms. ~67 FPS.
let TICK_MS : int = 15

type ReplayState() =
    let speed = ref 1.0
    let paused = ref true
    member _.Speed with get () = speed.Value and set v = speed.Value <- max 0.0 v
    member _.Paused with get () = paused.Value and set v = paused.Value <- v

/// Spin up the worker. Returns the channel reader the UI consumes from, plus
/// the producer task (useful for clean shutdown via the cancellation token).
let start
        (store: SnapshotStore)
        (state: ReplayState)
        (startCursorNs: int64)
        (ct: CancellationToken)
        : ChannelReader<PlayResult> * Task =
    let opts =
        BoundedChannelOptions(
            1,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true)
    let channel = Channel.CreateBounded<PlayResult>(opts)
    let writer = channel.Writer

    let producer =
        task {
            let player = Player(store)
            let mutable t = startCursorNs
            let lastTs =
                if store.Records.Length = 0 then 0L
                else store.Records.[store.Records.Length - 1].TsEvent

            // Emit an initial frame so the UI has something to render before Play
            // is pressed.
            let initial = player.Play t
            writer.TryWrite(initial) |> ignore

            try
                use timer = new PeriodicTimer(TimeSpan.FromMilliseconds(float TICK_MS))
                let mutable keepRunning = true
                while keepRunning && not ct.IsCancellationRequested do
                    let! more = timer.WaitForNextTickAsync(ct)
                    if not more then keepRunning <- false
                    else
                        if not state.Paused then
                            let advanceNs = int64 (state.Speed * float TICK_MS) * 1_000_000L
                            t <- min lastTs (t + advanceNs)
                            let result = player.Play t
                            // TryWrite with DropOldest replaces any pending frame the
                            // UI hasn't picked up yet.
                            writer.TryWrite(result) |> ignore
            with :? OperationCanceledException -> ()
            writer.TryComplete() |> ignore
        }
    channel.Reader, producer
