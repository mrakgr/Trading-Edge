module TradingEdge.CryptoBacktest.Breakout

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// VWAP-breakout strategy (research baseline)
// =============================================================================
//
// Symmetric breakout/breakdown on the per-bar VWAP series (price chosen
// over close because intra-bar VWAP is more stable on thin bars):
//
//     prevMaxVwap_t  =  max over the previous N bars (excluding bar t)
//     prevMinVwap_t  =  min over the previous N bars (excluding bar t)
//
//   bar.VWAP > prevMaxVwap  →  Long  (new N-hour high)
//   bar.VWAP < prevMinVwap  →  Short (new N-hour low)  [if AllowShort]
//   neither                 →  hold previous target  (this is the key
//                              difference from a level-driven signal: a
//                              breakout fires ONCE, then the target persists
//                              until the opposite breakout fires).
//
// The "consume the signal at fire time" rule from OrderflowMA still applies:
// we act only when `target ≠ lastTarget`. A breakout that gets gated does
// not retry — the next break must come from the OPPOSITE side.
//
// Reuses StrategyConfig / Side / RoundTrip from OrderflowMA so the rest of
// the pipeline (Cell wrapper, metrics, breakdown report) is unchanged.
// MaWindowHours plays the role of the breakout-lookback in hours.

// =============================================================================
// Streaming engine — bar-only path
// =============================================================================
//
// Rolling max / min over VWAP use the shared MaxMa / MinMa primitives
// (RollingMa.fs). The engine reads .State BEFORE pushing the bar's VWAP
// so the comparison is against the previous N bars exclusive. The window
// is "primed" once .Count >= WindowSize — same threshold as OrderflowMA's
// `barsSeen >= n`.

type Engine(cfg: StrategyConfig) =
    let mutable prevClose = 0.0
    let mutable barsSeen = 0

    let hoursToBars (hours: int) : int =
        if cfg.BucketUs > 0L then
            max 1 (int (int64 hours * 3_600_000_000L / cfg.BucketUs))
        else
            max 1 hours

    let daysToBars (days: int) : int =
        hoursToBars (24 * days)

    // Breakout-lookback window.
    let n = hoursToBars (max 1 cfg.MaWindowHours)
    // Track the rolling max / min of bar VWAPs OVER THE PREVIOUS n BARS.
    // We push the bar's VWAP only AFTER comparing it to the current
    // extreme — that way "bar.VWAP > rollMax.State" is comparing the
    // current bar's VWAP to the maximum of the previous n bars (exclusive).
    let rollMax = MaxMa(n)
    let rollMin = MinMa(n)

    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)

    let mutable side = Flat
    let mutable lastTarget = Flat
    // Signal lockout after a gap fires — see OrderflowMA for rationale.
    // Especially important for breakouts: the rolling max/min over VWAP
    // would otherwise hold pre-gap extremes that no post-gap bar can
    // reach, locking in a stale price-level reference.
    let mutable signalLockoutUntil = 0
    let mutable entryPrice = 0.0
    let mutable entryUs = 0L
    let mutable effectiveNotional = 0.0
    let mutable barsHeld = 0
    let mutable mfeUsd = 0.0
    let mutable maeUsd = 0.0
    let mutable ratioAtEntry = 0.0
    let mutable fundingPnl = 0.0
    let mutable advAtEntry = 0.0

    let mutable lastClose = 0.0
    let mutable lastUs = 0L

    let mutable fundingEvents : (int64 * float)[] = [||]
    let mutable fundingPtr = 0

    let trips = ResizeArray<RoundTrip>()

    let feeFor (notional: float) : float = notional * cfg.TakerFee

    let grossPnL (s: Side) (entry: float) (exit: float) (notional: float) : float =
        let qty = notional / entry
        match s with
        | Long  -> (exit - entry) * qty
        | Short -> (entry - exit) * qty
        | Flat  -> 0.0

    let computeEffectiveNotional () : float =
        if cfg.ReferenceVol <= 0.0 then cfg.Notional
        else
            let vol = volMa.SampleStd
            if vol <= 0.0 then cfg.Notional
            else min cfg.Notional (cfg.Notional * cfg.ReferenceVol / vol)

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
        let fees = 2.0 * feeFor effectiveNotional
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

    let updateExcursion (bar: SignedBar) =
        if side = Flat then ()
        else
            let qty = effectiveNotional / entryPrice
            let favorPrice, adversePrice =
                match side with
                | Long  -> bar.High, bar.Low
                | Short -> bar.Low,  bar.High
                | Flat  -> 0.0, 0.0
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

    let entryAllowed (bar: SignedBar) =
        let dvThisBar = bar.BuyDollarVolume + bar.SellDollarVolume
        let dailyOk =
            if cfg.MinDailyQuoteVolume <= 0.0 || cfg.BucketUs <= 0L then true
            else
                let barsPerDay = 86_400_000_000.0 / float cfg.BucketUs
                dvThisBar * barsPerDay >= cfg.MinDailyQuoteVolume
        let barOk =
            cfg.MinBarQuoteVolume <= 0.0 || dvThisBar >= cfg.MinBarQuoteVolume
        dailyOk && barOk

    let stopPriceIfHit (bar: SignedBar) : float option =
        if cfg.MaxAdverseFraction <= 0.0 || side = Flat then None
        else
            let qty = effectiveNotional / entryPrice
            let lossLimit = cfg.MaxAdverseFraction * effectiveNotional
            match side with
            | Long ->
                let stopPx = entryPrice - lossLimit / qty
                if bar.Low <= stopPx then Some stopPx else None
            | Short ->
                let stopPx = entryPrice + lossLimit / qty
                if bar.High >= stopPx then Some stopPx else None
            | Flat -> None

    // See OrderflowMA.priceGapHit. Identical semantics here — fires only
    // while a position is open; caller force-closes at lastClose / lastUs
    // and drops the contaminated bar.
    let priceGapHit (bar: SignedBar) : bool =
        if cfg.MaxBarPriceRatio <= 0.0 || side = Flat then false
        elif prevClose <= 0.0 || bar.Close <= 0.0 then false
        else
            let upGap   = bar.Close / prevClose
            let downGap = prevClose / bar.Close
            upGap > cfg.MaxBarPriceRatio || downGap > cfg.MaxBarPriceRatio

    member _.BarsSeen = barsSeen
    member _.Trips = trips :> seq<RoundTrip>

    member _.SetFundingEvents(events: (int64 * float)[]) =
        fundingEvents <- events
        fundingPtr <- 0

    member _.ProcessBar(bar: SignedBar) =
        applyFunding bar

        let gapFired = priceGapHit bar
        if gapFired then
            closePos lastClose lastUs
            signalLockoutUntil <- barsSeen + n
            lastTarget <- Flat
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        match stopPriceIfHit bar with
        | Some stopPx -> closePos stopPx bar.EndUs
        | None -> ()

        // ---- Signal evaluation BEFORE pushing this bar's VWAP into the
        // rolling extremes. That way rollMax.State and rollMin.State are
        // the strict max/min over the PREVIOUS n bars (exclusive of the
        // current bar) — so "bar.VWAP > prevMax" is a real new high.
        // Skip bars with no VWAP (zero-volume / synthetic), they can't
        // produce a meaningful signal.
        if rollMax.Count >= n && bar.VWAP > 0.0 && barsSeen >= signalLockoutUntil then
            let prevMax = rollMax.State
            let prevMin = rollMin.State
            // ratio = bar.VWAP / midpoint. Stamps the trip with a measure of
            // "how big a break was this" — comparable to OrderflowMA's
            // RatioAtEntry semantics: > 1 ↔ bull, < 1 ↔ bear, magnitude
            // tells you how far past the level it cracked.
            let ratio =
                let mid = 0.5 * (prevMax + prevMin)
                if mid > 0.0 then bar.VWAP / mid else 0.0
            let bullish = bar.VWAP > prevMax
            let bearish = bar.VWAP < prevMin
            let target =
                if bullish then Long
                elif bearish && cfg.AllowShort then Short
                else lastTarget   // breakout signal is event-based: hold
                                  // the prior regime when no break fires.
            if target <> lastTarget then
                if side <> Flat then closePos bar.Close bar.EndUs
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

        // Push VWAP into the rolling extremes AFTER signal evaluation. We
        // skip zero-VWAP bars (no trades) so they don't pollute the max/min
        // with a stale 0.0.
        if bar.VWAP > 0.0 then
            rollMax.Push bar.VWAP
            rollMin.Push bar.VWAP

        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close

        advMa.Push (bar.BuyDollarVolume + bar.SellDollarVolume)

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: StrategyConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
