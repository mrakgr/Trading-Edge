module TradingEdge.CryptoBacktest.VwmaCross

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// VWMA-crossover strategy (research baseline, NOT production)
// =============================================================================
//
// One-off research duplicate of OrderflowMA, swapping the signal for a
// volume-weighted moving average crossover. We keep this in its own file
// (rather than as a flag inside OrderflowMA) so that it can be deleted
// without disturbing the production engine.
//
// Signal:
//     vwma_t  = Σ_{i in last N bars} close_i · volume_i  /  Σ volume_i
//   close_t > vwma_t  →  long
//   close_t < vwma_t  →  flat (default) or short (configurable)
//
// All gating (per-bar liquidity floor, side-specific trailing-90d ADV,
// vol-based position sizing, stop-loss, funding accounting) and the
// regime-change "consume the signal" rule are identical to OrderflowMA so
// that VWMA-vs-orderflow is an apples-to-apples comparison — only the
// signal is swapped.
//
// We reuse OrderflowMA's StrategyConfig, Side, and RoundTrip types verbatim
// so Reporting / Backtest don't need to know which engine produced the
// trips. RatioAtEntry on the trip carries (close / vwma) at entry — same
// "above/below 1.0" semantics as the orderflow buy/sell ratio, which makes
// the breakdown's ratio-at-entry distribution comparable between the two.

/// Rolling VWMA over a fixed-length window of (close, volume) bars. State
/// holds (Σ close·volume, Σ volume) as a struct tuple to avoid allocation
/// per Push. Vwma reads as Σpv/Σv when Σv > 0.
[<Sealed>]
type VwmaMa(windowSize) =
    inherit RollingMa<struct (float * float), struct (float * float)>(struct (0.0, 0.0), windowSize)
    override _.Add    (struct (p, v), struct (spv, sv)) = struct (spv + p * v, sv + v)
    override _.Remove (struct (p, v), struct (spv, sv)) = struct (spv - p * v, sv - v)
    member this.Vwma =
        let struct (sumPV, sumV) = this.State
        if sumV > 0.0 then sumPV / sumV else 0.0

// =============================================================================
// Streaming engine — bar-only path
// =============================================================================

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

    // VWMA window — matches OrderflowMA's MaWindowHours so the same parameter
    // means the same wall-clock span at every timeframe.
    let n = hoursToBars (max 1 cfg.MaWindowHours)
    let vwmaMa = VwmaMa(n)

    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)

    let mutable side = Flat
    let mutable lastTarget = Flat
    // Signal lockout after a gap fires — see OrderflowMA for rationale.
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
            PriceRiseAtEntry = 0.0
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

        // Roll the VWMA window. We feed (close, volume) so the rolling
        // weighted mean is anchored to base-asset volume — same denominator
        // a real VWMA uses. Per-bar quote volume (BuyDollarVolume + Sell)
        // would also work and mostly tracks Volume·Close, but Volume is the
        // textbook choice.
        vwmaMa.Push (struct (bar.Close, bar.Volume))

        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close

        advMa.Push (bar.BuyDollarVolume + bar.SellDollarVolume)

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        if barsSeen >= n && barsSeen >= signalLockoutUntil then
            let vwma = vwmaMa.Vwma
            // Ratio = close / vwma. This makes the fired-trip's RatioAtEntry
            // directly comparable to OrderflowMA's buy/sell ratio: > 1 means
            // the bullish target fired, < 1 means bearish, regardless of
            // engine.
            let ratio = if vwma > 0.0 then bar.Close / vwma else 0.0
            let bullish = ratio > 1.0 && vwma > 0.0
            let bearish = ratio < 1.0 && vwma > 0.0
            let target =
                if bullish then Long
                elif bearish && cfg.AllowShort then Short
                else Flat
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

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: StrategyConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
