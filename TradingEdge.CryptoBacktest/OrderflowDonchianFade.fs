module TradingEdge.CryptoBacktest.OrderflowDonchianFade

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Donchian-channel fade engine — Lance-style trend-exhaustion
// =============================================================================
//
// Pure price-structure fade. No CVD trigger, no rvol gate, no smoothed MA.
//
// Setup (SHORT side):
//   1. A 30-bar uptrend run: at least cfg.MinTrendBars consecutive 1m bars
//      have NOT pierced the rolling DonchianBars-bar Donchian low.
//   2. The current bar finally breaks below the rolling Donchian low.
//   3. Fade the prior trend: open a SHORT at bar.Close.
//   4. Stop = rolling Donchian high at entry, ratcheted *down only* on
//      subsequent bars (a new high never widens the stop).
//   5. Cover when bar.High > trailingStop (re-violation of the just-broken
//      direction). Fill at bar.Close.
//
// LONG side is symmetric (downtrend run → break-up → fade long; stop at
// rolling Donchian low, ratcheted up).
//
// Read-before-write: all signal logic runs against rolling state from the
// PREVIOUS N bars (not including this one). MAs are pushed at the very end
// of ProcessBar, so donHighMa.State / donLowMa.State during entry/exit/
// counter checks reflect the prior window.

type DonchianRoundTrip = {
    EntryUs: int64
    ExitUs: int64
    Side: Side
    EntryPrice: float
    ExitPrice: float
    NetPnL: float
    Fees: float
    BarsHeld: int
    MaxFavorableExcursion: float
    MaxAdverseExcursion: float
    EffectiveNotional: float
    FundingPnL: float
    AvgDailyVolumeAtEntry: float
    // Donchian-specific post-hoc fields:
    BarsSinceUpViolationAtEntry: int
    BarsSinceDownViolationAtEntry: int
    Pct1hChangeAtEntry: float
    Pct72hChangeAtEntry: float
    PriceRatio72hOver1hAtEntry: float
    VolRatio1hOver72hAtEntry: float
}

type DonchianFadeConfig = {
    /// Donchian channel window in 1m bars. Default 3.
    /// Rolling MaxMa(N) of bar.High and MinMa(N) of bar.Low.
    DonchianBars: int
    /// Required consecutive-bar trend run before a break is fadeable.
    /// Default 30. Counts bars since the LAST violation of the opposite
    /// channel side. For a SHORT, requires MinTrendBars without bar.Low
    /// piercing the prior Donchian low.
    MinTrendBars: int
    /// Short-window reference in MINUTES (default 60 = 1h). Used for the
    /// 1h percent-change at entry (post-hoc) and the 1h vol/price MAs.
    ShortRefMinutes: int
    /// Long-window reference in HOURS (default 72). Trailing mean of close
    /// and trailing mean of volume — both surfaced as ratios on the trip
    /// record for post-hoc breakdown.
    LongRefHours: int
    /// Allow long-side fades (downtrend → break-up → fade long). Default true.
    AllowLong: bool
    /// Allow short-side fades (uptrend → break-down → fade short). Default true.
    AllowShort: bool
    Notional: float
    TakerFee: float
    BucketUs: int64
    MaxAdverseFraction: float
    ReferenceVol: float
    MinShortAdv: float
    MinLongAdv: float
    VolWindowDays: int
    MaxBarPriceRatio: float
}

let defaultDonchianFadeConfig () : DonchianFadeConfig =
    { DonchianBars = 3
      MinTrendBars = 30
      ShortRefMinutes = 60
      LongRefHours = 72
      AllowLong = true
      AllowShort = true
      Notional = 1000.0
      TakerFee = 0.0004
      BucketUs = 0L
      MaxAdverseFraction = 0.0
      ReferenceVol = 0.0
      MinShortAdv = 0.0
      MinLongAdv = 0.0
      VolWindowDays = 90
      MaxBarPriceRatio = 0.0 }

let private feeFor (notional: float) (takerFee: float) : float = notional * takerFee

let private grossPnL (side: Side) (entry: float) (exit: float) (notional: float) : float =
    let qty = notional / entry
    match side with
    | Long  -> (exit - entry) * qty
    | Short -> (entry - exit) * qty
    | Flat  -> 0.0

type Engine(cfg: DonchianFadeConfig) =
    let mutable prevClose = 0.0
    let mutable barsSeen = 0

    let hoursToBars (hours: int) : int =
        if cfg.BucketUs > 0L then
            max 1 (int (int64 hours * 3_600_000_000L / cfg.BucketUs))
        else
            max 1 hours

    let daysToBars (days: int) : int = hoursToBars (24 * days)

    let minutesToBars (minutes: int) : int =
        if cfg.BucketUs > 0L then
            max 1 (int (int64 minutes * 60_000_000L / cfg.BucketUs))
        else
            max 1 minutes

    let nDon       = max 1 cfg.DonchianBars
    let nMinTrend  = max 1 cfg.MinTrendBars
    let nShortRef  = minutesToBars (max 1 cfg.ShortRefMinutes)
    let nLongRef   = hoursToBars   (max 1 cfg.LongRefHours)
    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let warmupBars = hoursToBars 200

    let donHighMa = MaxMa(nDon)
    let donLowMa  = MinMa(nDon)
    let closeShortMa = SumMa(nShortRef)
    let closeLongMa  = SumMa(nLongRef)
    let volShortMa = SumMa(nShortRef)
    let volLongMa  = SumMa(nLongRef)
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)


    // Violation counters — read & updated against the PREVIOUS bar's
    // Donchian state (rolling state before the current bar is pushed).
    let mutable barsSinceUpViolation   = 0
    let mutable barsSinceDownViolation = 0

    // Position state.
    let mutable side = Flat
    let mutable entryPrice = 0.0
    let mutable entryUs = 0L
    let mutable effectiveNotional = 0.0
    let mutable barsHeld = 0
    let mutable mfeUsd = 0.0
    let mutable maeUsd = 0.0
    let mutable fundingPnl = 0.0
    let mutable advAtEntry = 0.0
    let mutable trailingStop = 0.0

    // Per-trip stash for the trip CSV (filled at entry, written at close).
    let mutable barsSinceUpViolationAtEntry = 0
    let mutable barsSinceDownViolationAtEntry = 0
    let mutable pct1hChangeAtEntry = 0.0
    let mutable pct72hChangeAtEntry = 0.0
    let mutable priceRatio72hOver1hAtEntry = 0.0
    let mutable volRatio1hOver72hAtEntry = 0.0

    let mutable lastClose = 0.0
    let mutable lastUs = 0L
    let mutable signalLockoutUntil = 0

    let mutable fundingEvents : (int64 * float)[] = [||]
    let mutable fundingPtr = 0

    let trips = ResizeArray<DonchianRoundTrip>()

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

    // closeRefAt1hAgo: the close from nShortRef bars ago, or 0.0 until the
    // ring is full. When the ring holds nShortRef+1 entries the front is
    // exactly nShortRef bars old.
    let openPos
        (newSide: Side)
        (fillPrice: float)
        (fillUs: int64)
        (stopLevel: float)
        (pct1hChange: float)
        (pct72hChange: float)
        (priceRatio: float)
        (volRatio: float) =
        side <- newSide
        entryPrice <- fillPrice
        entryUs <- fillUs
        effectiveNotional <- computeEffectiveNotional ()
        barsHeld <- 1
        mfeUsd <- 0.0
        maeUsd <- 0.0
        fundingPnl <- 0.0
        advAtEntry <- currentAdv ()
        trailingStop <- stopLevel
        barsSinceUpViolationAtEntry   <- barsSinceUpViolation
        barsSinceDownViolationAtEntry <- barsSinceDownViolation
        pct1hChangeAtEntry <- pct1hChange
        pct72hChangeAtEntry <- pct72hChange
        priceRatio72hOver1hAtEntry <- priceRatio
        volRatio1hOver72hAtEntry <- volRatio

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
            EffectiveNotional = effectiveNotional
            FundingPnL = fundingPnl
            AvgDailyVolumeAtEntry = advAtEntry
            BarsSinceUpViolationAtEntry = barsSinceUpViolationAtEntry
            BarsSinceDownViolationAtEntry = barsSinceDownViolationAtEntry
            Pct1hChangeAtEntry = pct1hChangeAtEntry
            Pct72hChangeAtEntry = pct72hChangeAtEntry
            PriceRatio72hOver1hAtEntry = priceRatio72hOver1hAtEntry
            VolRatio1hOver72hAtEntry = volRatio1hOver72hAtEntry
        }
        side <- Flat
        entryPrice <- 0.0
        entryUs <- 0L
        effectiveNotional <- 0.0
        barsHeld <- 0
        mfeUsd <- 0.0
        maeUsd <- 0.0
        fundingPnl <- 0.0
        advAtEntry <- 0.0
        trailingStop <- 0.0
        barsSinceUpViolationAtEntry <- 0
        barsSinceDownViolationAtEntry <- 0
        pct1hChangeAtEntry <- 0.0
        pct72hChangeAtEntry <- 0.0
        priceRatio72hOver1hAtEntry <- 0.0
        volRatio1hOver72hAtEntry <- 0.0

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

    let pctStopPriceIfHit (bar: SignedBar) : float option =
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
    member _.Trips = trips :> seq<DonchianRoundTrip>
    member _.InTrade : bool = side <> Flat

    member _.SetFundingEvents(events: (int64 * float)[]) =
        fundingEvents <- events
        fundingPtr <- 0

    member _.ProcessBar(bar: SignedBar) =
        applyFunding bar

        let gapFired = priceGapHit bar
        if gapFired then
            closePos lastClose lastUs
            signalLockoutUntil <- barsSeen + warmupBars
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        // Stop precedence: pct-stop first, then engine-specific Donchian
        // trailing stop. Strict inequality matches the violation/entry style.
        match pctStopPriceIfHit bar with
        | Some stopPx -> closePos stopPx bar.EndUs
        | None ->
            if side = Short && trailingStop > 0.0 && bar.High > trailingStop then
                closePos bar.Close bar.EndUs
            elif side = Long && trailingStop > 0.0 && bar.Low < trailingStop then
                closePos bar.Close bar.EndUs

        // Pre-push read of Donchian state — these reflect the previous nDon
        // bars (the "prior channel"). Window-readiness gates the counter
        // updates and the entry checks.
        let donHighReady = donHighMa.Count >= nDon
        let donLowReady  = donLowMa.Count  >= nDon
        let prevDonHigh = if donHighReady then donHighMa.State else 0.0
        let prevDonLow  = if donLowReady  then donLowMa.State  else 0.0

        // Entry FIRST — uses the run-length counters from the BAR BEFORE
        // this one. On a break-bar, this is exactly the "30 bars without
        // violating the opposite side, then this bar finally pierces"
        // semantics. The counters get updated below, after the entry
        // check, so the post-update counter for this bar is 0 (reset on
        // the breaking bar) and is correctly stashed on the trip record.
        let windowsReady =
            barsSeen             >= warmupBars
            && barsSeen          >= signalLockoutUntil
            && donHighReady && donLowReady
            && closeShortMa.Count >= nShortRef
            && closeLongMa.Count  >= nLongRef

        if windowsReady && side = Flat then
            // Post-hoc fields — same values for both sides.
            let closeShortMean =
                if closeShortMa.Count > 0
                then closeShortMa.State / float closeShortMa.Count
                else 0.0
            let closeLongMean =
                if closeLongMa.Count > 0
                then closeLongMa.State / float closeLongMa.Count
                else 0.0
            let volShortMean =
                if volShortMa.Count > 0
                then volShortMa.State / float volShortMa.Count
                else 0.0
            let volLongMean =
                if volLongMa.Count > 0
                then volLongMa.State / float volLongMa.Count
                else 0.0
            let priceRatio72hOver1h =
                if closeShortMean > 0.0 then closeLongMean / closeShortMean else 0.0
            let volRatio1hOver72h =
                if volLongMean > 0.0 then volShortMean / volLongMean else 0.0
            // Pct change vs the trailing MA — smoother than a single
            // point-in-time close N bars ago. closeShortMean is the 1h MA
            // already computed above; closeLongMean is the 72h MA.
            let pct1hChange =
                if closeShortMean > 0.0 then bar.Close / closeShortMean - 1.0 else 0.0
            let pct72hChange =
                if closeLongMean > 0.0 then bar.Close / closeLongMean - 1.0 else 0.0

            // SHORT-fade: sustained uptrend (no down-violation for
            // MinTrendBars) AND this bar broke the prior Donchian low.
            let canShort =
                cfg.AllowShort
                && barsSinceDownViolation >= nMinTrend
                && bar.Low < prevDonLow
                && (cfg.MinShortAdv <= 0.0 || currentAdv () >= cfg.MinShortAdv)

            // LONG-fade: sustained downtrend AND this bar broke the prior
            // Donchian high.
            let canLong =
                cfg.AllowLong
                && barsSinceUpViolation >= nMinTrend
                && bar.High > prevDonHigh
                && (cfg.MinLongAdv <= 0.0 || currentAdv () >= cfg.MinLongAdv)

            if canShort then
                openPos Short bar.Close bar.EndUs prevDonHigh
                    pct1hChange pct72hChange priceRatio72hOver1h volRatio1hOver72h
            elif canLong then
                openPos Long bar.Close bar.EndUs prevDonLow
                    pct1hChange pct72hChange priceRatio72hOver1h volRatio1hOver72h

        // Update violation counters AFTER the entry check, so the entry
        // logic sees the run-length going INTO this bar. The breaking bar
        // resets the counter to 0 here, but openPos has already stashed
        // the pre-reset value into barsSinceUp/DownViolationAtEntry.
        if donHighReady then
            if bar.High > prevDonHigh then barsSinceUpViolation <- 0
            else barsSinceUpViolation <- barsSinceUpViolation + 1
        if donLowReady then
            if bar.Low < prevDonLow then barsSinceDownViolation <- 0
            else barsSinceDownViolation <- barsSinceDownViolation + 1

        // Push bars into all rolling MAs — last, after all entry/exit/
        // counter logic has consumed the previous-bar state.
        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close
        let totalDv = bar.BuyDollarVolume + bar.SellDollarVolume
        advMa.Push totalDv
        donHighMa.Push bar.High
        donLowMa.Push  bar.Low
        closeShortMa.Push bar.Close
        closeLongMa.Push  bar.Close
        volShortMa.Push bar.Volume
        volLongMa.Push  bar.Volume

        // Update trailing stop post-push so the freshly-included current
        // bar is reflected in the rolling-MA state. Ratchet down only for
        // shorts, up only for longs.
        if side = Short && trailingStop > 0.0 then
            let newStop = donHighMa.State
            if newStop > 0.0 && newStop < trailingStop then
                trailingStop <- newStop
        elif side = Long && trailingStop > 0.0 then
            let newStop = donLowMa.State
            if newStop > 0.0 && newStop > trailingStop then
                trailingStop <- newStop

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: DonchianFadeConfig) (bars: SignedBar[]) : DonchianRoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
