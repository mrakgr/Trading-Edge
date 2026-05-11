module TradingEdge.CryptoBacktest.LimitFillSim

open System
open System.IO
open System.Globalization
open System.Collections.Generic
open Nito.Collections
open TradingEdge.Simulation.BinanceLoader
open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.OrderflowMA
open TradingEdge.CryptoBacktest.OrderflowDonchianFade
open TradingEdge.CryptoBacktest.TradeLoader

// =============================================================================
// Post-hoc limit-order fill simulator for DonchianFade trips
// =============================================================================
//
// Architectural reference: TradingEdge.Orb/Pipeline.fs lines 536-732
// (FillSimulator). We borrow the order-state machine — a single resting
// limit order that accumulates partial fills via a running FilledQuantity,
// orders auto-clear when fully filled, latency-deferred price/qty updates
// via a deque of (value, activation_time) pairs — but adapt for our
// crypto-perps case:
//
//   - 1-second time bars instead of volume bars
//   - explicit re-peg / ratchet trail rules on bar.High / bar.Low
//   - microsecond timestamps throughout (no DateTime conversions)
//   - Nito.Collections.Deque instead of an unbounded F# list (per user's
//     comment that the Orb list-based version was wasteful — the deque
//     gives bounded memory + O(1) front/back ops)
//   - 3-phase per-trip timeline:
//       A. entry-trail from EntryUs until filled or ExitUs cap
//       B. passive hold from EntryFillUs through engine ExitUs
//       C. exit-trail from ExitUs until filled or end-of-data
//
// Single-trip simulator runs purely in-memory; the streaming runner groups
// trips by (symbol, date) so each trade parquet is loaded once per day.

// -----------------------------------------------------------------------------
// Configuration
// -----------------------------------------------------------------------------

type TrailMode =
    /// Limit follows bar.Low (long buy-limit) / bar.High (short sell-limit) on
    /// each closed 1s bar. Can move adverse — the limit chases the bar wherever
    /// price has been in the last second. This is the user-locked v0 rule.
    | RePeg
    /// Limit moves only favorable: long buy-limit ratchets UP (max with new bar.Low),
    /// short sell-limit ratchets DOWN (min with new bar.High). On the exit side
    /// the symmetry flips (long position's sell-limit ratchets DOWN with new
    /// bar.High; short position's buy-limit ratchets UP with new bar.Low).
    /// Misses fills when price runs away but never fills at a worse price than
    /// the engine's recorded EntryPrice/ExitPrice.
    | Ratchet

type LimitFillConfig = {
    TrailMode: TrailMode
    /// Maker fee per side as a fraction. Default 0.0002 (= 2 bp = Binance USDT-perps
    /// base-tier maker fee). Charged on each side proportional to filled qty.
    MakerFee: float
    /// Probability that a crossing trade fails to fill our resting order (queue-position
    /// proxy). Default 0.30. Range [0, 1).
    RejectionRate: float
    /// Round-trip latency between order placement / repricing and the order being live
    /// at the new state, in microseconds. Default 100,000 (100 ms). A price/qty update
    /// posted at time t activates at t + LatencyUs.
    LatencyUs: int64
    /// PRNG seed for rejection sampling.
    Seed: int
    /// Number of trade-parquet days to load past the entry day, to give the
    /// exit trail room when trips hold across multiple days. Total load is
    /// (1 + LookaheadDays) days per (symbol, entry-date) group.
    /// Default 1 (sufficient for DonchianScalp's ~1h holds). FlowSwing's
    /// median 1.77d / p95 4.5d / max 16d holds need a larger value — pass
    /// --lookahead-days 6 (covers p95+) or 18 (covers max).
    LookaheadDays: int
}

let defaultConfig () : LimitFillConfig =
    { TrailMode = RePeg
      MakerFee = 0.0002
      RejectionRate = 0.30
      LatencyUs = 100_000L
      Seed = 12345
      LookaheadDays = 1 }

// -----------------------------------------------------------------------------
// Latency-deferred price/qty register
// -----------------------------------------------------------------------------
//
// Mirrors Orb's `Prices: struct (float * DateTime) list` and `Quantities: struct (int * DateTime) list`
// but on a Nito Deque rather than a singly-linked list. Newer entries pushed to
// the FRONT; the deque represents the most-recent-first history of pending
// updates. ActiveAt(now) walks from front looking for the first entry whose
// activation time is <= now. Older entries behind the active one are dead and
// can be evicted from the back.
//
// Bounded memory: at 1s bar cadence + sub-second latency we expect at most
// 2-3 in-flight entries (the previous active + the just-pushed pending +
// occasional skew). The deque keeps these inline without per-update heap
// allocations.

[<Struct>]
type private Latched = {
    Value: float
    ActivationUs: int64
}

[<Sealed>]
type LatchedRegister() =
    let dq = Deque<Latched>(4)

    /// Push a new (value, activation_time) entry. Newer entries go to the FRONT.
    member _.Push(value: float, activationUs: int64) =
        dq.AddToFront { Value = value; ActivationUs = activationUs }

    /// Return the active value at `now`: the front entry whose ActivationUs <= now.
    /// Returns NaN when no entry has activated yet (order not yet live due to latency).
    /// Side-effect: trims dead entries from the back so the deque stays bounded.
    member _.ActiveAt(now: int64) : float =
        // Walk from the FRONT (newest first). The first entry whose ActivationUs
        // is <= now is the active value at `now`.
        let mutable found = nan
        let mutable i = 0
        let mutable stop = false
        while not stop && i < dq.Count do
            let e = dq.[i]
            if e.ActivationUs <= now then
                found <- e.Value
                stop <- true
            i <- i + 1
        // Trim tail: anything strictly older than the active entry can never
        // be referenced again (we always look for the newest activated).
        if not (Double.IsNaN found) then
            // Drop everything after index i-1 (those are older than the found
            // active entry).
            while dq.Count > i do
                dq.RemoveFromBack() |> ignore
        found

    member _.Count = dq.Count

// -----------------------------------------------------------------------------
// Per-side fill accumulator
// -----------------------------------------------------------------------------
//
// One accumulator per phase (entry, exit). Tracks running filled qty and
// price-weighted notional so the VWAP at the end is `Notional / Filled`.

type FillAccumulator() =
    let mutable target = 0.0
    let mutable filled = 0.0
    let mutable notional = 0.0
    let mutable firstFillUs = 0L
    let mutable lastFillUs = 0L

    member _.Init(targetQty: float) =
        target <- targetQty
        filled <- 0.0
        notional <- 0.0
        firstFillUs <- 0L
        lastFillUs <- 0L

    member _.Target = target
    member _.Filled = filled
    member _.Remaining = target - filled
    member _.IsDone = filled >= target - 1e-12
    member _.FirstFillUs = firstFillUs
    member _.LastFillUs = lastFillUs
    member _.AvgFillPrice =
        if filled > 0.0 then notional / filled else nan

    /// Add a partial fill of `qty` at `price` at time `tsUs`. No bounds checking
    /// is done here — caller must ensure qty <= Remaining.
    member _.Add(price: float, qty: float, tsUs: int64) =
        if firstFillUs = 0L then firstFillUs <- tsUs
        lastFillUs <- tsUs
        filled <- filled + qty
        notional <- notional + price * qty

// -----------------------------------------------------------------------------
// Outcome record
// -----------------------------------------------------------------------------

type LimitFillOutcome = {
    /// Engine-intended base-asset qty (= effective_notional / entry_price).
    TargetQty: float
    /// Base-asset qty that filled in Phase A. Range [0, TargetQty].
    EntryFillQty: float
    /// Timestamp of the FIRST partial entry fill (0 if EntryFillQty = 0).
    EntryFirstFillUs: int64
    /// Timestamp of the LAST partial entry fill (0 if EntryFillQty = 0).
    EntryLastFillUs: int64
    /// VWAP of all entry-side partial fills (NaN if EntryFillQty = 0).
    EntryAvgFillPrice: float
    /// Base-asset qty that exited in Phase C. Range [0, EntryFillQty].
    ExitFillQty: float
    EntryFillFraction: float
    ExitFirstFillUs: int64
    ExitLastFillUs: int64
    ExitAvgFillPrice: float
    /// EntryFillQty - ExitFillQty: the unexited remainder marked at end-of-slice.
    ResidualQty: float
    /// Last trade price in the loaded slice (used to mark the residual).
    ResidualMarkPrice: float
    /// Round-trip P&L on min(entry_qty, exit_qty) at signed (exit_avg - entry_avg),
    /// with maker fees on both sides of the round-tripped portion.
    LimitRealizedPnL: float
    /// Mark-to-market on the residual position (entry-side fee only — no exit fee
    /// since unrealized).
    LimitResidualPnL: float
    /// LimitRealizedPnL + LimitResidualPnL.
    LimitNetPnL: float
    /// Wall-clock seconds from EntryFirstFillUs to ExitLastFillUs (or to the
    /// residual mark time if no exit fill). 0 when EntryFillQty = 0.
    LimitSecondsHeld: int
}

let private emptyOutcome (target: float) (markPrice: float) : LimitFillOutcome =
    { TargetQty = target
      EntryFillQty = 0.0
      EntryFillFraction = 0.0
      EntryFirstFillUs = 0L
      EntryLastFillUs = 0L
      EntryAvgFillPrice = nan
      ExitFillQty = 0.0
      ExitFirstFillUs = 0L
      ExitLastFillUs = 0L
      ExitAvgFillPrice = nan
      ResidualQty = 0.0
      ResidualMarkPrice = markPrice
      LimitRealizedPnL = 0.0
      LimitResidualPnL = 0.0
      LimitNetPnL = 0.0
      LimitSecondsHeld = 0 }

// -----------------------------------------------------------------------------
// Single-trip simulation
// -----------------------------------------------------------------------------

/// Compute the new trail price after a 1s bar closes, given the trail rule,
/// the trip side (so we know which axis of the bar to use), and the phase.
let private newTrailPrice
    (cfg: LimitFillConfig)
    (side: Side)
    (isEntryPhase: bool)
    (currentActive: float)
    (bar: SignedBar)
    : float =
    // On entry: long needs buy-limit (low side), short needs sell-limit (high side).
    // On exit: long position's exit is sell-limit (high side); short position's
    // exit is buy-limit (low side). The "isLowSideLimit" boolean captures this.
    let isLowSideLimit =
        match side, isEntryPhase with
        | Long, true | Short, false -> true   // buy-limit
        | Short, true | Long, false -> false  // sell-limit
        | Flat, _ -> false  // unreachable in practice
    let candidate = if isLowSideLimit then bar.Low else bar.High
    match cfg.TrailMode with
    | RePeg -> candidate
    | Ratchet ->
        if Double.IsNaN currentActive then candidate
        elif isLowSideLimit then
            // long buy-limit ratchets UP; long-pos sell-limit on exit symmetric
            // is sell-limit so this branch isn't hit for it. Only entry-long /
            // exit-short cases reach here, both are buy-limits ratcheting UP.
            max currentActive candidate
        else
            // sell-limit: ratchets DOWN.
            min currentActive candidate

/// Try to fill the accumulator against a single crossing trade. Returns true if
/// the order is now fully filled (caller should advance phases).
let private tryFill
    (acc: FillAccumulator)
    (rng: Random)
    (cfg: LimitFillConfig)
    (limitPrice: float)
    (trade: Trade)
    : bool =
    if acc.IsDone then true
    elif Double.IsNaN limitPrice then false  // order not yet live
    elif rng.NextDouble() < cfg.RejectionRate then false  // queue-position rejection
    else
        let fillQty = min acc.Remaining trade.Quantity
        if fillQty > 0.0 then
            acc.Add(limitPrice, fillQty, trade.TimestampUs)
        acc.IsDone

/// Lazy day-by-day trade stream. Holds at most one day's Trade[] in memory at
/// a time; when the current day is exhausted, drops the reference (lets the GC
/// reclaim the array) and loads the next day via `loadDay`. The caller passes
/// in `loadDay(dayOffset)` which returns the trades for the day at offset
/// dayOffset from the entry-date (0 = entry day, 1 = entry day + 1, ...).
/// Returning [||] from loadDay means "no more days" — the stream terminates.
///
/// The motivation for this design over loading all days upfront: FlowSwing's
/// p95 hold is 4.5 days, so a 6-day load buffer per (symbol, date) group at any
/// parallelism level multiplies memory by 6. With heavy mid-cap symbols having
/// hundreds of MB of trades per day, the working set easily exceeds 13 GB and
/// triggers the OOM-killer. Day-by-day streaming caps per-trip memory at a
/// single day's worth of trades.
[<Sealed>]
type TradeStream(loadDay: int -> Trade[], maxDays: int) =
    let mutable curDay : Trade[] = loadDay 0
    let mutable curIdx = 0
    let mutable nextDayIdx = 1
    let mutable lastPrice = if curDay.Length > 0 then curDay.[curDay.Length - 1].Price else nan
    let mutable totalSeen = curDay.Length

    /// Advance to the first trade whose TimestampUs >= targetUs. Skips entire
    /// days if their last trade is older than targetUs.
    member self.SeekTo(targetUs: int64) =
        // Within current day: binary-search if useful, otherwise linear forward.
        while curIdx < curDay.Length && curDay.[curIdx].TimestampUs < targetUs do
            curIdx <- curIdx + 1
        // If we exhausted current day, advance days until target found or stream done.
        while curIdx >= curDay.Length && nextDayIdx < maxDays do
            curDay <- loadDay nextDayIdx
            nextDayIdx <- nextDayIdx + 1
            curIdx <- 0
            totalSeen <- totalSeen + curDay.Length
            if curDay.Length > 0 then
                lastPrice <- curDay.[curDay.Length - 1].Price
            // skip into the new day if needed
            while curIdx < curDay.Length && curDay.[curIdx].TimestampUs < targetUs do
                curIdx <- curIdx + 1

    /// Pull the next trade. Returns ValueNone when stream is exhausted.
    member self.TryNext() : Trade voption =
        // Advance days until we have a trade or exhaust all.
        while curIdx >= curDay.Length && nextDayIdx < maxDays do
            curDay <- loadDay nextDayIdx
            nextDayIdx <- nextDayIdx + 1
            curIdx <- 0
            totalSeen <- totalSeen + curDay.Length
            if curDay.Length > 0 then
                lastPrice <- curDay.[curDay.Length - 1].Price
        if curIdx < curDay.Length then
            let t = curDay.[curIdx]
            curIdx <- curIdx + 1
            ValueSome t
        else
            ValueNone

    /// Last trade price seen so far (for residual mark when stream exhausts).
    /// NaN if no trade has been seen yet.
    member _.LastPrice = lastPrice

    /// Total trades seen across all loaded days (informational only).
    member _.TotalSeen = totalSeen

/// Simulate a single trip's entry+exit fills against a streaming trade source.
/// `stream` covers at least the trip's [EntryUs, ExitUs + exit-trail window].
/// The exit phase walks until full fill or end-of-stream; any unexited residual
/// is marked at the last trade's price.
let simulateTrip
    (cfg: LimitFillConfig)
    (trip: DonchianRoundTrip)
    (stream: TradeStream)
    : LimitFillOutcome =
    let rng = Random(cfg.Seed)
    let target =
        if trip.EntryPrice > 0.0 then trip.EffectiveNotional / trip.EntryPrice
        else 0.0

    if target <= 0.0 || trip.Side = Flat then
        let mark = if Double.IsNaN stream.LastPrice then trip.EntryPrice else stream.LastPrice
        emptyOutcome target mark
    else

    let entryAcc = FillAccumulator()
    entryAcc.Init target
    let entryReg = LatchedRegister()
    entryReg.Push(trip.EntryPrice, trip.EntryUs + cfg.LatencyUs)

    let entryBuilder = TimeBarBuilder(1_000_000L)
    // Closed-bar callback: compute next trail price from the bar that just
    // closed and push onto the latency register with activation = bar.EndUs +
    // LatencyUs.
    let onEntryBarClose (bar: SignedBar) =
        let active = entryReg.ActiveAt(bar.EndUs)
        let next = newTrailPrice cfg trip.Side true active bar
        entryReg.Push(next, bar.EndUs + cfg.LatencyUs)

    // Seek the stream to EntryUs.
    stream.SeekTo trip.EntryUs

    // Phase A — entry fill, walk trades in [EntryUs, ExitUs].
    let mutable phaseADone = false
    let mutable carryover : Trade voption = ValueNone
    while not phaseADone do
        match stream.TryNext() with
        | ValueNone -> phaseADone <- true
        | ValueSome t when t.TimestampUs > trip.ExitUs ->
            // Past the entry-phase cap. Stash this trade so Phase C can
            // observe it (otherwise we'd skip a trade just past the boundary).
            carryover <- ValueSome t
            phaseADone <- true
        | ValueSome t ->
            // Push the trade through the bar builder FIRST so the closed-bar
            // callback updates the trail price BEFORE we evaluate the fill.
            entryBuilder.Process(onEntryBarClose, t)
            let active = entryReg.ActiveAt(t.TimestampUs)
            let crosses =
                if Double.IsNaN active then false
                else
                    match trip.Side with
                    | Long  -> t.Price <= active
                    | Short -> t.Price >= active
                    | Flat  -> false
            if crosses then
                let fullyFilled = tryFill entryAcc rng cfg active t
                if fullyFilled then phaseADone <- true

    // If nothing filled, emit empty outcome.
    if entryAcc.Filled <= 0.0 then
        let mark = if Double.IsNaN stream.LastPrice then trip.EntryPrice else stream.LastPrice
        emptyOutcome target mark
    else

    // Phase B — passive hold from entryAcc.LastFillUs through trip.ExitUs.
    // The carryover trade (if any) is already past ExitUs and is the first
    // candidate for Phase C. If no carryover, seek the stream to ExitUs.
    if carryover.IsNone then
        stream.SeekTo trip.ExitUs

    // Phase C — exit fill, target qty = entryAcc.Filled (whatever we got).
    let exitAcc = FillAccumulator()
    exitAcc.Init entryAcc.Filled
    let exitReg = LatchedRegister()
    exitReg.Push(trip.ExitPrice, trip.ExitUs + cfg.LatencyUs)

    let exitBuilder = TimeBarBuilder(1_000_000L)
    let onExitBarClose (bar: SignedBar) =
        let active = exitReg.ActiveAt(bar.EndUs)
        let next = newTrailPrice cfg trip.Side false active bar
        exitReg.Push(next, bar.EndUs + cfg.LatencyUs)

    // Helper to evaluate one trade against the exit accumulator.
    let evalExitTrade (t: Trade) : bool =
        exitBuilder.Process(onExitBarClose, t)
        let active = exitReg.ActiveAt(t.TimestampUs)
        // Exit side is OPPOSITE of position direction:
        //   Long position → sell-limit → trade.Price >= active fills us
        //   Short position → buy-limit → trade.Price <= active fills us
        let crosses =
            if Double.IsNaN active then false
            else
                match trip.Side with
                | Long  -> t.Price >= active
                | Short -> t.Price <= active
                | Flat  -> false
        if crosses then tryFill exitAcc rng cfg active t else false

    let mutable phaseCDone = false
    // First, consume the carryover (if any) — it's the first trade past ExitUs.
    match carryover with
    | ValueSome t -> if evalExitTrade t then phaseCDone <- true
    | ValueNone -> ()

    while not phaseCDone do
        match stream.TryNext() with
        | ValueNone -> phaseCDone <- true
        | ValueSome t -> if evalExitTrade t then phaseCDone <- true

    // -------------------------------------------------------------------------
    // P&L reduction
    // -------------------------------------------------------------------------
    let entryAvg = entryAcc.AvgFillPrice
    let exitAvg = exitAcc.AvgFillPrice
    let entryFilled = entryAcc.Filled
    let exitFilled = exitAcc.Filled
    let residualQty = entryFilled - exitFilled
    let residualMark =
        if Double.IsNaN stream.LastPrice then trip.EntryPrice else stream.LastPrice

    let signMult =
        match trip.Side with
        | Long  ->  1.0
        | Short -> -1.0
        | Flat  ->  0.0

    // Round-tripped portion: signed (exit - entry) on the exited qty.
    let realizedGross =
        if exitFilled > 0.0 then signMult * (exitAvg - entryAvg) * exitFilled
        else 0.0

    // Residual portion: mark to last trade. (For shorts, residualMark < entryAvg
    // is favorable and signMult makes it positive.)
    let residualGross =
        if residualQty > 0.0 then signMult * (residualMark - entryAvg) * residualQty
        else 0.0

    // Maker fees: fraction of fill notional on each side. When the relevant
    // fill qty is zero, the avg price is NaN — guard so the fee is exactly 0
    // rather than NaN propagating through the P&L.
    let entryFeeTotal =
        if entryFilled > 0.0 then cfg.MakerFee * entryAvg * entryFilled else 0.0
    let exitFeeTotal =
        if exitFilled > 0.0 then cfg.MakerFee * exitAvg * exitFilled else 0.0
    // Allocate entry fee between the round-tripped portion and the residual
    // portion in proportion to qty.
    let entryFeeOnRound = if entryFilled > 0.0 then entryFeeTotal * (exitFilled / entryFilled) else 0.0
    let entryFeeOnResid = entryFeeTotal - entryFeeOnRound

    let limitRealizedPnL = realizedGross - entryFeeOnRound - exitFeeTotal
    let limitResidualPnL = residualGross - entryFeeOnResid

    let secondsHeld =
        let endUs =
            if exitAcc.LastFillUs > 0L then exitAcc.LastFillUs
            else entryAcc.LastFillUs
        if entryAcc.FirstFillUs > 0L then int ((endUs - entryAcc.FirstFillUs) / 1_000_000L)
        else 0

    { TargetQty = target
      EntryFillQty = entryFilled
      EntryFillFraction = if target > 0.0 then entryFilled / target else 0.0
      EntryFirstFillUs = entryAcc.FirstFillUs
      EntryLastFillUs = entryAcc.LastFillUs
      EntryAvgFillPrice = entryAvg
      ExitFillQty = exitFilled
      ExitFirstFillUs = exitAcc.FirstFillUs
      ExitLastFillUs = exitAcc.LastFillUs
      ExitAvgFillPrice = exitAvg
      ResidualQty = residualQty
      ResidualMarkPrice = residualMark
      LimitRealizedPnL = limitRealizedPnL
      LimitResidualPnL = limitResidualPnL
      LimitNetPnL = limitRealizedPnL + limitResidualPnL
      LimitSecondsHeld = secondsHeld }

// -----------------------------------------------------------------------------
// Trip-CSV row schema (subset of donchianTripsHeader needed for replay)
// -----------------------------------------------------------------------------

type TripRow = {
    Symbol: string
    EntryUs: int64
    ExitUs: int64
    Side: Side
    EntryPrice: float
    ExitPrice: float
    EffectiveNotional: float
    /// All the original CSV columns kept as a single passthrough string so we
    /// can write the output CSV with the original schema preserved + appended.
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

/// Read the donchian trips CSV. The schema has these columns (header row):
///   symbol,timeframe,donchian_bars,min_trend_bars,allow_short,allow_long,
///   entry_us,exit_us,side,entry_price,exit_price,net_pnl,fees,bars_held,
///   mfe,mae,effective_notional,funding_pnl,adv_at_entry,
///   bars_since_up_violation,bars_since_down_violation,
///   pct_1h_change,pct_72h_change,price_ratio_72h_over_1h,vol_ratio_1h_over_72h,
///   dollar_volume_1h_at_entry,trade_count_1h_at_entry
let loadTrips (path: string) : TripRow[] * string =
    use sr = new StreamReader(path)
    let header = sr.ReadLine()
    let cols = header.Split(',')
    let idx (name: string) =
        let i = Array.IndexOf(cols, name)
        if i < 0 then failwithf "loadTrips: column %s missing in %s" name path
        i
    let iSym = idx "symbol"
    let iEntryUs = idx "entry_us"
    let iExitUs = idx "exit_us"
    let iSide = idx "side"
    let iEntryPx = idx "entry_price"
    let iExitPx = idx "exit_price"
    let iEffNot = idx "effective_notional"
    let result = ResizeArray<TripRow>(capacity = 100_000)
    let mutable line = sr.ReadLine()
    while not (isNull line) do
        let parts = line.Split(',')
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
        line <- sr.ReadLine()
    result.ToArray(), header

// -----------------------------------------------------------------------------
// Output writer
// -----------------------------------------------------------------------------

let outcomeColumns =
    "target_qty,entry_fill_qty,entry_fill_fraction,entry_first_fill_us,entry_last_fill_us,entry_avg_fill_price,exit_fill_qty,exit_first_fill_us,exit_last_fill_us,exit_avg_fill_price,residual_qty,residual_mark_price,limit_realized_pnl,limit_residual_pnl,limit_net_pnl,limit_seconds_held"

let outcomeRow (o: LimitFillOutcome) : string =
    String.concat "," [
        fmtFloat o.TargetQty
        fmtFloat o.EntryFillQty
        fmtFloat o.EntryFillFraction
        string o.EntryFirstFillUs
        string o.EntryLastFillUs
        fmtFloat o.EntryAvgFillPrice
        fmtFloat o.ExitFillQty
        string o.ExitFirstFillUs
        string o.ExitLastFillUs
        fmtFloat o.ExitAvgFillPrice
        fmtFloat o.ResidualQty
        fmtFloat o.ResidualMarkPrice
        fmtFloat o.LimitRealizedPnL
        fmtFloat o.LimitResidualPnL
        fmtFloat o.LimitNetPnL
        string o.LimitSecondsHeld
    ]

// -----------------------------------------------------------------------------
// Streaming runner: group by (symbol, date), load each parquet once,
// process all trips for that day in chronological order.
// -----------------------------------------------------------------------------

/// Date of an EntryUs (UTC).
let private dateOfUs (us: int64) : DateTime =
    DateTimeOffset.FromUnixTimeMilliseconds(us / 1000L).UtcDateTime.Date

/// Run the simulator over every trip in `tripsCsvPath`, writing outcome rows
/// to `outputCsvPath`. Trips are grouped by (symbol, date_of_entry_us); each
/// group loads at most 2 days' parquets (the entry-day plus the next day to
/// cover the exit-side trail extending past midnight). Parallel.ForEach over
/// (symbol, date) groups; per-trip simulation is sequential within a group.
let runFillSim
    (cfg: LimitFillConfig)
    (dataRoot: string)
    (tripsCsvPath: string)
    (outputCsvPath: string)
    (parallelism: int)
    : unit =
    let trips, header = loadTrips tripsCsvPath
    printfn "[limit-fill-sim] loaded %d trips from %s" trips.Length tripsCsvPath

    // Group by (symbol, date_of_entry_us). The same (symbol, date) group is
    // sorted by EntryUs so the trades-pointer advances monotonically through
    // the day's parquet.
    let groups =
        trips
        |> Array.mapi (fun i t -> (i, t))
        |> Array.groupBy (fun (_, t) -> (t.Symbol, dateOfUs t.EntryUs))
        |> Array.map (fun ((sym, d), pairs) ->
            let sorted = pairs |> Array.sortBy (fun (_, t) -> t.EntryUs)
            (sym, d, sorted))

    Directory.CreateDirectory(Path.GetDirectoryName outputCsvPath) |> ignore
    if File.Exists outputCsvPath then File.Delete outputCsvPath
    use writer = new StreamWriter(outputCsvPath)
    writer.WriteLine(header + "," + outcomeColumns)
    let writeLock = obj()
    let mutable processed = 0
    let total = trips.Length

    let opts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = parallelism)
    System.Threading.Tasks.Parallel.ForEach(
        groups,
        opts,
        fun ((symbol: string), (date: DateTime), (group: (int * TripRow)[])) ->
            try
                // Per-group day cache: Dictionary<dayOffset, Trade[]>. Each
                // trip's TradeStream pulls days through this cache so multiple
                // trips in the same group don't reload day 0 from parquet.
                // Pruned aggressively after each trip — when the next trip's
                // EntryUs is in day-offset N+, evict all entries < N+.
                let dayCache = Dictionary<int, Trade[]>()
                let getDay (dayOffset: int) : Trade[] =
                    match dayCache.TryGetValue dayOffset with
                    | true, arr -> arr
                    | false, _ ->
                        let arr = TradeLoader.loadDay dataRoot symbol (date.AddDays(float dayOffset))
                        dayCache.[dayOffset] <- arr
                        arr
                // The maxDays passed to TradeStream caps how far it'll try to
                // pull past the entry day. Use 1 + LookaheadDays as the upper
                // bound; the stream will still terminate naturally when an
                // empty Trade[] is returned (a missing day-parquet beyond
                // the symbol's listing/delisting window).
                let maxDays = 1 + cfg.LookaheadDays

                let lines = ResizeArray<int * string>(group.Length)
                for (origIdx, trip) in group do
                    // Compute this trip's "anchor" day-offset: which day of the
                    // group's date axis does the trip's entry fall on? For a
                    // trip whose entry_us is on the same UTC date as the group
                    // anchor, anchor=0. The stream's loadDay callback is then
                    // (\i -> getDay (anchor + i)) so the stream's relative
                    // day-indexing maps to the absolute group-day axis.
                    let dateUs = DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000L
                    let microsPerDay = 86_400_000_000L
                    let anchor = int ((trip.EntryUs - dateUs) / microsPerDay)
                    let stream = TradeStream((fun i -> getDay (anchor + i)), maxDays)

                    let donTrip : DonchianRoundTrip = {
                        EntryUs = trip.EntryUs
                        ExitUs = trip.ExitUs
                        Side = trip.Side
                        EntryPrice = trip.EntryPrice
                        ExitPrice = trip.ExitPrice
                        NetPnL = 0.0
                        Fees = 0.0
                        BarsHeld = 0
                        MaxFavorableExcursion = 0.0
                        MaxAdverseExcursion = 0.0
                        EffectiveNotional = trip.EffectiveNotional
                        FundingPnL = 0.0
                        AvgDailyVolumeAtEntry = 0.0
                        BarsSinceUpViolationAtEntry = 0
                        BarsSinceDownViolationAtEntry = 0
                        Pct1hChangeAtEntry = 0.0
                        Pct72hChangeAtEntry = 0.0
                        PriceRatio72hOver1hAtEntry = 0.0
                        VolRatio1hOver72hAtEntry = 0.0
                        DollarVolume1hAtEntry = 0.0
                        TradeCount1hAtEntry = 0.0
                        ExitReason = Normal
                    }
                    let outcome = simulateTrip cfg donTrip stream
                    lines.Add(origIdx, trip.OriginalLine + "," + outcomeRow outcome)

                    // Prune dayCache: drop entries with offset < anchor (we
                    // know subsequent trips have entry_us >= this trip's, so
                    // no future trip needs days before its own anchor).
                    let toRemove =
                        dayCache.Keys
                        |> Seq.filter (fun k -> k < anchor)
                        |> Seq.toArray
                    for k in toRemove do dayCache.Remove(k) |> ignore
                lock writeLock (fun () ->
                    for (_, line) in lines do
                        writer.WriteLine line
                    processed <- processed + group.Length
                    if processed % 1000 = 0 || processed = total then
                        printfn "[limit-fill-sim] %d/%d trips" processed total)
            with ex ->
                lock writeLock (fun () ->
                    eprintfn "[limit-fill-sim] %s %s FAILED: %s"
                        symbol (date.ToString "yyyy-MM-dd") ex.Message
                    eprintfn "%s" ex.StackTrace))
    |> ignore

    writer.Flush()
    printfn "[limit-fill-sim] wrote %d trip rows -> %s" processed outputCsvPath
