module TradingEdge.CryptoBacktest.OrderflowCumsum

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Orderflow-Cumsum strategy: clamped cumsum of v0's smoothed orderflow sign
// =============================================================================
//
// On each bar, maintain two rolling-sum windows of length MaWindowHours
// (converted to bars via BucketUs) over per-bar BuyDollarVolume and
// SellDollarVolume — exactly v0's signal. Compute the sign of (buyMa - sellMa);
// that's the per-bar vote (+1, -1, or 0).
//
// Maintain a clamped cumsum of those votes in [-threshold, +threshold]. Long
// fires when the cumsum first touches +threshold from inside; short fires when
// it first touches -threshold from inside. Exits happen on the OPPOSITE
// extreme: a long is held until cumsum re-touches -threshold (full reversal),
// and vice versa.
//
// Why vote on the smoothed signal rather than per-bar volume sign: v0's edge
// comes from the 200h-rolling-sum regime, not from instantaneous orderflow.
// Voting on per-bar (buyDV - sellDV) at 1m is dominated by microstructure
// noise — votes flip every few bars and the cumsum hits +threshold quickly on
// noisy bursts. Voting on sign(buyMa_Nh - sellMa_Nh) keeps the input to the
// cumsum stable: it changes only when the underlying smoothed orderflow
// regime flips, so the cumsum becomes a regime-persistence detector layered
// on top of v0's signal.
//
// During the pre-fill period (first MaWindowHours of bars), the rolling sums
// aren't full and the vote is forced to 0; the cumsum stays at 0 and no
// signals fire — same lockout semantics as v0.
//
// All other v0 mechanics (gap detector, stop-loss, vol-sizing, ADV gate,
// funding accounting) are preserved verbatim. Per-bar liquidity gates from
// v0 (MinDailyQuoteVolume / MinBarQuoteVolume) are intentionally dropped —
// the gap detector subsumes the redenomination case, and we keep the
// trailing-90d ADV gate (MinLongAdv / MinShortAdv) for symbol-level
// liquidity filtering.

type CumsumConfig = {
    /// Clamp magnitude. Long fires at +CumsumThreshold, short at -CumsumThreshold.
    CumsumThreshold: int
    /// Rolling-sum window for the inner v0 orderflow signal, in HOURS.
    /// Converted to bars via BucketUs. Sign(buyMa - sellMa) over this window
    /// is the per-bar vote into the clamped cumsum.
    MaWindowHours: int
    Notional: float
    TakerFee: float
    AllowShort: bool
    BucketUs: int64
    MaxAdverseFraction: float
    ReferenceVol: float
    MinLongAdv: float
    MinShortAdv: float
    VolWindowDays: int
    MaxBarPriceRatio: float
}

let defaultCumsumConfig (threshold: int) : CumsumConfig =
    { CumsumThreshold = threshold
      MaWindowHours = 200
      Notional = 1000.0
      TakerFee = 0.0004
      AllowShort = false
      BucketUs = 0L
      MaxAdverseFraction = 0.0
      ReferenceVol = 0.0
      MinLongAdv = 0.0
      MinShortAdv = 0.0
      VolWindowDays = 90
      MaxBarPriceRatio = 0.0 }

let private feeFor (notional: float) (takerFee: float) : float = notional * takerFee

let private grossPnL (side: Side) (entry: float) (exit: float) (notional: float) : float =
    let qty = notional / entry
    match side with
    | Long  -> (exit - entry) * qty
    | Short -> (entry - exit) * qty
    | Flat  -> 0.0

type Engine(cfg: CumsumConfig) =
    let mutable prevClose = 0.0
    let mutable barsSeen = 0

    let hoursToBars (hours: int) : int =
        if cfg.BucketUs > 0L then
            max 1 (int (int64 hours * 3_600_000_000L / cfg.BucketUs))
        else
            max 1 hours

    let daysToBars (days: int) : int = hoursToBars (24 * days)

    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)

    // Inner v0 signal: rolling sums of buy/sell dollar volume over
    // MaWindowHours. The sign of (buyMa.State - sellMa.State) is the per-bar
    // vote into the clamped cumsum. Pre-fill period (first n bars) votes 0.
    let n = hoursToBars (max 1 cfg.MaWindowHours)
    let buyMa = SumMa(n)
    let sellMa = SumMa(n)

    let threshold = max 1 cfg.CumsumThreshold
    let mutable cumsum = 0

    let mutable side = Flat
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

    // Signal-lockout tracking (used only by gap detector). After a gap fires
    // we lock out new signals for one full threshold's worth of bars so the
    // cumsum recovers. Stored as a bar-index threshold.
    let mutable signalLockoutUntil = 0

    let mutable fundingEvents : (int64 * float)[] = [||]
    let mutable fundingPtr = 0

    let trips = ResizeArray<RoundTrip>()

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

    let openPos (newSide: Side) (fillPrice: float) (fillUs: int64) (signedCum: int) =
        side <- newSide
        entryPrice <- fillPrice
        entryUs <- fillUs
        effectiveNotional <- computeEffectiveNotional ()
        barsHeld <- 1
        mfeUsd <- 0.0
        maeUsd <- 0.0
        // Stash the cumsum value at entry into RatioAtEntry so the standard
        // RoundTrip schema can carry it through the reporting/stratify layer.
        ratioAtEntry <- float signedCum
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

    let priceGapHit (bar: SignedBar) : bool =
        if cfg.MaxBarPriceRatio <= 0.0 || side = Flat then false
        elif prevClose <= 0.0 || bar.Close <= 0.0 then false
        else
            let upGap   = bar.Close / prevClose
            let downGap = prevClose / bar.Close
            upGap > cfg.MaxBarPriceRatio || downGap > cfg.MaxBarPriceRatio

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

    member _.BarsSeen = barsSeen
    member _.Trips = trips :> seq<RoundTrip>
    member _.Cumsum = cumsum
    member _.InTrade : bool = side <> Flat

    member _.SetFundingEvents(events: (int64 * float)[]) =
        fundingEvents <- events
        fundingPtr <- 0

    member _.ProcessBar(bar: SignedBar) =
        applyFunding bar

        // Gap detector — same semantics as v0. On gap, force-close at prevClose,
        // then reset the cumsum and lock out signals for one threshold-worth of
        // bars so the post-gap regime gets a fresh accumulator.
        let gapFired = priceGapHit bar
        if gapFired then
            closePos lastClose lastUs
            cumsum <- 0
            signalLockoutUntil <- barsSeen + threshold
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        match stopPriceIfHit bar with
        | Some stopPx -> closePos stopPx bar.EndUs
        | None -> ()

        // Update vol/ADV/orderflow tracking BEFORE signal evaluation so the
        // current bar's contribution is reflected in entry-time state.
        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close
        advMa.Push (bar.BuyDollarVolume + bar.SellDollarVolume)
        buyMa.Push  bar.BuyDollarVolume
        sellMa.Push bar.SellDollarVolume

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        // Per-bar vote: sign of v0's smoothed orderflow signal. Pre-fill
        // (rolling window not full) votes 0.
        let vote =
            if barsSeen < n then 0
            else
                let diff = buyMa.State - sellMa.State
                if diff > 0.0 then 1
                elif diff < 0.0 then -1
                else 0

        // Update clamped cumsum.
        let prevCumsum = cumsum
        let next = cumsum + vote
        cumsum <-
            if next > threshold then threshold
            elif next < -threshold then -threshold
            else next

        // Signal evaluation. Edge-trigger only — we react when the cumsum
        // FIRST touches a clamp from inside (prevCumsum strictly inside the
        // band, cumsum at the clamp). While clamped, no re-fire. Lockout
        // (post-gap) suppresses new positions for one threshold of bars.
        if barsSeen >= signalLockoutUntil then
            let hitTop    = prevCumsum < threshold && cumsum = threshold
            let hitBottom = prevCumsum > -threshold && cumsum = -threshold

            if hitTop then
                // Long signal: exit any open short, enter long.
                if side = Short then closePos bar.Close bar.EndUs
                if side = Flat then
                    let adv = currentAdv ()
                    let advGate = cfg.MinLongAdv <= 0.0 || adv >= cfg.MinLongAdv
                    if advGate then
                        openPos Long bar.Close bar.EndUs cumsum
            elif hitBottom && cfg.AllowShort then
                if side = Long then closePos bar.Close bar.EndUs
                if side = Flat then
                    let adv = currentAdv ()
                    let advGate = cfg.MinShortAdv <= 0.0 || adv >= cfg.MinShortAdv
                    if advGate then
                        openPos Short bar.Close bar.EndUs cumsum
            elif hitBottom && not cfg.AllowShort && side = Long then
                // Bear extreme reached but short is disabled: still exit the
                // long (regime has fully reversed against us). Stay flat
                // until the next +threshold touch.
                closePos bar.Close bar.EndUs

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: CumsumConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
