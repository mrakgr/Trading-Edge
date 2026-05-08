module TradingEdge.CryptoBacktest.OrderflowLongFadeMA

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Long fade engine — mean-reversion to a time-based MA
// =============================================================================
//
// Sibling to OrderflowExtremeRvolLong but with a different exit thesis:
// instead of waiting for volume to normalise (which doesn't filter losers
// on the long side per the v8 quintile sweep), we cover when the bar
// closes back above a straight 24h time-based MA. Entry uses no volume
// gate — the v8 quintile sweep showed rvol is non-monotonic for longs.
// We rely on the price-decline gate and the CVD-cross trigger alone.
//
// Per-bar signals:
//   priceDecline = 1 - bar.Close / coverMa           (positive when down)
//   coverMa      = trailing CoverMaHours MA of bar.Close. Single reference
//                  for BOTH the decline gate and the cover-on-touch exit:
//                  decline is "% below the very target the trade is
//                  reverting to". Eliminates the stale-reference problem
//                  that would otherwise let trades fire on bars where the
//                  cover MA already sits at or below entry close.
//   cvd          = trailing-CvdMinutes CVD = Σ_{Nm}(buy_dv − sell_dv)
//
// Entry — ALL must hold simultaneously:
//   - flat
//   - cfg.AllowLong
//   - priceDecline >= cfg.PriceDeclineThreshold       (default 0.10; close <= (1 - threshold) * coverMa)
//   - prevCvd <= 0 AND cvd > 0                        (CVD just turned positive)
//   - ADV gate (cfg.MinLongAdv)
//   - distance gate: bar.Close within EntryDistanceMaxPct of the
//     reference low (Stop20m or Low8h). Filters out late entries where
//     the bounce has already started.
//   - 200h warm-up: barsSeen >= 200h AND every short window full.
//
// Stop — trailing-N-minute MinMa of bar.Low, snapshotted at entry and
// held fixed for the trade (default 20m). Trigger: bar.VWAP <= stop;
// fill at bar.Close.
//
// Cover — bar.Close >= meanMa. Fires the bar after the close prints
// at or above the trailing 24h MA; fill at bar.Close. No VWAP filter
// here — close-vs-MA is the cleanest "we got our reversion" check.
//
// Time-stop (optional) — when cfg.TimeStopMinutes > 0, close after that
// many minutes per cfg.TimeStopMode. Hard closes unconditionally;
// Conditional closes only if the trade isn't profitable
// (bar.Close <= entryPrice for a long). Time-stop takes precedence over
// the MA cover.

type TimeStopMode =
    | Hard
    | Conditional

type EntryDistanceRef =
    | Stop20m
    | Low8h

type LongFadeMAConfig = {
    /// Fractional price-decline gate against the cover MA.
    /// Default 0.10 → entry requires bar.Close <= 0.90 × coverMa.
    /// Using the same MA for both the gate and the exit eliminates the
    /// stale-reference problem that lets trades fire on bars where the
    /// cover already sits at or below entry close.
    PriceDeclineThreshold: float
    /// Cover MA window in HOURS. Default 24. Cover when bar.Close >=
    /// trailing-N-hour mean of close. Time-based, not volume-based —
    /// since this engine doesn't gate by volume, the cover can't either.
    CoverMaHours: int
    /// Recent-window length (hours) for the lagged-MA derivation.
    /// Default 8 → 8h-MA-lagged-LagHours.
    RecentHours: int
    /// Lag (hours) for the price-reference MA. Default 16.
    LagHours: int
    /// Trailing-CVD window (minutes) for the entry trigger. Default 75.
    CvdMinutes: int
    /// Low-stop window (minutes): trailing-N MinMa of bar.Low,
    /// snapshotted at entry and held fixed for the trade. Default 20.
    /// Pass 0 to disable the stop entirely (the trade only exits via
    /// MA-touch, time-stop, or price-gap). The rolling MinLow state is
    /// still maintained for downstream uses (entry-distance gate with
    /// EntryDistanceRef = Stop20m, RR gate); only the stop-fill check
    /// is short-circuited.
    StopLowWindowMinutes: int
    /// Time-stop window (minutes). Default 90. Pass 0 to disable.
    TimeStopMinutes: int
    /// Time-stop semantics. Only active when TimeStopMinutes > 0.
    TimeStopMode: TimeStopMode
    /// Entry-distance gate: max fractional distance from EntryDistanceRef
    /// to bar.Close at entry. Default 0.20 (sweep-validated for the
    /// rvol-gated long engine; carried over).
    EntryDistanceMaxPct: float
    /// Which surface to measure entry distance against. Default Low8h.
    EntryDistanceRef: EntryDistanceRef
    /// Minimum reward:risk ratio at entry. Risk = bar.Close - stopLevel
    /// (drop from entry to the 20m min-Low stop). Reward = coverMa -
    /// bar.Close (room to mean-revert up to the cover MA). Reject the
    /// trade unless reward / risk >= this. Default 0.0 (disabled). The
    /// fixed-reference design makes this redundant since pd > 0 already
    /// guarantees coverMa > close.
    MinRewardRiskRatio: float
    /// Minimum rvol at entry (computed over the CvdMinutes horizon).
    /// Reject trades with `rvolEntry < this`. Default 0.75 — the v10c
    /// quintile breakdown showed Q1 (rvol < 0.75) had PF 1.83 vs PF
    /// 3.14 aggregate; filtering Q1 out lifts aggregate PF cleanly.
    /// Pass 0 to disable.
    RvolEntryThreshold: float
    Notional: float
    TakerFee: float
    /// Always true for this engine; flag exists for parity.
    AllowLong: bool
    BucketUs: int64
    MaxAdverseFraction: float
    ReferenceVol: float
    MinLongAdv: float
    VolWindowDays: int
    MaxBarPriceRatio: float
}

let defaultLongFadeMAConfig () : LongFadeMAConfig =
    { PriceDeclineThreshold = 0.14
      CoverMaHours = 72
      RecentHours = 8
      LagHours = 16
      CvdMinutes = 240
      StopLowWindowMinutes = 0
      TimeStopMinutes = 0
      TimeStopMode = Conditional
      EntryDistanceMaxPct = 0.20
      EntryDistanceRef = Low8h
      MinRewardRiskRatio = 0.0
      RvolEntryThreshold = 0.75
      Notional = 1000.0
      TakerFee = 0.0004
      AllowLong = true
      BucketUs = 0L
      MaxAdverseFraction = 0.0
      ReferenceVol = 0.0
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

type Engine(cfg: LongFadeMAConfig) =
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

    let nLag       = hoursToBars   (max 1 cfg.LagHours)
    let nLagBig    = hoursToBars   (max 1 (cfg.RecentHours + cfg.LagHours))
    let nCvd       = minutesToBars (max 1 cfg.CvdMinutes)
    let nCoverMa   = hoursToBars   (max 1 cfg.CoverMaHours)
    let nStopLow   = minutesToBars (max 1 cfg.StopLowWindowMinutes)
    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let warmupBars = hoursToBars 200
    // Observational rvol — surfaced on the trip record as RatioAtEntry
    // for downstream analysis (no entry gating). Numerator window
    // matches CvdMinutes so rvol and CVD share a horizon: the volume
    // ratio is "how much volume vs baseline traded in the same window
    // that fired the CVD cross". 30d denominator.
    let nRvolEntry = nCvd
    let nRvolBase  = daysToBars 30

    // Close-price rolling sums for the lagged-MA derivation.
    let closeRecent = SumMa(nLagBig)
    let closeLag    = SumMa(nLag)
    // Cover MA — trailing-N-hour mean of bar.Close.
    let coverSum = SumMa(nCoverMa)
    // CVD trigger (mirror of the long fade — long enters on positive cross).
    let cvdMa = SumMa(nCvd)
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)
    let lowMinMa = MinMa(nStopLow)
    let nLow8h    = hoursToBars 8
    let low8hMinMa = MinMa(nLow8h)
    // Observational rvol state.
    let volEntry    = SumMa(nRvolEntry)
    let volBaseline = SumMa(nRvolBase)

    // Position state.
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
    let mutable stopPriceAtEntry = 0.0
    let mutable priceDeclineAtEntry = 0.0

    let mutable prevCvd = 0.0
    let mutable lastClose = 0.0
    let mutable lastUs = 0L

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

    let openLong (fillPrice: float) (fillUs: int64) (stopLevel: float) (declineAtFire: float) (rvolAtFire: float) =
        side <- Long
        entryPrice <- fillPrice
        entryUs <- fillUs
        effectiveNotional <- computeEffectiveNotional ()
        barsHeld <- 1
        mfeUsd <- 0.0
        maeUsd <- 0.0
        // Observational only — surfaced on the trip record so the
        // downstream analysis can quintile-bin trades by rvol without
        // any gating happening here.
        ratioAtEntry <- rvolAtFire
        fundingPnl <- 0.0
        advAtEntry <- currentAdv ()
        volAtEntry <- volMa.SampleStd
        stopPriceAtEntry <- stopLevel
        priceDeclineAtEntry <- declineAtFire

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
            // Negative for longs (decline) so quintile-bin script reads
            // abs() and bins by magnitude alongside the short engine.
            PriceRiseAtEntry = -priceDeclineAtEntry
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
        stopPriceAtEntry <- 0.0
        priceDeclineAtEntry <- 0.0

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

    let lowStopFill (bar: SignedBar) : float option =
        if cfg.StopLowWindowMinutes <= 0 then None
        elif side <> Long || stopPriceAtEntry <= 0.0 then None
        elif bar.VWAP <= stopPriceAtEntry then Some bar.Close
        else None

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
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        match lowStopFill bar with
        | Some fillPx -> closePos fillPx bar.EndUs
        | None ->
            match pctStopPriceIfHit bar with
            | Some stopPx -> closePos stopPx bar.EndUs
            | None -> ()

        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close
        let totalDv = bar.BuyDollarVolume + bar.SellDollarVolume
        advMa.Push totalDv
        closeRecent.Push bar.Close
        closeLag.Push    bar.Close
        coverSum.Push    bar.Close
        cvdMa.Push (bar.BuyDollarVolume - bar.SellDollarVolume)
        volEntry.Push    bar.Volume
        volBaseline.Push bar.Volume
        lowMinMa.Push bar.Low
        low8hMinMa.Push bar.Low

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        let windowsReady =
            barsSeen             >= warmupBars
            && closeRecent.Count >= nLagBig
            && closeLag.Count    >= nLag
            && coverSum.Count    >= nCoverMa
            && cvdMa.Count       >= nCvd
            && lowMinMa.Count    >= nStopLow

        let cvd = cvdMa.State

        if windowsReady && side = Flat && cfg.AllowLong then
            // Cover MA is the single reference for both the decline gate
            // and the cover-on-touch exit. priceDecline is "% below the
            // very level the trade is targeting" — no stale reference
            // problem, no possibility of firing on bars where the cover
            // MA already sits at or below entry.
            let coverMa =
                if coverSum.Count > 0
                then coverSum.State / float coverSum.Count
                else 0.0
            let priceRatio =
                if coverMa > 0.0 then bar.Close / coverMa else 0.0
            let priceDecline = 1.0 - priceRatio

            let advGate = cfg.MinLongAdv <= 0.0 || currentAdv () >= cfg.MinLongAdv
            let cvdCross = prevCvd <= 0.0 && cvd > 0.0

            let stopLevel = lowMinMa.State
            let stopBelowClose = stopLevel > 0.0 && stopLevel < bar.Close

            // Reward:risk gate. Risk = entry close - stop level (the 20m
            // min-Low). Reward = cover MA - entry close (room to revert up
            // to the trailing-N-hour mean). Reject unless reward/risk >=
            // MinRewardRiskRatio. Default 1.0 — never take a trade whose
            // upside doesn't at least match its downside.
            let rewardRiskOk =
                if cfg.MinRewardRiskRatio <= 0.0 then true
                elif coverMa <= 0.0 || stopLevel <= 0.0 then false
                else
                    let risk = bar.Close - stopLevel
                    let reward = coverMa - bar.Close
                    risk > 0.0 && reward / risk >= cfg.MinRewardRiskRatio

            let distanceGate =
                if cfg.EntryDistanceMaxPct <= 0.0 then true
                else
                    let refLow =
                        match cfg.EntryDistanceRef with
                        | Stop20m -> stopLevel
                        | Low8h   -> low8hMinMa.State
                    if refLow <= 0.0 then false
                    else
                        let distance = (bar.Close - refLow) / refLow
                        distance <= cfg.EntryDistanceMaxPct

            // Rvol at entry — same-horizon as CvdMinutes / 30d baseline.
            // Surfaced on the trip record as RatioAtEntry and gated by
            // cfg.RvolEntryThreshold (default 0.75 = filter Q1 of the
            // v10c long-fade quintile breakdown).
            let rvolAtEntry =
                let entryMean = volEntry.State / float nRvolEntry
                let baselineMean =
                    if volBaseline.Count > 0
                    then volBaseline.State / float volBaseline.Count
                    else 0.0
                if baselineMean > 0.0 then entryMean / baselineMean else 0.0
            let rvolGate = cfg.RvolEntryThreshold <= 0.0 || rvolAtEntry >= cfg.RvolEntryThreshold

            if priceDecline >= cfg.PriceDeclineThreshold
               && cvdCross
               && advGate
               && stopBelowClose
               && rewardRiskOk
               && distanceGate
               && rvolGate then
                openLong bar.Close bar.EndUs stopLevel priceDecline rvolAtEntry

        elif windowsReady && side = Long then
            let timeStopHit =
                if cfg.TimeStopMinutes <= 0 then false
                else
                    let nTimeStop = minutesToBars cfg.TimeStopMinutes
                    if barsHeld < nTimeStop then false
                    else
                        match cfg.TimeStopMode with
                        | Hard -> true
                        | Conditional -> bar.Close <= entryPrice

            if timeStopHit then
                closePos bar.Close bar.EndUs
            else
                // Cover: bar.Close >= trailing-N-hour MA of close.
                let coverMa =
                    if coverSum.Count > 0
                    then coverSum.State / float coverSum.Count
                    else 0.0
                if coverMa > 0.0 && bar.Close >= coverMa then
                    closePos bar.Close bar.EndUs

        prevCvd <- cvd

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: LongFadeMAConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
