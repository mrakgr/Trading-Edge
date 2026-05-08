module TradingEdge.CryptoBacktest.OrderflowShortFadeMA

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Short fade engine — mean-reversion to a time-based MA (mirror of LongFadeMA)
// =============================================================================
//
// Symmetric mirror of OrderflowLongFadeMA:
//   * price RISE relative to the cover MA (instead of decline)
//   * CVD turning NEGATIVE (instead of positive) as the entry trigger
//   * cover when bar.Close <= coverMa (instead of >=)
//   * entry-distance gate measured against trailing High8h (instead of Low8h)
//
// Per-bar signals:
//   priceRise   = bar.Close / coverMa - 1                (positive when up)
//   coverMa     = trailing CoverMaHours MA of bar.Close. Single reference for
//                 BOTH the rise gate and the cover-on-touch exit. Same logic as
//                 the long mirror: rise is "% above the very target the trade
//                 is reverting to" — eliminates the stale-reference problem.
//   cvd         = trailing-CvdMinutes CVD = Σ_{Nm}(buy_dv − sell_dv)
//
// Entry — ALL must hold simultaneously:
//   - flat
//   - cfg.AllowShort
//   - priceRise >= cfg.PriceRiseThreshold     (default 0.05; close >= (1 + th) * coverMa)
//   - prevCvd >= 0 AND cvd < 0                (CVD just turned negative)
//   - ADV gate (cfg.MinShortAdv)
//   - distance gate: bar.Close within EntryDistanceMaxPct ABOVE the
//     reference high (Stop20m or High8h). Filters out late entries where
//     the rollover has already started.
//   - 200h warm-up: barsSeen >= 200h AND every short window full.
//
// Stop — trailing-N-minute MaxMa of bar.High, snapshotted at entry and
// held fixed for the trade (default 0 = disabled). Trigger: bar.VWAP >= stop;
// fill at bar.Close.
//
// Cover — bar.Close <= coverMa. Fires the bar after the close prints
// at or below the trailing-N-hour MA; fill at bar.Close. No VWAP filter
// here — close-vs-MA is the cleanest "we got our reversion" check.
//
// Time-stop (optional) — when cfg.TimeStopMinutes > 0, close after that
// many minutes per cfg.TimeStopMode. Hard closes unconditionally;
// Conditional closes only if the trade isn't profitable
// (bar.Close >= entryPrice for a short). Time-stop takes precedence over
// the MA cover.

type TimeStopMode =
    | Hard
    | Conditional

type EntryDistanceRef =
    | Stop20m
    | High8h

type ShortFadeMAConfig = {
    /// Fractional price-rise gate against the cover MA.
    /// Default 0.05 → entry requires bar.Close >= 1.05 × coverMa.
    /// Same MA is used for the gate and the exit (single-reference design).
    PriceRiseThreshold: float
    /// Cover MA window in HOURS. Default 72. Cover when bar.Close <=
    /// trailing-N-hour mean of close. Time-based, not volume-based.
    CoverMaHours: int
    /// Recent-window length (hours) for legacy lagged-MA derivation.
    /// Default 8 → 8h-MA-lagged-LagHours (kept for parity / sweepability).
    RecentHours: int
    /// Lag (hours) for the legacy lagged-MA. Default 16.
    LagHours: int
    /// Trailing-CVD window (minutes) for the entry trigger. Default 240
    /// (4h, mirror of the long-side default).
    CvdMinutes: int
    /// Long-horizon CVD window (minutes) — risk management overlay.
    /// Default 12000 (200h). Entry requires the long CVD to ALSO be
    /// negative (sustained selling pressure across a multi-day window,
    /// not just the 4h trigger). Cover when BOTH the trigger CVD and
    /// the long CVD are non-negative — the regime has flipped. Pass 0
    /// to disable (falls back to the MA-touch cover).
    LongCvdMinutes: int
    /// High-stop window (minutes): trailing-N MaxMa of bar.High,
    /// snapshotted at entry and held fixed for the trade. Default 0 to
    /// disable the stop entirely (the trade only exits via MA-touch,
    /// time-stop, or price-gap). The rolling MaxHigh state is still
    /// maintained for the entry-distance gate (EntryDistanceRef = Stop20m)
    /// and the RR gate; only the stop-fill check is short-circuited.
    StopHighWindowMinutes: int
    /// Time-stop window (minutes). Default 0 (disabled). Pass >0 to enable.
    TimeStopMinutes: int
    /// Time-stop semantics. Only active when TimeStopMinutes > 0.
    TimeStopMode: TimeStopMode
    /// Entry-distance gate: max fractional distance from EntryDistanceRef
    /// to bar.Close at entry. Default 0.20 (carried over from the long
    /// mirror; will be re-validated by the v10d-style sweeps).
    EntryDistanceMaxPct: float
    /// Which surface to measure entry distance against. Default High8h.
    EntryDistanceRef: EntryDistanceRef
    /// Minimum reward:risk ratio at entry. Risk = stopLevel - bar.Close
    /// (rise from entry to the 20m max-High stop). Reward = bar.Close -
    /// coverMa (room to mean-revert down to the cover MA). Reject the
    /// trade unless reward / risk >= this. Default 0.0 (disabled). The
    /// fixed-reference design makes this redundant since pr > 0 already
    /// guarantees coverMa < close.
    MinRewardRiskRatio: float
    /// Minimum rvol at entry (computed over the CvdMinutes horizon).
    /// Reject trades with `rvolEntry < this`. Default 3.0 — highest-PF
    /// cell at the 3k-trip volume floor in the 7×7 rvol×pr sweep
    /// (rvol≥3 / pr=0.05 → PF 1.98, +$128,248 over 3,306 trips).
    /// Pass 0 to disable.
    RvolEntryThreshold: float
    Notional: float
    TakerFee: float
    /// Always true for this engine; flag exists for parity.
    AllowShort: bool
    BucketUs: int64
    MaxAdverseFraction: float
    ReferenceVol: float
    MinShortAdv: float
    VolWindowDays: int
    MaxBarPriceRatio: float
}

let defaultShortFadeMAConfig () : ShortFadeMAConfig =
    { PriceRiseThreshold = 0.05
      CoverMaHours = 72
      RecentHours = 8
      LagHours = 16
      CvdMinutes = 240
      LongCvdMinutes = 12_000
      StopHighWindowMinutes = 0
      TimeStopMinutes = 0
      TimeStopMode = Conditional
      EntryDistanceMaxPct = 0.20
      EntryDistanceRef = High8h
      MinRewardRiskRatio = 0.0
      RvolEntryThreshold = 3.0
      Notional = 1000.0
      TakerFee = 0.0004
      AllowShort = true
      BucketUs = 0L
      MaxAdverseFraction = 0.0
      ReferenceVol = 0.0
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

type Engine(cfg: ShortFadeMAConfig) =
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
    let nLongCvd   = minutesToBars (max 1 cfg.LongCvdMinutes)
    let longCvdEnabled = cfg.LongCvdMinutes > 0
    let nCoverMa   = hoursToBars   (max 1 cfg.CoverMaHours)
    let nStopHigh  = minutesToBars (max 1 cfg.StopHighWindowMinutes)
    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let warmupBars = hoursToBars 200
    // Observational rvol — surfaced on the trip record as RatioAtEntry
    // for downstream analysis. Same 30d baseline / CvdMinutes numerator
    // as the long mirror.
    let nRvolEntry = nCvd
    let nRvolBase  = daysToBars 30

    // Close-price rolling sums for the lagged-MA derivation.
    let closeRecent = SumMa(nLagBig)
    let closeLag    = SumMa(nLag)
    // Cover MA — trailing-N-hour mean of bar.Close.
    let coverSum = SumMa(nCoverMa)
    // CVD trigger (mirror of the long fade — short enters on negative cross).
    let cvdMa = SumMa(nCvd)
    // Long-horizon CVD — risk-management overlay. Both the trigger CVD and
    // this long CVD must be negative at entry; cover when both have flipped
    // to non-negative. When LongCvdMinutes <= 0, longCvdMa is still kept in
    // sync but the gate / cover terms collapse to no-ops.
    let longCvdMa = SumMa(nLongCvd)
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)
    let highMaxMa = MaxMa(nStopHigh)
    let nHigh8h    = hoursToBars 8
    let high8hMaxMa = MaxMa(nHigh8h)
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
    let mutable priceRiseAtEntry = 0.0

    let mutable prevCvd = 0.0
    let mutable lastClose = 0.0
    let mutable lastUs = 0L
    // Set to (barsSeen + warmupBars) after a price-gap close. Entries are
    // suppressed until barsSeen catches up: rolling state populated before
    // the gap (cover MA, lagged-MA components, CVD, MaxHigh) is stale
    // relative to the post-gap regime.
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

    let openShort (fillPrice: float) (fillUs: int64) (stopLevel: float) (riseAtFire: float) (rvolAtFire: float) =
        side <- Short
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
        priceRiseAtEntry <- riseAtFire

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
            // Positive for shorts (rise) so quintile-bin script reads
            // abs() and bins by magnitude alongside the long engine.
            PriceRiseAtEntry = priceRiseAtEntry
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
        priceRiseAtEntry <- 0.0

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

    let highStopFill (bar: SignedBar) : float option =
        if cfg.StopHighWindowMinutes <= 0 then None
        elif side <> Short || stopPriceAtEntry <= 0.0 then None
        elif bar.VWAP >= stopPriceAtEntry then Some bar.Close
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
            signalLockoutUntil <- barsSeen + warmupBars
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        match highStopFill bar with
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
        longCvdMa.Push (bar.BuyDollarVolume - bar.SellDollarVolume)
        volEntry.Push    bar.Volume
        volBaseline.Push bar.Volume
        highMaxMa.Push bar.High
        high8hMaxMa.Push bar.High

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        let windowsReady =
            barsSeen             >= warmupBars
            && barsSeen          >= signalLockoutUntil
            && closeRecent.Count >= nLagBig
            && closeLag.Count    >= nLag
            && coverSum.Count    >= nCoverMa
            && cvdMa.Count       >= nCvd
            && highMaxMa.Count   >= nStopHigh
            && (not longCvdEnabled || longCvdMa.Count >= nLongCvd)

        let cvd = cvdMa.State
        let longCvd = longCvdMa.State

        if windowsReady && side = Flat && cfg.AllowShort then
            // Cover MA is the single reference for both the rise gate and
            // the cover-on-touch exit. priceRise is "% above the very level
            // the trade is targeting" — no stale-reference problem, no
            // possibility of firing on bars where the cover MA already sits
            // at or above entry.
            let coverMa =
                if coverSum.Count > 0
                then coverSum.State / float coverSum.Count
                else 0.0
            let priceRatio =
                if coverMa > 0.0 then bar.Close / coverMa else 0.0
            let priceRise = priceRatio - 1.0

            let advGate = cfg.MinShortAdv <= 0.0 || currentAdv () >= cfg.MinShortAdv
            let cvdCross = prevCvd >= 0.0 && cvd < 0.0
            // Risk-management overlay: long-horizon CVD must agree the
            // tape is net-selling. Without this gate, the 4h cross can
            // fire during routine pullbacks inside an uptrend; with it,
            // shorts only fire when the regime itself is bearish.
            let longCvdGate = (not longCvdEnabled) || longCvd < 0.0

            let stopLevel = highMaxMa.State
            let stopAboveClose = stopLevel > 0.0 && stopLevel > bar.Close

            // Reward:risk gate. Risk = stop level - entry close (the 20m
            // max-High). Reward = entry close - cover MA (room to revert
            // down to the trailing-N-hour mean). Reject unless
            // reward/risk >= MinRewardRiskRatio. Default 0.0 (disabled).
            let rewardRiskOk =
                if cfg.MinRewardRiskRatio <= 0.0 then true
                elif coverMa <= 0.0 || stopLevel <= 0.0 then false
                else
                    let risk = stopLevel - bar.Close
                    let reward = bar.Close - coverMa
                    risk > 0.0 && reward / risk >= cfg.MinRewardRiskRatio

            let distanceGate =
                if cfg.EntryDistanceMaxPct <= 0.0 then true
                else
                    let refHigh =
                        match cfg.EntryDistanceRef with
                        | Stop20m -> stopLevel
                        | High8h  -> high8hMaxMa.State
                    if refHigh <= 0.0 then false
                    else
                        let distance = (refHigh - bar.Close) / refHigh
                        distance <= cfg.EntryDistanceMaxPct

            // Rvol at entry — same-horizon as CvdMinutes / 30d baseline.
            // Surfaced on the trip record as RatioAtEntry and gated by
            // cfg.RvolEntryThreshold (default 3.0 — highest-PF cell at 3k-trip floor in 7x7 rvol×pr sweep).
            let rvolAtEntry =
                let entryMean = volEntry.State / float nRvolEntry
                let baselineMean =
                    if volBaseline.Count > 0
                    then volBaseline.State / float volBaseline.Count
                    else 0.0
                if baselineMean > 0.0 then entryMean / baselineMean else 0.0
            let rvolGate = cfg.RvolEntryThreshold <= 0.0 || rvolAtEntry >= cfg.RvolEntryThreshold

            if priceRise >= cfg.PriceRiseThreshold
               && cvdCross
               && longCvdGate
               && advGate
               && stopAboveClose
               && rewardRiskOk
               && distanceGate
               && rvolGate then
                openShort bar.Close bar.EndUs stopLevel priceRise rvolAtEntry

        elif windowsReady && side = Short then
            let timeStopHit =
                if cfg.TimeStopMinutes <= 0 then false
                else
                    let nTimeStop = minutesToBars cfg.TimeStopMinutes
                    if barsHeld < nTimeStop then false
                    else
                        match cfg.TimeStopMode with
                        | Hard -> true
                        | Conditional -> bar.Close >= entryPrice

            if timeStopHit then
                closePos bar.Close bar.EndUs
            elif longCvdEnabled then
                // Dual-CVD cover: when LongCvdMinutes > 0, the cover
                // is "both CVDs flipped non-negative" — the regime that
                // gated entry has cleared. The MA-touch cover is
                // bypassed entirely; this is intentional, since the
                // price-target cover failed catastrophically on shorts.
                if cvd >= 0.0 && longCvd >= 0.0 then
                    closePos bar.Close bar.EndUs
            else
                // Fallback: legacy MA-touch cover (only when LongCvd
                // disabled). bar.Close <= trailing-N-hour MA of close.
                let coverMa =
                    if coverSum.Count > 0
                    then coverSum.State / float coverSum.Count
                    else 0.0
                if coverMa > 0.0 && bar.Close <= coverMa then
                    closePos bar.Close bar.EndUs

        prevCvd <- cvd

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: ShortFadeMAConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
