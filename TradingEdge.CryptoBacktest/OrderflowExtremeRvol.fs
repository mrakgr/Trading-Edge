module TradingEdge.CryptoBacktest.OrderflowExtremeRvol

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Extreme-rvol short engine (chart-review-derived)
// =============================================================================
//
// Targets the >=10x rvol short bucket where the cumsum-z system underperforms
// (PF 1.78 even in raw-z mode). Built from the visual pattern observed during
// the 175-trade chart review: large price expansion on a sudden burst of
// volume, fade short when 1h CVD turns negative, cover when activity
// normalises.
//
// Two rvol frames in parallel (both share a 30d-mean denominator). The
// entry numerator is short (default 1h) so a single hot hour fires the
// gate; the cover numerator (default 75m) and the CVD trigger window
// (default 75m) are kept the same length on purpose so the cover is
// logically consistent with the trigger that opened the trade.
//   rvolEntry = trailing 1h-mean volume / 30d-mean        (entry)
//   rvolExit  = trailing 75m-mean volume / 30d-mean       (cover)
//
// Other per-bar signals:
//   priceRise  = bar.Close / laggedMean
//   laggedMean = 8h-MA-of-close lagged 16h, derived from two trailing MAs:
//                  Σ24h.Close − Σ16h.Close
//                  ─────────────────────── = 8h sum 16h ago / 8h bars
//                     n24h − n16h
//                (no new primitive needed; one subtraction per bar.)
//   cvd        = trailing-75m CVD = Σ_{75m}(buy_dv − sell_dv)
//
// Entry — ALL must hold simultaneously:
//   - flat
//   - cfg.AllowShort  (this engine is short-only by design)
//   - rvolEntry   >= cfg.RvolEntryThreshold     (default 24.0; 1h-mean gate, rvol Q5 floor)
//   - priceRise   >= 1 + cfg.PriceRiseThreshold (default 1.10)
//   - prevCvd     >= 0  AND  cvd < 0            (75m CVD just crossed)
//   - ADV gate    (cfg.MinShortAdv)
//   - distance gate (when cfg.EntryDistanceMaxPct > 0): bar.Close must be
//     within EntryDistanceMaxPct of the reference high (Stop20m or
//     High8h per cfg.EntryDistanceRef). Filters out late entries where
//     the flush has already started.
//   - 200h warm-up: barsSeen >= 200h, and the short rolling windows
//     (rvol numerators, lagged-MA, CVD, high-stop) are all full. The 30d
//     baseline window is NOT required to be full — we divide by
//     volBaseline.Count and let recently-listed symbols trade against
//     whatever baseline they've accumulated, same convention as
//     volume_momentum_stratify.py.
//
// Stop — trailing-N-minute MaxMa of bar.High, snapshotted at entry and
// held fixed for the trade (default 20m). The level is set off bar.High
// (so it captures the actual blowoff peak) but the trigger fires off
// bar.VWAP (so wicks alone don't fire it — most of the bar's trading has
// to clear the level). When triggered, we fill at bar.Close. Snapshot-
// at-entry means the stop doesn't drift up if the symbol keeps melting
// up after we're in.
//
// Cover — rvolExit < cfg.RvolExitThreshold (default 2.0). On the default
// 75m cover window, this fires as soon as a single 75-minute slice
// normalises below 2× baseline.
//
// Time-stop (optional) — when cfg.TimeStopMinutes > 0, close the trade
// after that many minutes from entry. TimeStopMode = Hard closes
// unconditionally; Conditional closes only if the position isn't
// profitable. Disabled by default. Useful for cutting trades that wander
// sideways without ever showing edge.

/// Time-stop semantics. `Hard`: close unconditionally at the timer.
/// `Conditional`: close only if the trade isn't currently profitable
/// (bar.Close >= entryPrice for a short, so the position is at or above
/// breakeven). Only active when TimeStopMinutes > 0 in the config.
type TimeStopMode =
    | Hard
    | Conditional

/// Reference surface for the entry-distance gate. The gate rejects entries
/// where bar.Close has already drifted too far below this surface — the
/// idea is to short *near* the top, not after the flush has begun.
///   Stop20m  — the 20m trailing max-High (same surface as the stop). Tight:
///              the move must have peaked in the last 20 minutes.
///   High8h   — a separate trailing 8h max-High. Looser: the actual blowoff
///              peak just has to have happened recently in 8h.
type EntryDistanceRef =
    | Stop20m
    | High8h

type ExtremeRvolConfig = {
    /// Entry rvol gate: trailing-EntryRvolHours-mean / LookbackDays-mean
    /// volume ratio. Default 24.0 — the rvol-Q5 floor from the v6
    /// quintile sweep. Below ~24× the system has no edge; above it the
    /// PF jumps from 1.11 to 1.28. Designed to fire fast on a sudden
    /// burst of activity; the EntryRvolHours window is 1h by default.
    RvolEntryThreshold: float
    /// Cover gate: trailing-ExitRvolMinutes-mean / LookbackDays-mean ratio.
    /// Default 2.0. Cover when activity has normalised. The default
    /// ExitRvolMinutes window is 75m — kept identical to CvdMinutes so
    /// the cover frame is logically consistent with the trigger that
    /// opened the trade.
    RvolExitThreshold: float
    /// Fractional price-rise gate against the LagMA-lagged price. Default
    /// 0.10 → entry requires bar.Close >= 1.10 × laggedMean.
    PriceRiseThreshold: float
    /// Entry rvol numerator window (hours). Default 1. Short window so a
    /// single hot hour fires the gate before the 8h frame catches up.
    EntryRvolHours: int
    /// Exit / cover rvol numerator window (minutes). Default 75. Same
    /// length as CvdMinutes so cover and trigger are over the same
    /// horizon — book as soon as a single 75m slice drops below
    /// RvolExitThreshold × baseline.
    ExitRvolMinutes: int
    /// Baseline-window length (days) for both rvol denominators.
    /// Default 30.
    LookbackDays: int
    /// Recent-window length (hours) for the lagged-MA derivation.
    /// Default 8 → 8h-MA-lagged-LagHours.
    RecentHours: int
    /// Lag (hours) for the price-reference MA. Default 16 → 24h-MA,
    /// 16h-MA, derived 8h-MA-lagged-16h.
    LagHours: int
    /// Trailing-CVD window (minutes) for the entry trigger. Default 75.
    /// Same length as ExitRvolMinutes so the trigger and the cover MA
    /// agree on horizon.
    CvdMinutes: int
    /// High-stop window (minutes): trailing-N MaxMa of bar.High,
    /// snapshotted at entry and held fixed for the trade. Default 20.
    /// Tight, reactive band sitting on the bar high (not VWAP) — suits
    /// the fast-fader thesis where we want out promptly if the
    /// blowoff prints a fresh high after entry.
    StopHighWindowMinutes: int
    /// Time-stop window (minutes). When > 0, close the trade after this
    /// many minutes per the TimeStopMode. Default 0 (disabled).
    TimeStopMinutes: int
    /// Time-stop semantics. `Hard`: close unconditionally at the timer.
    /// `Conditional`: close only if the trade isn't currently profitable
    /// (bar.Close >= entryPrice for a short). Conditional gives winners
    /// room to run. Only active when TimeStopMinutes > 0.
    TimeStopMode: TimeStopMode
    /// Entry-distance gate: max fractional distance from EntryDistanceRef
    /// to bar.Close at entry. Default 0 (disabled). Reject entry when
    /// (referenceHigh - bar.Close) / referenceHigh > EntryDistanceMaxPct.
    /// Filters out late entries where the flush has already started.
    /// Typical sweep range 0.05–0.20.
    EntryDistanceMaxPct: float
    /// Which surface to measure entry distance against. See
    /// EntryDistanceRef. Default Stop20m.
    EntryDistanceRef: EntryDistanceRef
    /// Re-entry cooldown (minutes) after a time-stop only. Default 0
    /// (disabled). When > 0, reject new entries until this many minutes
    /// have elapsed since the most recent time-stop on this symbol.
    /// High-stops and cover-on-vol-normalize do NOT engage the cooldown
    /// — only time-stops, which fire when a trade went sideways without
    /// showing edge (so re-shorting the same regime is doubling down on
    /// a failed read).
    ReentryTimeoutMinutes: int
    Notional: float
    TakerFee: float
    /// Always true for this engine; flag exists for parity with the
    /// other engines' configs and to make the short-only contract
    /// explicit at the cell level.
    AllowShort: bool
    BucketUs: int64
    MaxAdverseFraction: float
    ReferenceVol: float
    MinShortAdv: float
    VolWindowDays: int
    MaxBarPriceRatio: float
}

let defaultExtremeRvolConfig () : ExtremeRvolConfig =
    { RvolEntryThreshold = 24.0
      RvolExitThreshold = 2.0
      PriceRiseThreshold = 0.10
      EntryRvolHours = 1
      ExitRvolMinutes = 75
      LookbackDays = 30
      RecentHours = 8
      LagHours = 16
      CvdMinutes = 75
      StopHighWindowMinutes = 20
      TimeStopMinutes = 90
      TimeStopMode = Conditional
      EntryDistanceMaxPct = 0.20
      EntryDistanceRef = High8h
      ReentryTimeoutMinutes = 0
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

type Engine(cfg: ExtremeRvolConfig) =
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

    // Window sizes in bars.
    let nEntryRvol = hoursToBars   (max 1 cfg.EntryRvolHours)               // 1h (entry numerator)
    let nExitRvol  = minutesToBars (max 1 cfg.ExitRvolMinutes)              // 75m (cover numerator)
    let nLag       = hoursToBars   (max 1 cfg.LagHours)                     // 16h
    let nLagBig    = hoursToBars   (max 1 (cfg.RecentHours + cfg.LagHours)) // 24h
    let nCvd       = minutesToBars (max 1 cfg.CvdMinutes)                   // 75m
    let nBaseline  = daysToBars (max 1 cfg.LookbackDays)        // 30d (cap)
    let nStopHigh  = minutesToBars (max 1 cfg.StopHighWindowMinutes)   // 20m
    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    // Warm-up floor: a symbol must have seen at least this many bars
    // before any signal can fire, even though the baseline rolling window
    // has capacity for 30d. Same convention as volume_momentum_stratify.py
    // ("for symbols with <lookback-days of bars at entry, use whatever's
    // available"). Set to 200h to match the regime-window scale used
    // elsewhere in the project.
    let warmupBars = hoursToBars 200

    // Volume rolling state. Two numerators (1h for entry, 75m for cover)
    // share the same 30d denominator.
    let volEntry    = SumMa(nEntryRvol)
    let volExit     = SumMa(nExitRvol)
    let volBaseline = SumMa(nBaseline)
    // Close-price rolling sums for the lagged-MA derivation.
    let closeRecent = SumMa(nLagBig)   // 24h
    let closeLag    = SumMa(nLag)      // 16h
    // 1h CVD trigger.
    let cvdMa = SumMa(nCvd)
    // ADV / vol-stat trackers (parity with CumsumZ).
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)
    // Trailing 20m max-High for the entry stop. Snapshotted at entry and
    // held fixed for the trade.
    let highMaxMa = MaxMa(nStopHigh)
    // Trailing 8h max-High used by the optional entry-distance gate when
    // EntryDistanceRef = High8h. Allocated unconditionally — it's a
    // single monotonic-deque with amortized O(1) per-bar updates, cheap.
    let nHigh8h     = hoursToBars 8
    let high8hMaxMa = MaxMa(nHigh8h)

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
    // Snapshot of trailing-8h max-VWAP at entry, held fixed for the trade.
    let mutable stopPriceAtEntry = 0.0
    // Realized price-rise at entry (bar.Close / 8h-MA-lagged-16h - 1.0).
    // Captured at fire time and surfaced on the round-trip so the
    // quintile-binning analysis can stratify trades by the actual rise
    // they fired on, independent of the firing threshold.
    let mutable priceRiseAtEntry = 0.0

    let mutable prevCvd = 0.0
    let mutable lastClose = 0.0
    let mutable lastUs = 0L
    /// End-of-bar timestamp of the most recent time-stop close. 0L when
    /// none has fired yet. Used by the re-entry cooldown to suppress new
    /// entries within ReentryTimeoutMinutes of the last time-stop.
    let mutable lastTimeStopUs = 0L

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

    let openShort (fillPrice: float) (fillUs: int64) (rvolAtFire: float) (stopLevel: float) (priceRiseAtFire: float) =
        side <- Short
        entryPrice <- fillPrice
        entryUs <- fillUs
        effectiveNotional <- computeEffectiveNotional ()
        barsHeld <- 1
        mfeUsd <- 0.0
        maeUsd <- 0.0
        ratioAtEntry <- rvolAtFire
        fundingPnl <- 0.0
        advAtEntry <- currentAdv ()
        volAtEntry <- volMa.SampleStd
        stopPriceAtEntry <- stopLevel
        priceRiseAtEntry <- priceRiseAtFire

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

    // Per-bar percentage stop, parity with CumsumZ. Disabled by default.
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

    // VWAP-vs-stop trigger: fires when the bar's VWAP (volume-weighted
    // average price) prints at or above the entry-time trailing max-high.
    // Wicks alone don't trigger — most of the bar's trading has to clear
    // the level. Fill at bar.Close: by end-of-bar we've decided the level
    // was breached (VWAP exceeded stop), and we exit at the next available
    // print, which is the bar's close. Consistent with how the cover-on-
    // vol-normalize and persistence-exit paths fill.
    let highStopFill (bar: SignedBar) : float option =
        if side <> Short || stopPriceAtEntry <= 0.0 then None
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
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        // Stop check: high-stop first (engine-defining), then optional
        // percentage-stop for parity. Both are leak-free since they read
        // state from the prior bar's pushes only.
        match highStopFill bar with
        | Some fillPx -> closePos fillPx bar.EndUs
        | None ->
            match pctStopPriceIfHit bar with
            | Some stopPx -> closePos stopPx bar.EndUs
            | None -> ()

        // Update rolling state from this bar BEFORE the signal evaluation
        // below uses it. NB: highMaxMa is also pushed BEFORE entry — at
        // entry we want the snapshot of the trailing-N max INCLUDING
        // this bar's high, since that's "the highest level the market
        // has just touched in the run-up". The same-bar guard below
        // (stopAboveClose) handles the case where this very bar set
        // the trailing max.
        if prevClose > 0.0 && bar.Close > 0.0 then
            volMa.Push (log (bar.Close / prevClose))
        prevClose <- bar.Close
        let totalDv = bar.BuyDollarVolume + bar.SellDollarVolume
        advMa.Push totalDv
        volEntry.Push    bar.Volume
        volExit.Push     bar.Volume
        volBaseline.Push bar.Volume
        closeRecent.Push bar.Close
        closeLag.Push    bar.Close
        cvdMa.Push (bar.BuyDollarVolume - bar.SellDollarVolume)
        highMaxMa.Push bar.High
        high8hMaxMa.Push bar.High

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        // Compute live signals. Fire only once the engine has at least
        // `warmupBars` (200h) of history AND every short window is full.
        // The baseline window may not yet be full at 30d (e.g. for a
        // recently-listed symbol) — in that case we use whatever's been
        // accumulated, dividing by volBaseline.Count to get a fair mean
        // over the available history. After 30d the window is full and
        // the divisor stabilises at nBaseline. Same convention as
        // volume_momentum_stratify.py.
        let windowsReady =
            barsSeen             >= warmupBars
            && volEntry.Count    >= nEntryRvol
            && volExit.Count     >= nExitRvol
            && closeRecent.Count >= nLagBig
            && closeLag.Count    >= nLag
            && cvdMa.Count       >= nCvd
            && highMaxMa.Count   >= nStopHigh

        let cvd = cvdMa.State

        // Baseline mean is shared between the entry and cover gates. Uses
        // volBaseline.Count (not nBaseline) so a recently-listed symbol
        // with <30d of history can trade against whatever's accumulated.
        let baselineMean () =
            if volBaseline.Count > 0
            then volBaseline.State / float volBaseline.Count
            else 0.0

        if windowsReady && side = Flat && cfg.AllowShort then
            // Entry rvol: 1h-mean / baseline-mean.
            let entryMean = volEntry.State / float nEntryRvol
            let bMean = baselineMean ()
            let rvolEntry =
                if bMean > 0.0 then entryMean / bMean else 0.0

            // 8h-MA-lagged-16h close: (Σ24h − Σ16h) / (n24h − n16h)
            let lagSum  = closeRecent.State - closeLag.State
            let lagBars = float (nLagBig - nLag)
            let laggedMean = if lagBars > 0.0 then lagSum / lagBars else 0.0
            let priceRise =
                if laggedMean > 0.0 then bar.Close / laggedMean else 0.0

            let advGate = cfg.MinShortAdv <= 0.0 || currentAdv () >= cfg.MinShortAdv
            let cvdCross = prevCvd >= 0.0 && cvd < 0.0

            // Defer the entry when the candidate stop level equals the
            // entry close. This happens on bars that print a new
            // trailing-N MaxHigh — the bar itself sets the level it would
            // be stopped against, so any subsequent bar with bar.High >=
            // entry.Close fires the stop at the same level we filled at
            // (guaranteed loser of fees). Wait one bar: the trailing max
            // rolls forward and the stop sits strictly above close.
            let stopLevel = highMaxMa.State
            let stopAboveClose = stopLevel > bar.Close

            // Entry-distance gate: reject entries where bar.Close has
            // already drifted too far below the reference high. The
            // edge is in fading near the peak; once the flush has
            // started we're chasing. Disabled when threshold is 0.
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

            // Re-entry cooldown after a time-stop. Time-stops fire when a
            // trade went sideways without showing edge — re-shorting the
            // same regime within the cooldown is doubling down on a
            // failed read. High-stops and cover-on-vol-normalize do NOT
            // engage the cooldown.
            let cooldownGate =
                if cfg.ReentryTimeoutMinutes <= 0 || lastTimeStopUs = 0L then true
                else
                    let cooldownUs = int64 cfg.ReentryTimeoutMinutes * 60_000_000L
                    bar.EndUs - lastTimeStopUs >= cooldownUs

            if rvolEntry   >= cfg.RvolEntryThreshold
               && priceRise >= 1.0 + cfg.PriceRiseThreshold
               && cvdCross
               && advGate
               && stopAboveClose
               && distanceGate
               && cooldownGate then
                openShort bar.Close bar.EndUs rvolEntry stopLevel (priceRise - 1.0)

        // Exit checks while holding a short. Two independent triggers:
        //   1. Time-stop (when configured): close after N minutes per mode.
        //      Hard fires unconditionally; Conditional fires only when not
        //      profitable (bar.Close >= entryPrice).
        //   2. Cover-on-vol-normalize: rvolExit < threshold. Default 1h-mean /
        //      baseline-mean; book as soon as a single hour drops below 2×.
        // Time-stop wins precedence: if both conditions are true on the same
        // bar, the time-stop fires first. Either way fill is bar.Close.
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
                lastTimeStopUs <- bar.EndUs
            else
                let exitMean = volExit.State / float nExitRvol
                let bMean = baselineMean ()
                let rvolExit =
                    if bMean > 0.0 then exitMean / bMean else 0.0
                if rvolExit < cfg.RvolExitThreshold then
                    closePos bar.Close bar.EndUs

        prevCvd <- cvd

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: ExtremeRvolConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
