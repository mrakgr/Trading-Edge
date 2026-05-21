module TradingEdge.ReplaySimulatorV2.Worker

// Background worker that drives the Player off the UI thread.
//
// Two channels:
//   * Inbox (UI + timer → worker): WorkerMsg messages. UI button handlers post
//     SetSpeed / SetPaused; a private PeriodicTimer task posts Tick at TICK_MS
//     cadence. The worker is the sole reader, with all replay state (speed,
//     paused, cursor) as private locals.
//   * Outbox (worker → UI): bounded(1) DropOldest channel of Snapshot. UI
//     polls it on its dispatcher timer.
//
// Single-reader on the inbox means every state mutation and every advance is
// serial — no shared mutable state with the UI, no locks.

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open TradingEdge.ReplaySimulatorV2.Snapshots
open TradingEdge.ReplaySimulatorV2.Play

/// Wall-clock tick cadence in ms. ~67 FPS.
let TICK_MS : int = 15

type WorkerMsg =
    | SetSpeed of float
    | SetPaused of bool
    | Tick

type WorkerHandle = {
    /// UI posts SetSpeed / SetPaused here. The worker's own timer task posts
    /// Tick here.
    Inbox: ChannelWriter<WorkerMsg>
    /// UI reads rendered Snapshots from here.
    Outbox: ChannelReader<Snapshot>
    /// The producer task — await for clean shutdown via the cancellation token.
    Producer: Task
}

/// Spin up the worker. Returns the handle holding both channels plus the
/// producer task.
let start
        (store: SnapshotStore)
        (startCursorNs: int64)
        (ct: CancellationToken)
        : WorkerHandle =
    // Inbox: unbounded. Ticks at 67 Hz are tiny structs and the worker drains
    // them as fast as it can produce snapshots; control messages are rare.
    let inbox = Channel.CreateUnbounded<WorkerMsg>(
        UnboundedChannelOptions(SingleReader = true, SingleWriter = false))
    // Outbox: bounded(1) DropOldest — the UI only ever needs the freshest frame.
    let outboxOpts =
        BoundedChannelOptions(
            1,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true)
    let outbox = Channel.CreateBounded<Snapshot>(outboxOpts)

    let lastTs =
        if store.Records.Length = 0 then 0L
        else store.Records.[store.Records.Length - 1].TsEvent

    // Timer task: posts Tick to the inbox at TICK_MS cadence. Lives until ct
    // is cancelled or the inbox is completed.
    let timerTask =
        task {
            try
                use timer = new PeriodicTimer(TimeSpan.FromMilliseconds(float TICK_MS))
                let mutable keepRunning = true
                while keepRunning && not ct.IsCancellationRequested do
                    let! more = timer.WaitForNextTickAsync(ct)
                    if not more then keepRunning <- false
                    else
                        inbox.Writer.TryWrite(Tick) |> ignore
            with :? OperationCanceledException -> ()
        }

    // Producer: drain the inbox, react. All replay state lives here as locals.
    let producer =
        task {
            let player = Player(store)
            let mutable speed = 1.0
            let mutable paused = true
            let mutable t = startCursorNs

            // Emit an initial frame so the UI has something to render before
            // Play is pressed.
            let initial : Snapshot = player.Play t
            outbox.Writer.TryWrite(initial) |> ignore

            try
                let mutable keepReading = true
                while keepReading && not ct.IsCancellationRequested do
                    let! ok = inbox.Reader.WaitToReadAsync(ct)
                    if not ok then keepReading <- false
                    else
                        let mutable msg = Unchecked.defaultof<WorkerMsg>
                        while inbox.Reader.TryRead(&msg) do
                            match msg with
                            | SetSpeed v -> speed <- max 0.0 v
                            | SetPaused p -> paused <- p
                            | Tick ->
                                if not paused then
                                    let advanceNs = int64 (speed * float TICK_MS) * 1_000_000L
                                    t <- min lastTs (t + advanceNs)
                                    let result = player.Play t
                                    outbox.Writer.TryWrite(result) |> ignore
            with :? OperationCanceledException -> ()

            // Tear down the timer so it stops feeding a dead inbox.
            inbox.Writer.TryComplete() |> ignore
            outbox.Writer.TryComplete() |> ignore
        }

    // Best-effort: when the producer exits, the timer task will see ct or
    // exit on its own. Returning both is overkill; producer is the one the
    // caller awaits.
    let _ = timerTask

    { Inbox = inbox.Writer; Outbox = outbox.Reader; Producer = producer }
