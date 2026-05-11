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

type TakerFillConfig = {
    /// Latency window before any trade can be ours. Default 200_000 us (200 ms).
    TSkipUs: int64
    /// Hard cap on the lookahead window. Default 3_000_000 us (3 s).
    WMaxUs: int64
    /// Minimum same-side aggressive trade count within [TSkipUs, WMaxUs] for
    /// the signal to be tradeable. Below this, the signal is SCRATCHED.
    /// Default 10.
    NMin: int
    /// Exponential half-life in microseconds for the time-weighted VWAP.
    /// Default 500_000 us (500 ms). Decay rate = ln(2) / Tau.
    TauUs: int64
    /// Taker fee per side as a fraction. Default 0.0005 (= 5 bp).
    TakerFee: float
}

let defaultConfig () : TakerFillConfig =
    { TSkipUs = 200_000L
      WMaxUs = 3_000_000L
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

/// Run the design-doc algorithm on a pre-filtered window slice.
///
/// `takeSide` is the side WE'D BE TAKING — Long means we lift offers
/// (want buyer-aggressive trades, sign > 0); Short means we hit bids
/// (want seller-aggressive trades, sign < 0).
let simulateOneSide
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

    if isNull (box windowTrades) then
        Scratched 0
    else
        for trade in windowTrades do
            // Aggressor filter:
            //   Long takes from offers → sign > 0 (buyer-aggressive)
            //   Short takes from bids → sign < 0 (seller-aggressive)
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
    let entryOutcome = simulateOneSide cfg trip.Side trip.EntryUs entryWindow
    let exitOutcome  = simulateOneSide cfg (oppositeSide trip.Side) trip.ExitUs exitWindow

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
// Top-level runner — one DuckDB query per symbol, parallel over symbols.
// -----------------------------------------------------------------------------

let runTakerFillSim
    (cfg: TakerFillConfig)
    (dataRoot: string)
    (tripsCsvPath: string)
    (outputCsvPath: string)
    (parallelism: int)
    : unit =
    let trips, header = loadTrips tripsCsvPath
    printfn "[taker-fill-sim] loaded %d eligible trips from %s" trips.Length tripsCsvPath

    // Group by symbol (one DuckDB query per symbol; cheap because each query
    // only scans the parquet row groups overlapping the trip windows).
    let bySymbol =
        trips
        |> Array.mapi (fun i t -> (i, t))
        |> Array.groupBy (fun (_, t) -> t.Symbol)

    Directory.CreateDirectory(Path.GetDirectoryName outputCsvPath) |> ignore
    if File.Exists outputCsvPath then File.Delete outputCsvPath
    use writer = new StreamWriter(outputCsvPath)
    writer.WriteLine(header + "," + outcomeColumns)
    let writeLock = obj()
    let mutable processed = 0
    let total = trips.Length
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let opts = ParallelOptions(MaxDegreeOfParallelism = parallelism)
    Parallel.ForEach(bySymbol, opts, fun (symbol: string, symTrips: (int * TripRow)[]) ->
        try
            // Build window specs: one entry window + one exit window per trip.
            // Window index in WindowLoader is the trip's original index in the
            // global CSV (not the per-symbol index), so lookups in the
            // returned dictionary use the same key.
            let specs =
                symTrips
                |> Array.collect (fun (i, t) ->
                    [| (i, "entry", t.EntryUs + cfg.TSkipUs, t.EntryUs + cfg.WMaxUs)
                       (i, "exit",  t.ExitUs  + cfg.TSkipUs, t.ExitUs  + cfg.WMaxUs) |])
            let loaded = loadSymbolWindows dataRoot symbol specs

            let getWindow tripIdx sideKind =
                let key = { TripIdx = tripIdx; SideKind = sideKind }
                match loaded.ByWindow.TryGetValue key with
                | true, lst -> lst
                | false, _  -> ResizeArray<TradeRow>(0)

            let symLines = ResizeArray<string>(symTrips.Length)
            for (origIdx, trip) in symTrips do
                let entryWin = getWindow origIdx "entry"
                let exitWin  = getWindow origIdx "exit"
                let outcome = simulateTrip cfg trip entryWin exitWin
                symLines.Add(trip.OriginalLine + "," + outcomeRow outcome)

            lock writeLock (fun () ->
                for ln in symLines do writer.WriteLine ln
                processed <- processed + symTrips.Length
                if processed % 5_000 < symTrips.Length then
                    printfn "[taker-fill-sim] %d / %d trips processed (%.1fs)"
                        processed total sw.Elapsed.TotalSeconds)
        with ex ->
            eprintfn "[taker-fill-sim] symbol %s FAILED: %s" symbol ex.Message)
    |> ignore

    printfn "[taker-fill-sim] done — wrote %d trips in %.2fs"
        trips.Length sw.Elapsed.TotalSeconds
