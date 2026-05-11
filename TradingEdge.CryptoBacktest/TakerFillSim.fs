module TradingEdge.CryptoBacktest.TakerFillSim

open System
open System.IO
open System.Globalization
open System.Collections.Generic
open System.Threading.Tasks
open TradingEdge.Simulation.BinanceLoader
open TradingEdge.CryptoBacktest.OrderflowMA
open TradingEdge.CryptoBacktest.WindowLoader

// =============================================================================
// Post-hoc taker-fill simulator for DonchianFade trips.
// =============================================================================
//
// Design doc: docs/taker_fill_simulator.md
//
// For each trip:
//   - At EntryUs: gather SAME-SIDE aggressive trades in
//     [EntryUs + TSkipUs, EntryUs + WMaxUs] (defaults: [200ms, 3s]).
//     If fewer than NMin same-side trades, SCRATCH the signal.
//     Otherwise compute fillPrice = exp-time-and-size-weighted VWAP.
//   - At ExitUs: symmetric on the OPPOSITE side.
//   - Apply taker fee on each side (default 5bp).
//
// Trade windows come from WindowLoader (one DuckDB range-join per symbol),
// so the simulator only sees pre-filtered slices. The hot loop has no
// timestamp comparisons.
//
// Trips with exit_reason != "normal" (redenomination, endofstream) are
// skipped at CSV-read time — they don't represent real signals and would
// pollute the fill-rate / PF aggregates.

// -----------------------------------------------------------------------------
// Configuration
// -----------------------------------------------------------------------------

/// Fill-price computation rule.
///   CumVolume — accumulate same-side trades chronologically until cumulative
///     DOLLAR volume reaches `CvMultiplier × trip.EffectiveNotional`. Fill at
///     the VWAP of those accumulated trades — the trades that would actually
///     have filled our order plus a safety margin. SCRATCH if the threshold
///     isn't reached within the window. This is the principled default: the
///     fill price reflects only flow that genuinely could have lifted our
///     order, and the scratch decision is based on capacity (notional we'd
///     consume) rather than trade count.
///   TimeEwma — exponential-time-weighted VWAP of every same-side trade in
///     the window (per `TauUs`). Requires `NMin` same-side trades to fill.
///     Kept for v6 reproducibility / side-by-side comparison.
type FillMode =
    | CumVolume
    | TimeEwma

type TakerFillConfig = {
    /// Latency window before any trade can be ours. Default 200_000 us (200 ms).
    TSkipUs: int64
    /// Hard cap on the lookahead window. Default 10_000_000 us (10 s) — longer
    /// than v6's 3s default because the cum-volume rule needs time to
    /// accumulate enough flow on thinner symbols (which often have the best
    /// reversion edge).
    WMaxUs: int64
    /// Fill-price computation rule. Default CumVolume.
    Mode: FillMode
    /// CumVolume: target cumulative dollar volume is `CvMultiplier × notional`.
    /// Default 3.0 — fill against 3× our intended order size before declaring
    /// "we'd have been filled and out". A safety margin over the literal 1×
    /// captures realistic queue-position friction.
    CvMultiplier: float
    /// TimeEwma only: minimum same-side trade count to fill. Below this, scratch.
    /// Default 10.
    NMin: int
    /// TimeEwma only: exponential half-life in microseconds for time-weighted
    /// VWAP. Default 500_000 us (500 ms).
    TauUs: int64
    /// Taker fee per side as a fraction. Default 0.0005 (= 5 bp).
    TakerFee: float
}

let defaultConfig () : TakerFillConfig =
    { TSkipUs = 200_000L
      WMaxUs = 10_000_000L
      Mode = CumVolume
      CvMultiplier = 3.0
      NMin = 10
      TauUs = 500_000L
      TakerFee = 0.0005 }

// -----------------------------------------------------------------------------
// Trip row schema (the subset of the donchian trip CSV we replay).
// -----------------------------------------------------------------------------

type TripRow = {
    Symbol: string
    EntryUs: int64
    ExitUs: int64
    Side: Side
    EntryPrice: float
    ExitPrice: float
    EffectiveNotional: float
    OriginalLine: string
}

let private parseSide (s: string) : Side =
    match s with
    | "long" -> Long
    | "short" -> Short
    | _ -> Flat

let private parseFloat (s: string) : float =
    Double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)

let private parseInt64 (s: string) : int64 =
    Int64.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)

let private fmtFloat (x: float) : string =
    if Double.IsNaN x then "nan"
    elif Double.IsPositiveInfinity x then "inf"
    elif Double.IsNegativeInfinity x then "-inf"
    else x.ToString("R", CultureInfo.InvariantCulture)

/// Read the donchian trips CSV. Skips rows with exit_reason != "normal"
/// (so the output only contains trips that represent real signals). Returns
/// the array of TripRow plus the original header line so the output writer
/// can preserve the schema + append fill columns.
///
/// If the CSV is older and doesn't have exit_reason, every row is treated
/// as "normal" — backwards-compatible.
let loadTrips (path: string) : TripRow[] * string =
    use sr = new StreamReader(path)
    let header = sr.ReadLine()
    let cols = header.Split(',')
    let idx (name: string) =
        let i = Array.IndexOf(cols, name)
        if i < 0 then failwithf "loadTrips: column %s missing in %s" name path
        i
    let optIdx (name: string) =
        Array.IndexOf(cols, name)  // -1 if absent
    let iSym = idx "symbol"
    let iEntryUs = idx "entry_us"
    let iExitUs = idx "exit_us"
    let iSide = idx "side"
    let iEntryPx = idx "entry_price"
    let iExitPx = idx "exit_price"
    let iEffNot = idx "effective_notional"
    let iExitReason = optIdx "exit_reason"
    let result = ResizeArray<TripRow>(capacity = 100_000)
    let mutable line = sr.ReadLine()
    let mutable totalRead = 0
    let mutable skipped = 0
    while not (isNull line) do
        let parts = line.Split(',')
        totalRead <- totalRead + 1
        let keep =
            iExitReason < 0 || parts.[iExitReason] = "normal"
        if keep then
            result.Add {
                Symbol = parts.[iSym]
                EntryUs = parseInt64 parts.[iEntryUs]
                ExitUs = parseInt64 parts.[iExitUs]
                Side = parseSide parts.[iSide]
                EntryPrice = parseFloat parts.[iEntryPx]
                ExitPrice = parseFloat parts.[iExitPx]
                EffectiveNotional = parseFloat parts.[iEffNot]
                OriginalLine = line
            }
        else
            skipped <- skipped + 1
        line <- sr.ReadLine()
    if skipped > 0 then
        printfn "[taker-fill-sim] skipped %d/%d trips with exit_reason != normal" skipped totalRead
    result.ToArray(), header

// -----------------------------------------------------------------------------
// Per-side simulator
// -----------------------------------------------------------------------------

type TakerFillOutcome =
    | Scratched of nTradesSameSide: int
    | Filled of fillPrice: float * nTradesSameSide: int * windowSpanUs: int64

/// Time-EWMA fill (v6 mode). Computes a size-and-time-weighted VWAP over
/// every same-side trade in the window. Scratches when fewer than `cfg.NMin`
/// same-side trades appear.
let private simulateOneSideEwma
    (cfg: TakerFillConfig)
    (takeSide: Side)
    (signalUs: int64)
    (windowTrades: ResizeArray<TradeRow>)
    : TakerFillOutcome =
    let mutable n = 0
    let mutable weightedSum = 0.0
    let mutable weightDenom = 0.0
    let mutable firstUs = 0L
    let mutable lastUs = 0L
    let tStart = signalUs + cfg.TSkipUs
    let tauF = float cfg.TauUs
    let ln2 = log 2.0
    for trade in windowTrades do
        let sameSide =
            match takeSide with
            | Long  -> trade.Sign > 0.0
            | Short -> trade.Sign < 0.0
            | Flat  -> false
        if sameSide then
            let tElapsed = float (trade.TimestampUs - tStart)
            let w = exp (- tElapsed * ln2 / tauF)
            let contribution = w * trade.Quantity
            weightedSum <- weightedSum + contribution * trade.Price
            weightDenom <- weightDenom + contribution
            if n = 0 then firstUs <- trade.TimestampUs
            lastUs <- trade.TimestampUs
            n <- n + 1
    if n < cfg.NMin || weightDenom <= 0.0 then
        Scratched n
    else
        Filled (weightedSum / weightDenom, n, lastUs - firstUs)

/// Cumulative-volume fill (production default). Walks same-side trades in
/// chronological order, accumulating BASE-asset quantity until the running
/// total reaches `cfg.CvMultiplier × targetQty`. The fill price is the VWAP
/// of trades up to that cutoff. Scratches if the threshold isn't met within
/// the window.
///
/// CRITICAL: the threshold is base-asset qty, NOT dollar notional. The entry
/// fills X coins; the exit must unload X coins (not X-dollars-worth, which
/// would be a different qty if price moved). Using qty keeps the entry and
/// exit symmetric on the actual position size.
///
/// `targetQty` is `trip.EffectiveNotional / trip.EntryPrice` — the base-asset
/// quantity the engine would have transacted at entry, and which the exit
/// must also clear.
let private simulateOneSideCumVolume
    (cfg: TakerFillConfig)
    (takeSide: Side)
    (signalUs: int64)
    (targetQty: float)
    (windowTrades: ResizeArray<TradeRow>)
    : TakerFillOutcome =
    let mutable n = 0
    let mutable accumQty = 0.0
    let mutable accumNotional = 0.0
    let mutable firstUs = 0L
    let mutable lastUs = 0L
    let target = cfg.CvMultiplier * targetQty
    if target <= 0.0 then
        Scratched 0
    else
        let mutable hit = false
        let mutable i = 0
        while not hit && i < windowTrades.Count do
            let trade = windowTrades.[i]
            let sameSide =
                match takeSide with
                | Long  -> trade.Sign > 0.0
                | Short -> trade.Sign < 0.0
                | Flat  -> false
            if sameSide then
                accumQty <- accumQty + trade.Quantity
                accumNotional <- accumNotional + trade.Quantity * trade.Price
                if n = 0 then firstUs <- trade.TimestampUs
                lastUs <- trade.TimestampUs
                n <- n + 1
                if accumQty >= target then hit <- true
            i <- i + 1
        if not hit || accumQty <= 0.0 then
            Scratched n
        else
            // VWAP = Σ(price · qty) / Σ(qty)
            Filled (accumNotional / accumQty, n, lastUs - firstUs)

/// Run the appropriate fill rule on a pre-filtered window slice.
///
/// `takeSide` is the side WE'D BE TAKING — Long means we lift offers
/// (want buyer-aggressive trades, sign > 0); Short means we hit bids
/// (want seller-aggressive trades, sign < 0).
///
/// `targetQty` is the BASE-ASSET quantity the trip would have transacted
/// (entry: effective_notional / entry_price; exit: same qty by construction).
/// Consulted only in CumVolume mode; EWMA ignores it.
let simulateOneSide
    (cfg: TakerFillConfig)
    (takeSide: Side)
    (signalUs: int64)
    (targetQty: float)
    (windowTrades: ResizeArray<TradeRow>)
    : TakerFillOutcome =
    if isNull (box windowTrades) || windowTrades.Count = 0 then
        Scratched 0
    else
        match cfg.Mode with
        | CumVolume -> simulateOneSideCumVolume cfg takeSide signalUs targetQty windowTrades
        | TimeEwma  -> simulateOneSideEwma cfg takeSide signalUs windowTrades

// -----------------------------------------------------------------------------
// Per-trip outcome
// -----------------------------------------------------------------------------

type TripTakerFillOutcome = {
    EntryFilled: bool
    EntryFillPrice: float        // NaN if scratched
    EntryNTrades: int
    EntryWindowSpanUs: int64
    EntryEffectivePrice: float   // NaN if scratched
    ExitFilled: bool
    ExitFillPrice: float
    ExitNTrades: int
    ExitWindowSpanUs: int64
    ExitEffectivePrice: float
    TakerRealizedPnL: float      // NaN if either side scratched
    BothFilled: bool
}

let private oppositeSide (s: Side) : Side =
    match s with
    | Long -> Short
    | Short -> Long
    | Flat -> Flat

let simulateTrip
    (cfg: TakerFillConfig)
    (trip: TripRow)
    (entryWindow: ResizeArray<TradeRow>)
    (exitWindow:  ResizeArray<TradeRow>)
    : TripTakerFillOutcome =
    let targetQty =
        if trip.EntryPrice > 0.0 then trip.EffectiveNotional / trip.EntryPrice else 0.0
    let entryOutcome =
        simulateOneSide cfg trip.Side trip.EntryUs targetQty entryWindow
    let exitOutcome  =
        simulateOneSide cfg (oppositeSide trip.Side) trip.ExitUs targetQty exitWindow

    let entryFilled, entryPx, entryN, entrySpan =
        match entryOutcome with
        | Filled (p, n, s) -> true, p, n, s
        | Scratched n -> false, nan, n, 0L
    let exitFilled, exitPx, exitN, exitSpan =
        match exitOutcome with
        | Filled (p, n, s) -> true, p, n, s
        | Scratched n -> false, nan, n, 0L

    // Fee application — taker pays through on entry, pays through on exit.
    //   Long  entry: lift offers → effective = fillPx * (1 + fee)
    //   Long  exit:  hit bids    → effective = fillPx * (1 - fee)
    //   Short entry: hit bids    → effective = fillPx * (1 - fee)
    //   Short exit:  lift offers → effective = fillPx * (1 + fee)
    let entryEff =
        if not entryFilled then nan
        else
            match trip.Side with
            | Long -> entryPx * (1.0 + cfg.TakerFee)
            | Short -> entryPx * (1.0 - cfg.TakerFee)
            | Flat -> nan
    let exitEff =
        if not exitFilled then nan
        else
            match trip.Side with
            | Long -> exitPx * (1.0 - cfg.TakerFee)
            | Short -> exitPx * (1.0 + cfg.TakerFee)
            | Flat -> nan

    let bothFilled = entryFilled && exitFilled
    let realizedPnL =
        if not bothFilled then nan
        elif entryEff <= 0.0 then nan
        else
            let targetQty = trip.EffectiveNotional / entryEff
            match trip.Side with
            | Long  -> (exitEff - entryEff) * targetQty
            | Short -> (entryEff - exitEff) * targetQty
            | Flat  -> 0.0

    { EntryFilled = entryFilled
      EntryFillPrice = entryPx
      EntryNTrades = entryN
      EntryWindowSpanUs = entrySpan
      EntryEffectivePrice = entryEff
      ExitFilled = exitFilled
      ExitFillPrice = exitPx
      ExitNTrades = exitN
      ExitWindowSpanUs = exitSpan
      ExitEffectivePrice = exitEff
      TakerRealizedPnL = realizedPnL
      BothFilled = bothFilled }

// -----------------------------------------------------------------------------
// Output writer
// -----------------------------------------------------------------------------

let outcomeColumns =
    "entry_filled,entry_fill_price,entry_n_trades,entry_window_span_us,entry_effective_price,exit_filled,exit_fill_price,exit_n_trades,exit_window_span_us,exit_effective_price,taker_realized_pnl,both_filled"

let private boolToStr (b: bool) = if b then "1" else "0"

let outcomeRow (o: TripTakerFillOutcome) : string =
    String.concat "," [
        boolToStr o.EntryFilled
        fmtFloat o.EntryFillPrice
        string o.EntryNTrades
        string o.EntryWindowSpanUs
        fmtFloat o.EntryEffectivePrice
        boolToStr o.ExitFilled
        fmtFloat o.ExitFillPrice
        string o.ExitNTrades
        string o.ExitWindowSpanUs
        fmtFloat o.ExitEffectivePrice
        fmtFloat o.TakerRealizedPnL
        boolToStr o.BothFilled
    ]

// -----------------------------------------------------------------------------
// Top-level runner — three-phase pipeline.
//
// Phase 1: split each trip into two independent SideJobs (entry, exit).
// Phase 2: bucket SideJobs by (symbol, date). Each (symbol, date) is one
//          parallel unit of work — load that one parquet, simulate every
//          SideJob whose window falls on that date, write per-side outcomes
//          to an intermediate CSV. No cross-job coordination needed.
// Phase 3: read the per-side outcome CSV, join on trip_idx, compute fee-
//          adjusted prices + realised P&L, write the final trip CSV.
//
// Memory per worker: one day's matching trades. Bounded.
// Parallelism unit: (symbol, date) pair (not symbol). Much better load
// balance — a heavy-history symbol like CRVUSDT with 590 dates spreads
// across all workers instead of serializing in one.
// -----------------------------------------------------------------------------

/// One side of one trip — the atomic unit of work for phase 2.
[<Struct>]
type SideJob = {
    TripIdx: int
    SideKind: string        // "entry" or "exit"
    Symbol: string
    TakeSide: Side          // Long → buyer-aggressive trades; Short → seller-aggressive
    SignalUs: int64         // EntryUs or ExitUs (not the t_skip-adjusted bound)
    WindowStartUs: int64    // SignalUs + TSkipUs
    WindowEndUs: int64      // SignalUs + WMaxUs
    TargetQty: float        // effective_notional / entry_price; same for entry+exit
}

/// Per-side outcome emitted by phase 2. Joined back to trip-level in phase 3.
[<Struct>]
type SideOutcome = {
    TripIdx: int
    SideKind: string
    Filled: bool
    FillPrice: float        // NaN if scratched
    NTrades: int
    WindowSpanUs: int64
}

let private sideOutcomeHeader =
    "trip_idx,side_kind,filled,fill_price,n_trades,window_span_us"

let private writeSideOutcomeRow (sw: StreamWriter) (o: SideOutcome) =
    sw.Write(o.TripIdx)
    sw.Write(',')
    sw.Write(o.SideKind)
    sw.Write(',')
    sw.Write(if o.Filled then '1' else '0')
    sw.Write(',')
    sw.Write(fmtFloat o.FillPrice)
    sw.Write(',')
    sw.Write(o.NTrades)
    sw.Write(',')
    sw.Write(o.WindowSpanUs)
    sw.WriteLine()

/// Run a single SideJob against its pre-loaded window slice.
let private simulateSideJob (cfg: TakerFillConfig) (job: SideJob)
                            (window: ResizeArray<TradeRow>) : SideOutcome =
    let outcome =
        simulateOneSide cfg job.TakeSide job.SignalUs job.TargetQty window
    match outcome with
    | Filled (px, n, span) ->
        { TripIdx = job.TripIdx; SideKind = job.SideKind
          Filled = true; FillPrice = px; NTrades = n; WindowSpanUs = span }
    | Scratched n ->
        { TripIdx = job.TripIdx; SideKind = job.SideKind
          Filled = false; FillPrice = nan; NTrades = n; WindowSpanUs = 0L }

let runTakerFillSim
    (cfg: TakerFillConfig)
    (dataRoot: string)
    (tripsCsvPath: string)
    (outputCsvPath: string)
    (parallelism: int)
    : unit =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let trips, header = loadTrips tripsCsvPath
    printfn "[taker-fill-sim] loaded %d eligible trips from %s" trips.Length tripsCsvPath

    // ---- Phase 1: split into SideJobs and bucket by (symbol, date). ----
    let allJobs =
        trips
        |> Array.mapi (fun i t ->
            // Base-asset quantity that the engine transacted at entry and that
            // the exit must also clear. Identical for both sides by construction.
            let targetQty =
                if t.EntryPrice > 0.0 then t.EffectiveNotional / t.EntryPrice else 0.0
            [| { TripIdx = i; SideKind = "entry"; Symbol = t.Symbol
                 TakeSide = t.Side; SignalUs = t.EntryUs
                 WindowStartUs = t.EntryUs + cfg.TSkipUs
                 WindowEndUs   = t.EntryUs + cfg.WMaxUs
                 TargetQty = targetQty }
               { TripIdx = i; SideKind = "exit"; Symbol = t.Symbol
                 TakeSide = oppositeSide t.Side; SignalUs = t.ExitUs
                 WindowStartUs = t.ExitUs + cfg.TSkipUs
                 WindowEndUs   = t.ExitUs + cfg.WMaxUs
                 TargetQty = targetQty } |])
        |> Array.concat

    // A job whose window straddles midnight (window start date != end date)
    // gets duplicated into both buckets. The duplicate produces the same
    // outcome whichever day's trades it's evaluated against — the per-day
    // window-trade filter naturally restricts to that day's trades, so
    // emitting two outcomes per cross-date job and taking either is fine.
    // But for cleanliness and to avoid duplicate rows in the side CSV, we
    // anchor every job to the date of its WindowStartUs only (the actual
    // signal moment + t_skip — that's the date the trade execution would
    // happen on, even if the latest possible trade in the 3s window is the
    // first millisecond of the next day, which is exceptionally rare for
    // 1m-bar signals where windows are 200ms-3000ms after a minute boundary).
    let dateOfWindow (job: SideJob) = dateOfUs job.WindowStartUs

    let bucketed = Dictionary<struct(string * DateTime), ResizeArray<SideJob>>()
    for job in allJobs do
        let key = struct(job.Symbol, dateOfWindow job)
        match bucketed.TryGetValue key with
        | true, lst -> lst.Add job
        | false, _ ->
            let lst = ResizeArray<SideJob>(4)
            lst.Add job
            bucketed.[key] <- lst

    let totalJobs = allJobs.Length
    let totalPairs = bucketed.Count
    let totalTrips = trips.Length

    printfn "[taker-fill-sim] phase 1 — %d trips → %d side-jobs across %d (symbol, date) pairs"
        totalTrips totalJobs totalPairs
    printfn "[taker-fill-sim] phase 2 — running %d (symbol, date) jobs at parallelism=%d"
        totalPairs parallelism

    // ---- Phase 2: parallel over (symbol, date) pairs. ----
    let sideCsvPath = outputCsvPath + ".sides"
    if File.Exists sideCsvPath then File.Delete sideCsvPath
    Directory.CreateDirectory(Path.GetDirectoryName outputCsvPath) |> ignore
    use sideWriter = new StreamWriter(sideCsvPath)
    sideWriter.WriteLine sideOutcomeHeader
    let sideWriteLock = obj()

    let mutable processedJobs = 0
    let mutable donePairs = 0
    let inFlight = System.Collections.Concurrent.ConcurrentDictionary<string, int64>()

    let progressTimer = new System.Timers.Timer(1000.0, AutoReset = true)
    progressTimer.Elapsed.Add(fun _ ->
        let pj = processedJobs
        let dp = donePairs
        let inflight = inFlight |> Seq.toArray
        let inflightStr =
            if inflight.Length = 0 then ""
            else
                let now = sw.ElapsedMilliseconds
                inflight
                |> Array.sortByDescending (fun kv -> now - kv.Value)
                |> Array.truncate 4
                |> Array.map (fun kv ->
                    sprintf "%s@%.1fs" kv.Key (float (now - kv.Value) / 1000.0))
                |> String.concat " "
        let pctPairs = 100.0 * float dp / float totalPairs
        let pctJobs = 100.0 * float pj / float totalJobs
        let elapsed = sw.Elapsed.TotalSeconds
        let rate = if elapsed > 0.0 then float pj / elapsed else 0.0
        printfn "[taker-fill-sim] %.0fs | pairs %d/%d (%.0f%%) | jobs %d/%d (%.0f%%, %.0f/s) | in-flight: %s"
            elapsed dp totalPairs pctPairs pj totalJobs pctJobs rate inflightStr)
    progressTimer.Start()

    let processPair (symbol: string, date: DateTime, jobs: ResizeArray<SideJob>) : unit =
        let pairLabel = sprintf "%s/%s" symbol (date.ToString "yyyy-MM-dd")
        let startMs = sw.ElapsedMilliseconds
        inFlight.[pairLabel] <- startMs
        try
            // Build window specs (the loader's narrower struct).
            let specs = ResizeArray<WindowSpec>(jobs.Count)
            for j in jobs do
                specs.Add {
                    TripIdx = j.TripIdx
                    SideKind = j.SideKind
                    StartUs = j.WindowStartUs
                    EndUs   = j.WindowEndUs
                }
            let loaded = loadDayWindows dataRoot symbol date specs

            // Simulate each job, collect outcomes.
            let outcomes = ResizeArray<SideOutcome>(jobs.Count)
            for j in jobs do
                let key = { TripIdx = j.TripIdx; SideKind = j.SideKind }
                let window =
                    match loaded.TryGetValue key with
                    | true, lst -> lst
                    | false, _  -> ResizeArray<TradeRow>(0)
                outcomes.Add(simulateSideJob cfg j window)

            // Write atomically.
            lock sideWriteLock (fun () ->
                for o in outcomes do writeSideOutcomeRow sideWriter o
                processedJobs <- processedJobs + jobs.Count
                donePairs <- donePairs + 1)
        with ex ->
            eprintfn "[taker-fill-sim] %s FAILED: %s" pairLabel ex.Message
            // Still emit scratched outcomes for every job in this pair so
            // phase 3 has the rows.
            lock sideWriteLock (fun () ->
                for j in jobs do
                    let o = {
                        TripIdx = j.TripIdx; SideKind = j.SideKind
                        Filled = false; FillPrice = nan; NTrades = 0
                        WindowSpanUs = 0L
                    }
                    writeSideOutcomeRow sideWriter o
                processedJobs <- processedJobs + jobs.Count
                donePairs <- donePairs + 1)
        inFlight.TryRemove pairLabel |> ignore

    // Flatten the bucketed dict into a parallelisable seq of (symbol, date, jobs).
    // Sort by job count descending so the heaviest pairs start first — better
    // tail-latency when parallelism doesn't divide work cleanly.
    let pairWork =
        bucketed
        |> Seq.map (fun kv ->
            let struct(s, d) = kv.Key
            (s, d, kv.Value))
        |> Seq.sortByDescending (fun (_, _, jobs) -> jobs.Count)
        |> Seq.toArray

    let opts = ParallelOptions(MaxDegreeOfParallelism = parallelism)
    Parallel.ForEach(pairWork, opts, fun work ->
        processPair work)
    |> ignore

    progressTimer.Stop()
    progressTimer.Dispose()
    sideWriter.Flush()
    sideWriter.Close()

    let phase2WallSec = sw.Elapsed.TotalSeconds
    printfn "[taker-fill-sim] phase 2 done — %d jobs in %.2fs (%.0f jobs/s)"
        totalJobs phase2WallSec (float totalJobs / phase2WallSec)

    // ---- Phase 3: join side outcomes back into the trip CSV. ----
    printfn "[taker-fill-sim] phase 3 — merging side outcomes into trip CSV..."
    let phase3Start = sw.Elapsed.TotalSeconds

    // Load the side-outcome CSV into per-trip (entry, exit) pairs.
    let entryByTrip = Dictionary<int, SideOutcome>(totalTrips)
    let exitByTrip  = Dictionary<int, SideOutcome>(totalTrips)
    do
        use sr = new StreamReader(sideCsvPath)
        let _ = sr.ReadLine()  // skip header
        let mutable line = sr.ReadLine()
        while not (isNull line) do
            let parts = line.Split(',')
            let tripIdx = Int32.Parse(parts.[0], CultureInfo.InvariantCulture)
            let sideKind = parts.[1]
            let filled = parts.[2] = "1"
            let fillPrice =
                if parts.[3] = "nan" then nan
                else parseFloat parts.[3]
            let nTrades = Int32.Parse(parts.[4], CultureInfo.InvariantCulture)
            let spanUs = parseInt64 parts.[5]
            let outcome = {
                TripIdx = tripIdx; SideKind = sideKind
                Filled = filled; FillPrice = fillPrice
                NTrades = nTrades; WindowSpanUs = spanUs
            }
            match sideKind with
            | "entry" -> entryByTrip.[tripIdx] <- outcome
            | "exit"  -> exitByTrip.[tripIdx]  <- outcome
            | _ -> ()
            line <- sr.ReadLine()

    // Default-scratched outcome for trips whose side never appeared (shouldn't
    // happen unless a parquet was completely missing AND no rows got emitted).
    let scratched (tripIdx: int) (sideKind: string) = {
        TripIdx = tripIdx; SideKind = sideKind
        Filled = false; FillPrice = nan; NTrades = 0; WindowSpanUs = 0L
    }

    if File.Exists outputCsvPath then File.Delete outputCsvPath
    use writer = new StreamWriter(outputCsvPath)
    writer.WriteLine(header + "," + outcomeColumns)
    for (i, trip) in trips |> Array.mapi (fun i t -> (i, t)) do
        let e =
            match entryByTrip.TryGetValue i with
            | true, o -> o
            | false, _ -> scratched i "entry"
        let x =
            match exitByTrip.TryGetValue i with
            | true, o -> o
            | false, _ -> scratched i "exit"
        let entryEff =
            if not e.Filled then nan
            else
                match trip.Side with
                | Long -> e.FillPrice * (1.0 + cfg.TakerFee)
                | Short -> e.FillPrice * (1.0 - cfg.TakerFee)
                | Flat -> nan
        let exitEff =
            if not x.Filled then nan
            else
                match trip.Side with
                | Long -> x.FillPrice * (1.0 - cfg.TakerFee)
                | Short -> x.FillPrice * (1.0 + cfg.TakerFee)
                | Flat -> nan
        let bothFilled = e.Filled && x.Filled
        let realizedPnL =
            if not bothFilled || entryEff <= 0.0 then nan
            else
                let targetQty = trip.EffectiveNotional / entryEff
                match trip.Side with
                | Long  -> (exitEff - entryEff) * targetQty
                | Short -> (entryEff - exitEff) * targetQty
                | Flat  -> 0.0
        let outcome = {
            EntryFilled = e.Filled
            EntryFillPrice = e.FillPrice
            EntryNTrades = e.NTrades
            EntryWindowSpanUs = e.WindowSpanUs
            EntryEffectivePrice = entryEff
            ExitFilled = x.Filled
            ExitFillPrice = x.FillPrice
            ExitNTrades = x.NTrades
            ExitWindowSpanUs = x.WindowSpanUs
            ExitEffectivePrice = exitEff
            TakerRealizedPnL = realizedPnL
            BothFilled = bothFilled
        }
        writer.WriteLine(trip.OriginalLine + "," + outcomeRow outcome)

    writer.Flush()
    let phase3WallSec = sw.Elapsed.TotalSeconds - phase3Start
    printfn "[taker-fill-sim] phase 3 done — wrote %d trips in %.2fs"
        totalTrips phase3WallSec

    // Optional: keep .sides CSV for debugging; comment the next line out to keep.
    try File.Delete sideCsvPath with _ -> ()

    printfn "[taker-fill-sim] done — wrote %d trips in %.2fs (%.0f trips/s)"
        trips.Length sw.Elapsed.TotalSeconds
        (float trips.Length / sw.Elapsed.TotalSeconds)
