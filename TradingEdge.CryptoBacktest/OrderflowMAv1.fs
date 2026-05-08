module TradingEdge.CryptoBacktest.OrderflowMAv1

open MathNet.Numerics
open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Orderflow-MA v1 — RawZ-only, persistence-required, rvol-bucketed sizing
// =============================================================================
//
// Direct successor to OrderflowCumsumZ (RawZ-100 winner config). v0/Erf
// branches and the optional non-persistence-gated exit path are removed —
// this engine is exactly the variant that won the v0 sweeps:
//   * raw-z magnitude (each bar contributes its full z-score)
//   * persistence required for both entry AND exit (200h regime sign)
//   * 200h MA window (also configurable)
//   * threshold default 100
//
// The new feature: VARIABLE BET SIZING via an rvol step function.
//   * rvol = (volume mean over RvolRecentHours) / (volume mean over
//     RvolBaselineDays). Defaults: 24h numerator, 30d denominator.
//   * SizingBuckets is a sorted list of (lowExclusive, highInclusive,
//     sizeMultiplier) tuples. The first matching bucket wins; if none
//     matches, multiplier is 0.0 (notional is zero).
//   * effectiveNotional = baseNotional * sizeMultiplier(rvolAtEntry)
//   * Crucially, a 0-multiplier trade still EXECUTES through the engine's
//     state machine — the position is opened (with effective_notional=0)
//     and closed exactly as a sized trade would be. Cumsum drainage,
//     persistence flips, and follow-up entries fire identically whether or
//     not the entry was sized > 0. This is the whole point: filtering by
//     sizing instead of by entry-veto leaves the engine state unaffected
//     by the filter, so different size schedules are directly comparable
//     on the same trade-event stream.
//   * Each side has its own bucket schedule. Default for shorts is the
//     "PM session" finding [(0,2,0); (2,10,1); (10,inf,0)] — fade only
//     the 2-10x rvol bucket. Default for longs is [(0,inf,0)] — longs
//     are sized to zero by default, since the asymmetric findings showed
//     long fades require a different entry mechanic (see LongFadeMA).
//   * 0-size trips are still recorded so analysis can recover the bucket
//     distribution from the trip CSV.
//
// Also dropped from v0:
//   * MagnitudeMode (always RawZ)
//   * RequirePersistenceForExit (always on)
//   * The unclamped legacy cumsum branch (lives in OrderflowCumsum.fs;
//     not referenced here).
//
// Carried forward from v0:
//   * Rolling buy/sell MAs (persistence gate)
//   * Rolling delta-std (RawZ normalizer)
//   * ADV gate, vol-stop, vwap-stop, percentage-stop, gap-detector
//   * Funding accounting

/// One bucket in the sizing schedule. Inclusive on the high side, exclusive
/// on the low side, so adjacent buckets like (0,2] and (2,10] are clean.
type SizingBucket = {
    LowExclusive: float
    HighInclusive: float
    SizeMultiplier: float
}

type MAv1Config = {
    /// Clamped-cumsum magnitude threshold. Long fires at +CumsumThreshold,
    /// short fires at -CumsumThreshold. Default 100 (RawZ-100 v0 winner).
    CumsumThreshold: float
    /// Rolling-window length in HOURS for both the std-of-delta normalizer
    /// and the buy/sell persistence gate. Default 200.
    MaWindowHours: int
    Notional: float
    TakerFee: float
    BucketUs: int64
    MaxAdverseFraction: float
    ReferenceVol: float
    MinLongAdv: float
    MinShortAdv: float
    VolWindowDays: int
    MaxBarPriceRatio: float
    /// Trailing-VWAP-band stop in HOURS. 0 = disabled.
    VwapStopHours: int
    /// Vol-based stop in M * volMa.SampleStd-at-entry log-return units.
    /// 0 = disabled. Takes precedence over VwapStopHours and MaxAdverseFraction.
    VolStopMultiplier: float
    /// Numerator window length (hours) for the rvol value used by the
    /// sizing buckets. Default 24.
    RvolRecentHours: int
    /// Denominator window length (days) for the rvol value used by the
    /// sizing buckets. Default 30.
    RvolBaselineDays: int
    /// Sizing schedule for short entries. Each entry has its rvol mapped
    /// to a multiplier; effectiveNotional = Notional * multiplier. If no
    /// bucket matches, multiplier is 0.0. Buckets are evaluated in order;
    /// first match wins.
    ShortSizingBuckets: SizingBucket list
    /// Sizing schedule for long entries. Default [(0,inf,0)] — longs are
    /// off in v1 by default since the long-fade edge belongs in LongFadeMA.
    LongSizingBuckets: SizingBucket list
}

let defaultMAv1Config () : MAv1Config =
    { CumsumThreshold = 100.0
      MaWindowHours = 200
      Notional = 1000.0
      TakerFee = 0.0004
      BucketUs = 0L
      MaxAdverseFraction = 0.0
      ReferenceVol = 0.0
      MinLongAdv = 0.0
      MinShortAdv = 0.0
      VolWindowDays = 90
      MaxBarPriceRatio = 0.0
      VwapStopHours = 0
      VolStopMultiplier = 0.0
      RvolRecentHours = 24
      RvolBaselineDays = 30
      ShortSizingBuckets =
        [ { LowExclusive = 2.0;  HighInclusive = 10.0;     SizeMultiplier = 1.0 } ]
      LongSizingBuckets =
        [ { LowExclusive = 0.0;  HighInclusive = infinity; SizeMultiplier = 0.0 } ] }

let private feeFor (notional: float) (takerFee: float) : float = notional * takerFee

let private grossPnL (side: Side) (entry: float) (exit: float) (notional: float) : float =
    let qty = if entry > 0.0 then notional / entry else 0.0
    match side with
    | Long  -> (exit - entry) * qty
    | Short -> (entry - exit) * qty
    | Flat  -> 0.0

/// Look up the size multiplier for `rvol` in `buckets`. First (lowExclusive,
/// highInclusive] match wins; returns 0.0 if no bucket matches.
let private resolveMultiplier (buckets: SizingBucket list) (rvol: float) : float =
    let rec loop = function
        | [] -> 0.0
        | b :: rest ->
            if rvol > b.LowExclusive && rvol <= b.HighInclusive then b.SizeMultiplier
            else loop rest
    loop buckets

type Engine(cfg: MAv1Config) =
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

    // Rvol state. Numerator is mean of bar.Volume over RvolRecentHours;
    // denominator is mean of bar.Volume over RvolBaselineDays. Maintained
    // unconditionally — the sizing logic always reads it.
    let nRvolEntry = hoursToBars (max 1 cfg.RvolRecentHours)
    let nRvolBase  = daysToBars  (max 1 cfg.RvolBaselineDays)
    let volEntry    = SumMa(nRvolEntry)
    let volBaseline = SumMa(nRvolBase)

    // 200h-window state for the persistence gate + RawZ normalizer.
    let n = hoursToBars (max 1 cfg.MaWindowHours)
    let buyMa = SumMa(n)
    let sellMa = SumMa(n)
    let deltaStd = StdMa(n)

    let vwapStopBars = if cfg.VwapStopHours > 0 then hoursToBars cfg.VwapStopHours else 0
    let vwapMaxMa = MaxMa(max 1 vwapStopBars)
    let vwapMinMa = MinMa(max 1 vwapStopBars)

    let threshold = cfg.CumsumThreshold
    let mutable cumsum = 0.0

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
    let mutable volAtEntry = 0.0
    let mutable rvolAtEntry = 0.0

    let mutable lastClose = 0.0
    let mutable lastUs = 0L

    let mutable signalLockoutUntil = 0

    let mutable fundingEvents : (int64 * float)[] = [||]
    let mutable fundingPtr = 0

    let trips = ResizeArray<RoundTrip>()

    let computeBaseEffectiveNotional () : float =
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

    let currentRvol () : float =
        if volBaseline.Count = 0 || nRvolEntry = 0 then 0.0
        else
            let entryMean    = volEntry.State / float nRvolEntry
            let baselineMean = volBaseline.State / float volBaseline.Count
            if baselineMean > 0.0 then entryMean / baselineMean else 0.0

    let openPos (newSide: Side) (fillPrice: float) (fillUs: int64) (cumAtFire: float) (rvol: float) =
        let baseNotional = computeBaseEffectiveNotional ()
        let buckets =
            match newSide with
            | Long  -> cfg.LongSizingBuckets
            | Short -> cfg.ShortSizingBuckets
            | Flat  -> []
        let mult = resolveMultiplier buckets rvol
        side <- newSide
        entryPrice <- fillPrice
        entryUs <- fillUs
        effectiveNotional <- baseNotional * mult
        barsHeld <- 1
        mfeUsd <- 0.0
        maeUsd <- 0.0
        ratioAtEntry <- cumAtFire
        fundingPnl <- 0.0
        advAtEntry <- currentAdv ()
        volAtEntry <- volMa.SampleStd
        rvolAtEntry <- rvol

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
            // Stash rvol on the trip record so quintile-bin scripts can
            // recover the bucket distribution. RatioAtEntry is the
            // observational signal; the cumsum-at-fire is decoded from
            // the engine config + threshold if needed.
            RatioAtEntry = rvolAtEntry
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
        volAtEntry <- 0.0
        rvolAtEntry <- 0.0

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
        if side = Flat || effectiveNotional <= 0.0 || entryPrice <= 0.0 then ()
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
        if cfg.MaxAdverseFraction <= 0.0 || side = Flat || effectiveNotional <= 0.0 then None
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

    let volStopPriceIfHit (bar: SignedBar) : float option =
        if cfg.VolStopMultiplier <= 0.0 || side = Flat || volAtEntry <= 0.0 then None
        else
            let stopRet = cfg.VolStopMultiplier * volAtEntry
            match side with
            | Long ->
                let stopPx = entryPrice * exp (-stopRet)
                if bar.Low <= stopPx then Some stopPx else None
            | Short ->
                let stopPx = entryPrice * exp (+stopRet)
                if bar.High >= stopPx then Some stopPx else None
            | Flat -> None

    let vwapStopPriceIfHit (bar: SignedBar) : float option =
        if cfg.VwapStopHours <= 0 || side = Flat then None
        else
            match side with
            | Long ->
                if vwapMinMa.Count < vwapStopBars then None
                elif bar.VWAP <= vwapMinMa.State then Some bar.VWAP
                else None
            | Short ->
                if vwapMaxMa.Count < vwapStopBars then None
                elif bar.VWAP >= vwapMaxMa.State then Some bar.VWAP
                else None
            | Flat -> None

    member _.BarsSeen = barsSeen
    member _.Trips = trips :> seq<RoundTrip>
    member _.InTrade : bool = side <> Flat

    member _.SetFundingEvents(events: (int64 * float)[]) =
        fundingEvents <- events
        fundingPtr <- 0

    member _.ProcessBar(bar: SignedBar) =
        applyFunding bar

        let gapFired = priceGapHit bar
        if gapFired then
            closePos lastClose lastUs
            // Reset cumsum to 0 and lockout signals for the warmup window
            // after a gap. The pre-gap rolling state is now stale relative
            // to the post-gap price/volume regime; firing immediately would
            // be reading garbage. Match v0 behavior exactly.
            cumsum <- 0.0
            signalLockoutUntil <- barsSeen + n
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        // Stop precedence: vol-stop > vwap-stop > pct-stop. Each only
        // fires when the corresponding feature is enabled in cfg.
        if cfg.VolStopMultiplier > 0.0 then
            match volStopPriceIfHit bar with
            | Some stopPx -> closePos stopPx bar.EndUs
            | None -> ()
        elif cfg.VwapStopHours > 0 then
            match vwapStopPriceIfHit bar with
            | Some stopPx -> closePos stopPx bar.EndUs
            | None -> ()
        else
            match stopPriceIfHit bar with
            | Some stopPx -> closePos stopPx bar.EndUs
            | None -> ()

        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close
        advMa.Push (bar.BuyDollarVolume + bar.SellDollarVolume)
        volEntry.Push    bar.Volume
        volBaseline.Push bar.Volume
        let delta = bar.BuyDollarVolume - bar.SellDollarVolume
        buyMa.Push  bar.BuyDollarVolume
        sellMa.Push bar.SellDollarVolume
        deltaStd.Push delta
        if cfg.VwapStopHours > 0 then
            vwapMaxMa.Push bar.VWAP
            vwapMinMa.Push bar.VWAP

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        // Per-bar magnitude — RawZ only.
        let magnitude =
            if barsSeen < n then 0.0
            else
                let sigma = deltaStd.SampleStd
                if sigma <= 0.0 then 0.0
                else delta / sigma

        let prevCumsum = cumsum
        let next = cumsum + magnitude
        cumsum <-
            if next > threshold then threshold
            elif next < -threshold then -threshold
            else next

        if barsSeen >= signalLockoutUntil then
            let hitTop    = prevCumsum < threshold && cumsum >= threshold
            let hitBottom = prevCumsum > -threshold && cumsum <= -threshold

            // Persistence: the 200h-regime sign must agree with the
            // direction of the cumsum-driven fire AND must have flipped
            // against an open position before that position can close.
            let regimeBull = buyMa.State > sellMa.State
            let regimeBear = sellMa.State > buyMa.State

            // Persistence-required exit (always on in v1).
            let canCloseShort = regimeBull
            let canCloseLong  = regimeBear

            if hitTop then
                if side = Short && canCloseShort then closePos bar.Close bar.EndUs
                if side = Flat && regimeBull then
                    let adv = currentAdv ()
                    let advGate = cfg.MinLongAdv <= 0.0 || adv >= cfg.MinLongAdv
                    if advGate then
                        openPos Long bar.Close bar.EndUs cumsum (currentRvol ())
            elif hitBottom then
                if side = Long && canCloseLong then closePos bar.Close bar.EndUs
                if side = Flat && regimeBear then
                    let adv = currentAdv ()
                    let advGate = cfg.MinShortAdv <= 0.0 || adv >= cfg.MinShortAdv
                    if advGate then
                        openPos Short bar.Close bar.EndUs cumsum (currentRvol ())

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: MAv1Config) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
