module TradingEdge.CryptoBacktest.OrderflowMA

open System.Collections.Generic
open TradingEdge.CryptoBacktest.SignedBar

// =============================================================================
// Orderflow-MA strategy: rolling-sum ratio of buy / sell taker dollar volume
// =============================================================================
//
// Two independent rolling sums of length N over the bar stream:
//     sumBuy_N  = Σ_{i in last N bars} BuyDollarVolume_i
//     sumSell_N = Σ_{i in last N bars} SellDollarVolume_i
//     ratio_t   = sumBuy_N / sumSell_N
//
// We use two separate sums (rather than averaging the per-bar ratio across
// N bars) because per-bar ratios on thin bars are unstable — a bar with
// $10 of buy volume and $1 of sell volume gives ratio = 10, drowning out
// 100× larger bars. Summing first preserves volume-weighting by construction.
//
// Signal:
//   ratio > 1  →  long
//   ratio < 1  →  flat (default) or short (configurable)
//
// Fill model: decide at bar i close, fill at bar i's close price. The bar's
// trades have already happened in the past (we observed them to compute the
// ratio), so trading at the close price models a market order placed at the
// instant the bar closes. On hourly+ timeframes the difference between this
// bar's close and the next bar's open is noise; we use bar.Close so we can
// run on pre-aggregated bar parquets without needing the trade tape.

[<Struct>]
type Side = Flat | Long | Short

type StrategyConfig = {
    /// Rolling-sum signal window length, in HOURS. Independent of the
    /// bar timeframe — the engine converts to bars using BucketUs. A
    /// 240h window is the same wall-clock span at every timeframe
    /// (240 bars at 1h, 60 bars at 4h, etc.), which makes parameter
    /// values directly comparable across timeframes.
    MaWindowHours: int
    /// Notional per trade, in quote currency (USDT). Default $1000.
    Notional: float
    /// Per-fill taker fee fraction. 4 bps default = 0.0004.
    TakerFee: float
    /// If true, take a short position when ratio < 1; otherwise stay flat
    /// on the bear signal.
    AllowShort: bool
    /// Liquidity gate (entries only): the bar at signal time must have
    /// quote volume × bars-per-day ≥ this threshold (USDT). Models the
    /// requirement that current liquidity support a market-taker fill.
    /// Existing positions are NOT subject to this gate — exits always fire.
    /// Set to 0 to disable.
    MinDailyQuoteVolume: float
    /// Microseconds per bar at this strategy's timeframe — needed to
    /// extrapolate per-bar volume to a daily figure for the gate. The
    /// engine receives a per-bar volume of (buyDV + sellDV); to compare
    /// across timeframes we scale by (1 day / bucketUs).
    BucketUs: int64
    /// Stop-loss as fraction of notional (e.g. 0.10 = stop out when
    /// unrealized P&L hits -10% of notional). Fires when the bar's
    /// adverse extreme (Low for longs, High for shorts) breaches the
    /// implied stop price. Exit fills at the stop price exactly — this
    /// is optimistic on gap-throughs but matches a stop-limit order's
    /// best case. Set to 0 to disable.
    MaxAdverseFraction: float
    /// Reference per-bar log-return std (e.g. 0.01 = 1%/bar). When
    /// non-zero, position size at entry scales as
    ///     effectiveNotional = min(notional, notional * referenceVol / barVol)
    /// where barVol is the realized rolling-window log-return std at
    /// signal time. High-vol entries get downsized; low-vol entries are
    /// capped at full notional. Same formula as TradingEdge.Orb.
    /// Set to 0 to disable (everything sizes at full notional).
    ReferenceVol: float
    /// Minimum trailing-90d ADV (USDT/day) required for a long entry.
    /// Evaluated at signal-fire time using the engine's rolling 90-day
    /// quote-volume window. Below threshold → entry is suppressed AND
    /// the signal is consumed (no retry on subsequent bars while the
    /// signal persists). Set to 0 to disable.
    MinLongAdv: float
    /// Minimum trailing-90d ADV (USDT/day) required for a short entry.
    /// Same semantics as MinLongAdv. Set to 0 to disable.
    MinShortAdv: float
    /// Vol-window length in DAYS for the rolling log-return std used by
    /// vol-based position sizing. Decoupled from MaLength because the
    /// signal window (days) and risk-measurement window are conceptually
    /// independent. The engine converts days → bars using BucketUs.
    /// Default 90 days (matches the ADV window). Set lower (e.g. 7) for
    /// a more responsive vol estimate that tracks recent regime shifts.
    VolWindowDays: int
}

let defaultConfig (maWindowHours: int) : StrategyConfig =
    { MaWindowHours = maWindowHours
      Notional = 1000.0
      TakerFee = 0.0004
      AllowShort = false
      MinDailyQuoteVolume = 0.0
      BucketUs = 0L
      MaxAdverseFraction = 0.0
      ReferenceVol = 0.0
      MinLongAdv = 0.0
      MinShortAdv = 0.0
      VolWindowDays = 90 }

type RoundTrip = {
    EntryUs: int64
    ExitUs: int64
    Side: Side
    EntryPrice: float
    ExitPrice: float
    /// Net P&L after entry + exit fees, in quote currency.
    NetPnL: float
    /// Total fees paid on this round-trip (entry + exit), in quote currency.
    Fees: float
    /// Number of bars the position was held (entry bar through exit bar
    /// inclusive). 1 means entered and exited on consecutive bars.
    BarsHeld: int
    /// Maximum favorable excursion: the most-positive unrealized P&L
    /// during the trade, in quote currency. For longs computed against
    /// each bar's High; for shorts against each bar's Low. Excludes fees.
    /// 0 if the position was always offside.
    MaxFavorableExcursion: float
    /// Maximum adverse excursion: the most-negative unrealized P&L during
    /// the trade, in quote currency (so a value of -50 means we were down
    /// $50 at the worst point). For longs computed against each bar's Low;
    /// for shorts against each bar's High. Excludes fees. 0 if the
    /// position was always green.
    /// **The squeeze metric for shorts.**
    MaxAdverseExcursion: float
    /// The orderflow ratio (sumBuy / sumSell) at the close of the
    /// signal-producing bar — i.e. what triggered the entry.
    RatioAtEntry: float
    /// Effective notional used for this trade, in quote currency. Equals
    /// cfg.Notional when vol-sizing is disabled, or scaled down when the
    /// realized vol at entry exceeds cfg.ReferenceVol.
    EffectiveNotional: float
    /// Accumulated funding payments over the trade. For longs this is
    /// `-Σ(rate × notional)` (longs PAY positive funding), for shorts
    /// it's `+Σ(rate × notional)` (shorts RECEIVE positive funding).
    /// Zero if no funding events fired during the holding window or if
    /// funding data wasn't loaded for this run.
    FundingPnL: float
    /// Average daily quote volume over the trailing ~90 days at entry,
    /// in USDT/day. Computed from the bar series leading up to (but not
    /// including) the entry bar's close — leak-free. Used by the breakdown
    /// to bin trades into liquidity deciles WITHOUT contamination from
    /// the symbol's future. Falls back to whatever partial-window data
    /// is available during the first 90 days of a symbol's history.
    AvgDailyVolumeAtEntry: float
}

let private feeFor (notional: float) (takerFee: float) : float = notional * takerFee

let private grossPnL (side: Side) (entry: float) (exit: float) (notional: float) : float =
    let qty = notional / entry
    match side with
    | Long  -> (exit - entry) * qty
    | Short -> (entry - exit) * qty
    | Flat  -> 0.0

// =============================================================================
// Rolling-window aggregates
// =============================================================================
//
// One queue + one running aggregate. Subclasses describe the aggregate as
// pure (input, oldState) -> newState functions; the base owns the queue
// and the eviction. State is read via the .State member; subclasses can
// add derived readers (e.g. StdMa.SampleStd) when the raw state needs a
// non-trivial transform.

[<AbstractClass>]
type RollingMa<'Bar, 'State>(initState: 'State, windowSize: int) =
    let q = Queue<'Bar>(windowSize)
    let mutable state = initState
    abstract member Add    : 'Bar * 'State -> 'State
    abstract member Remove : 'Bar * 'State -> 'State
    member _.Count = q.Count
    member _.WindowSize = windowSize
    member _.State = state
    member this.Push (x: 'Bar) =
        if q.Count = windowSize then
            state <- this.Remove (q.Dequeue(), state)
        q.Enqueue x
        state <- this.Add (x, state)

/// Rolling sum over a fixed-length window of floats. State IS the sum.
[<Sealed>]
type SumMa(windowSize) =
    inherit RollingMa<float, float>(0.0, windowSize)
    override _.Add    (v, s) = s + v
    override _.Remove (v, s) = s - v

/// Rolling sample-std over a fixed-length window of floats. State holds
/// (ΣX, ΣX²) as a struct tuple to avoid heap allocation per Push. The
/// SampleStd reader collapses that into the sample standard deviation
/// using Welford's algebraic identity:
///     var = (ΣX² − m·mean²) / (m − 1)
[<Sealed>]
type StdMa(windowSize) =
    inherit RollingMa<float, struct (float * float)>(struct (0.0, 0.0), windowSize)
    override _.Add    (v, struct (sx, sx2)) = struct (sx + v, sx2 + v * v)
    override _.Remove (v, struct (sx, sx2)) = struct (sx - v, sx2 - v * v)
    member this.SampleStd =
        let m = this.Count
        if m < 2 then 0.0
        else
            let struct (sumX, sumX2) = this.State
            let mean = sumX / float m
            let v = (sumX2 - float m * mean * mean) / float (m - 1)
            if v <= 0.0 then 0.0 else sqrt v

// =============================================================================
// Streaming engine — bar-only path
// =============================================================================
//
// State machine, on each ProcessBar(bar):
//   1. Slide the rolling window forward by one bar (add this bar, drop the
//      oldest if window is full).
//   2. If the window is full, compute the ratio and target side. If the
//      target differs from the current side, execute the change at bar.Close.
//
// At end of stream, Flush() force-closes any open position at the last
// observed close price.

type Engine(cfg: StrategyConfig) =
    let mutable prevClose = 0.0
    let mutable barsSeen = 0

    // Hours → bars conversion. Falls back to 1-bar-per-hour when BucketUs
    // isn't set (unusual but not fatal).
    let hoursToBars (hours: int) : int =
        if cfg.BucketUs > 0L then
            max 1 (int (int64 hours * 3_600_000_000L / cfg.BucketUs))
        else
            max 1 hours

    // Days → bars conversion (used for vol/ADV windows).
    let daysToBars (days: int) : int =
        hoursToBars (24 * days)

    // Signal window — converted from cfg.MaWindowHours so the parameter
    // is timeframe-independent (240h = same wall-clock span at 1h or 4h).
    let n = hoursToBars (max 1 cfg.MaWindowHours)
    // Rolling N-bar buy/sell dollar-volume sums. Read via .State; the
    // orderflow ratio is buyMa.State / sellMa.State at signal time.
    let buyMa  = SumMa(n)
    let sellMa = SumMa(n)

    // ADV window — fixed at 90 days. Slow-moving baseline used to gate
    // entries by symbol-level liquidity at fire time.
    let advWindowBars = daysToBars 90

    // Vol window — configurable, decoupled from MaWindowHours. The signal
    // window (hours of orderflow) and the risk-measurement window are
    // conceptually independent: vol wants to track the regime we're
    // about to trade IN, not necessarily the regime that produced the
    // signal. Default 90 days; 7 days has shown to be more responsive
    // when symbols shift regime quickly.
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)

    // Rolling 90-day window of (buy + sell) dollar volume — used to stamp
    // each trade with a leak-free ADV at entry. State is the rolling sum;
    // currentAdv() divides by days to get the per-day average.
    let advMa = SumMa(advWindowBars)

    // Rolling vol-window of bar-to-bar log returns. SampleStd is O(1).
    let volMa = StdMa(volWindowBars)

    let mutable side = Flat
    // Last regime target (Long/Short/Flat) we acted on. Drives the
    // "consume the signal at fire time" rule: the engine reacts only when
    // the orderflow target FLIPS from its prior value, not whenever it
    // happens to disagree with the current position. Without this, an
    // entry that was suppressed by an ADV / liquidity gate would re-fire
    // on every subsequent bar that still has the same target — i.e. the
    // strategy would chase, entering whenever the gate later relaxed
    // even though that bar isn't the actual signal bar.
    let mutable lastTarget = Flat
    let mutable entryPrice = 0.0
    let mutable entryUs = 0L
    let mutable effectiveNotional = 0.0
    // Per-trade tracking: barsHeld counts the entry bar plus every bar the
    // position was alive through. mfeUsd / maeUsd are the running maxima of
    // favorable / adverse unrealized P&L, in quote currency, derived from
    // each bar's High/Low. ratioAtEntry is the orderflow ratio that
    // produced the entry signal. fundingPnl accumulates funding payments
    // received (positive) or paid (negative) over the trade.
    let mutable barsHeld = 0
    let mutable mfeUsd = 0.0
    let mutable maeUsd = 0.0
    let mutable ratioAtEntry = 0.0
    let mutable fundingPnl = 0.0
    let mutable advAtEntry = 0.0

    let mutable lastClose = 0.0
    let mutable lastUs = 0L

    // Funding event feed. Set via SetFundingEvents (typically once when the
    // Cell is initialized), then consumed by ProcessBar. The pointer
    // advances monotonically with bar timestamps. When no funding data is
    // configured, the array stays empty and fundingPtr never advances.
    let mutable fundingEvents : (int64 * float)[] = [||]
    let mutable fundingPtr = 0

    let trips = ResizeArray<RoundTrip>()

    /// Compute the effective notional for an entry given the current vol.
    /// Disabled (returns cfg.Notional) when ReferenceVol = 0 or when we
    /// don't have enough returns to estimate vol yet.
    let computeEffectiveNotional () : float =
        if cfg.ReferenceVol <= 0.0 then cfg.Notional
        else
            let vol = volMa.SampleStd
            if vol <= 0.0 then cfg.Notional
            else min cfg.Notional (cfg.Notional * cfg.ReferenceVol / vol)

    /// Average daily quote volume from the trailing rolling window.
    /// Uses whatever bars are in the buffer so far (partial during warmup);
    /// the caller can treat 0 as "no data yet". Days = bars / barsPerDay.
    let currentAdv () : float =
        let m = advMa.Count
        if m = 0 || cfg.BucketUs <= 0L then 0.0
        else
            let barsPerDay = 86_400_000_000.0 / float cfg.BucketUs
            let days = float m / barsPerDay
            if days > 0.0 then advMa.State / days else 0.0

    let openPos (newSide: Side) (fillPrice: float) (fillUs: int64) (ratio: float) =
        side <- newSide
        entryPrice <- fillPrice
        entryUs <- fillUs
        effectiveNotional <- computeEffectiveNotional ()
        barsHeld <- 1
        mfeUsd <- 0.0
        maeUsd <- 0.0
        ratioAtEntry <- ratio
        fundingPnl <- 0.0
        advAtEntry <- currentAdv ()

    let closePos (fillPrice: float) (fillUs: int64) =
        let gross = grossPnL side entryPrice fillPrice effectiveNotional
        let fees = 2.0 * feeFor effectiveNotional cfg.TakerFee
        trips.Add {
            EntryUs = entryUs
            ExitUs = fillUs
            Side = side
            EntryPrice = entryPrice
            ExitPrice = fillPrice
            NetPnL = gross - fees + fundingPnl
            Fees = fees
            BarsHeld = barsHeld
            MaxFavorableExcursion = mfeUsd
            MaxAdverseExcursion = maeUsd
            RatioAtEntry = ratioAtEntry
            EffectiveNotional = effectiveNotional
            FundingPnL = fundingPnl
            AvgDailyVolumeAtEntry = advAtEntry
        }
        side <- Flat
        entryPrice <- 0.0
        entryUs <- 0L
        effectiveNotional <- 0.0
        barsHeld <- 0
        mfeUsd <- 0.0
        maeUsd <- 0.0
        ratioAtEntry <- 0.0
        fundingPnl <- 0.0
        advAtEntry <- 0.0

    /// Advance the funding pointer through events whose timestamp is at
    /// or before the current bar's end. For each event that fires while a
    /// position is open, accumulate funding P&L. Sign convention:
    ///   positive rate → longs PAY (pnl is negative for longs)
    ///   positive rate → shorts RECEIVE (pnl is positive for shorts)
    /// Events at exactly entryUs are excluded — we entered AT the bar
    /// close after the funding moment passed.
    let applyFunding (bar: SignedBar) =
        while fundingPtr < fundingEvents.Length
              && (fst fundingEvents.[fundingPtr]) <= bar.EndUs do
            let ts, rate = fundingEvents.[fundingPtr]
            if side <> Flat && ts > entryUs then
                let payment = effectiveNotional * rate
                let signed =
                    match side with
                    | Long  -> -payment
                    | Short -> +payment
                    | Flat  -> 0.0
                fundingPnl <- fundingPnl + signed
            fundingPtr <- fundingPtr + 1

    // Update MAE/MFE for the bar just observed, given that the position is
    // still open at this point. For longs, the bar's High is the most-
    // favorable price reached and the Low is the most-adverse. For shorts,
    // it's mirrored. Excludes fees — the running unrealized P&L is gross.
    let updateExcursion (bar: SignedBar) =
        if side = Flat then ()
        else
            let qty = effectiveNotional / entryPrice
            let favorPrice, adversePrice =
                match side with
                | Long  -> bar.High, bar.Low
                | Short -> bar.Low,  bar.High
                | Flat  -> 0.0, 0.0  // unreachable, we checked above
            let favorPnl =
                match side with
                | Long  -> (favorPrice - entryPrice) * qty
                | Short -> (entryPrice - favorPrice) * qty
                | Flat  -> 0.0
            let advPnl =
                match side with
                | Long  -> (adversePrice - entryPrice) * qty
                | Short -> (entryPrice - adversePrice) * qty
                | Flat  -> 0.0
            if favorPnl > mfeUsd then mfeUsd <- favorPnl
            if advPnl < maeUsd then maeUsd <- advPnl

    // Liquidity gate (entries only). If MinDailyQuoteVolume = 0 the gate is
    // disabled. Otherwise compare this bar's implied daily quote volume
    // (= per-bar volume × bars-per-day) against the threshold.
    let entryAllowed (bar: SignedBar) =
        if cfg.MinDailyQuoteVolume <= 0.0 || cfg.BucketUs <= 0L then true
        else
            let dvThisBar = bar.BuyDollarVolume + bar.SellDollarVolume
            let barsPerDay = 86_400_000_000.0 / float cfg.BucketUs
            dvThisBar * barsPerDay >= cfg.MinDailyQuoteVolume

    // Stop-loss check. Returns Some stopPrice if this bar's adverse extreme
    // breached the implied stop level, otherwise None. Exits fill at the stop
    // price exactly — optimistic on gap-throughs, but a useful upper bound on
    // the stop's value before refining.
    let stopPriceIfHit (bar: SignedBar) : float option =
        if cfg.MaxAdverseFraction <= 0.0 || side = Flat then None
        else
            let qty = effectiveNotional / entryPrice
            let lossLimit = cfg.MaxAdverseFraction * effectiveNotional
            // Stop price: the price at which unrealized P&L = -lossLimit.
            // For long: entry - lossLimit/qty. For short: entry + lossLimit/qty.
            match side with
            | Long ->
                let stopPx = entryPrice - lossLimit / qty
                if bar.Low <= stopPx then Some stopPx else None
            | Short ->
                let stopPx = entryPrice + lossLimit / qty
                if bar.High >= stopPx then Some stopPx else None
            | Flat -> None

    member _.BarsSeen = barsSeen
    member _.Trips = trips :> seq<RoundTrip>

    /// Provide funding events for this engine, sorted by timestamp. Caller
    /// is responsible for filtering to the engine's date window. Pass an
    /// empty array (the default) to disable funding accounting.
    member _.SetFundingEvents(events: (int64 * float)[]) =
        fundingEvents <- events
        fundingPtr <- 0

    member _.ProcessBar(bar: SignedBar) =
        // Step 0: apply any funding events that fired up to and including
        // this bar's end. Done before excursion / stop / signal so funding
        // P&L is reflected if a stop fires this bar.
        applyFunding bar

        // Step 1: update excursion tracking on this bar BEFORE any close.
        // The position was alive through this entire bar (we entered at the
        // PREVIOUS bar's close, or earlier), so this bar's High/Low contribute.
        // For the entry bar itself, barsHeld was set to 1 at open and we
        // skip excursion tracking on it (the high/low were already established
        // when we opened, so they don't count).
        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        // Step 2: stop-loss check. If the bar breached the stop, close at
        // the stop price (which is more favorable than bar.Close for trades
        // that kept moving against us within the bar, but identical or worse
        // for the bar that printed a Low ≤ stopPx then bounced). This runs
        // BEFORE signal evaluation so a stopped-out trade goes flat for the
        // remainder of this bar's logic.
        match stopPriceIfHit bar with
        | Some stopPx -> closePos stopPx bar.EndUs
        | None -> ()

        // Roll the orderflow signal MAs forward by one bar.
        buyMa.Push  bar.BuyDollarVolume
        sellMa.Push bar.SellDollarVolume

        // Bar-to-bar log return for the rolling vol MA. Skip the first bar
        // (no previous close yet) and any bar where prices look broken.
        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close

        // Rolling 90-day ADV MA. currentAdv() reads advMa.State / advMa.Count
        // when an entry fires later in this same ProcessBar.
        advMa.Push (bar.BuyDollarVolume + bar.SellDollarVolume)

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        if barsSeen >= n then
            let ratio = if sellMa.State > 0.0 then buyMa.State / sellMa.State else infinity
            let bullish = ratio > 1.0
            let bearish = ratio < 1.0
            let target =
                if bullish then Long
                elif bearish && cfg.AllowShort then Short
                else Flat
            // Fire only on regime CHANGE (target <> lastTarget), not on
            // (target <> side). Once we evaluate this bar's signal, we
            // commit lastTarget to it regardless of whether the entry
            // actually fired — that way a gated entry doesn't retry on
            // the next bar that happens to share the same target. The
            // signal is consumed at fire time.
            if target <> lastTarget then
                // Exits always fire — once we're in a position, we want out
                // regardless of current bar liquidity. (In live trading we'd
                // take whatever fill we can get; this matches that behavior.)
                if side <> Flat then closePos bar.Close bar.EndUs
                // Entries are gated by both per-bar liquidity and by the
                // side-specific trailing-90d ADV thresholds. If either
                // gate fails, we stay flat AND consume the signal — no
                // retry until the orderflow regime flips again.
                if target <> Flat && entryAllowed bar then
                    let adv = currentAdv ()
                    let advGate =
                        match target with
                        | Long  -> cfg.MinLongAdv  <= 0.0 || adv >= cfg.MinLongAdv
                        | Short -> cfg.MinShortAdv <= 0.0 || adv >= cfg.MinShortAdv
                        | Flat  -> true
                    if advGate then
                        openPos target bar.Close bar.EndUs ratio
                lastTarget <- target

    /// Force-close any open position at the last seen bar's close.
    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

/// Convenience runner over a precomputed bar array.
let run (cfg: StrategyConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
