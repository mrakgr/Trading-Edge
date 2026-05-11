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

/// Cover-rule discriminator.
///   OppositeChannel              — original v0: cover on re-violation of the
///                                  just-broken side (ratcheted trailing stop on
///                                  the prior-channel extreme).
///   EntryChannelTarget           — short-term reversion: cover when price touches
///                                  the prior 3-bar Donchian extreme on the
///                                  with-trend side. No stops. Used with
///                                  --reverse-direction to test continuation pullbacks.
///   EntryChannelTargetOnBreak    — v3: position runs unhedged after entry. When
///                                  the with-trend Donchian channel cracks (long:
///                                  bar.Low < prevDonLow; short: bar.High > prevDonHigh),
///                                  arm a trailing take-profit at the OPPOSITE
///                                  channel extreme. From that point on, behaves
///                                  identically to EntryChannelTarget.
/// Cover-rule discriminator (additional case).
///   MaCrossCover                 — v4/v5: cover when bar.Close crosses the 1h
///                                  MA from the favorable side (long: close >=
///                                  closeShortMa.mean; short: close <=).
///                                  No stops, no time cap. Trade runs until
///                                  the cross fires or end-of-stream Flush.
///                                  Designed to pair with the strict MA-side
///                                  entry filter (long below MA / short above).
type CoverMode =
    | OppositeChannel
    | EntryChannelTarget
    | EntryChannelTargetOnBreak
    | MaCrossCover

/// Entry-trigger discriminator.
///   BreakTrigger          — v0/v1: entry only on the bar that pierces the opposite
///                           channel after a sustained 30-bar trend run.
///   TrendOnly             — v2: entry on the first bar where the trend qualifier
///                           is satisfied (no break required). With-trend direction:
///                           uptrend → LONG, downtrend → SHORT (independent of
///                           ReverseDirection). Re-arms after each violation reset.
type EntryMode =
    | BreakTrigger
    | TrendOnly

/// Reason the round-trip exited. Tagged at closePos / Flush time so post-hoc
/// fill simulators can exclude degenerate exits.
///   Normal         — regular cover (MA-cross, OppositeChannel stop, take-profit,
///                    pct-stop). The trip represents a real tradeable signal.
///   Redenomination — exit forced by the MaxBarPriceRatio gap detector. The exit
///                    price is the LAST PRE-GAP bar close, but the symbol just
///                    redenominated (e.g. DYDXUSDT → 1000DYDXUSDT), so any
///                    "real" exit at the new price scale would be a different
///                    instrument. Exclude from fill sims.
///   EndOfStream    — position open when the bar stream ended. The exit is the
///                    last bar close, NOT a real cover signal. Exclude from
///                    fill sims (the exit window doesn't have actionable trades).
type ExitReason =
    | Normal
    | Redenomination
    | EndOfStream

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
    /// Sum of bar dollar-volume (BuyDollarVolume + SellDollarVolume, in USDT)
    /// over the trailing nShortRef bars ending the bar BEFORE entry. Pre-push
    /// state (does not include the entry bar itself). Pairs with
    /// TradeCount1hAtEntry to characterise actual liquidity in the 1h window
    /// leading into the trade — independent of the trailing-90d
    /// AvgDailyVolumeAtEntry which can be high while the immediate vicinity
    /// is illiquid.
    DollarVolume1hAtEntry: float
    /// Sum of bar.TradeCount over the trailing nShortRef bars ending the bar
    /// BEFORE entry. Recorded as float for CSV serialisation simplicity.
    TradeCount1hAtEntry: float
    /// Sum of bar dollar-volume (BuyDollarVolume + SellDollarVolume) over the
    /// FIXED 5-minute window ending the bar BEFORE entry. Surfaces the
    /// short-horizon liquidity at the entry bar for post-hoc fill-quality
    /// filtering. Independent of the configurable ShortRefMinutes so values
    /// remain comparable across MA-length configs.
    DollarVolume5mAtEntry: float
    /// Sum of bar.TradeCount over the trailing 5m window. Companion to
    /// DollarVolume5mAtEntry.
    TradeCount5mAtEntry: float
    ExitReason: ExitReason
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
    /// Cover rule. Default OppositeChannel (v0 thesis).
    /// EntryChannelTarget = short-term reversion: cover at the prior 3-bar
    /// Donchian extreme on the with-trend side. No stops.
    CoverMode: CoverMode
    /// When true, the entry sides are flipped: an uptrend break-down opens
    /// LONG (instead of SHORT), and a downtrend break-up opens SHORT.
    /// Used to test the continuation-pullback hypothesis. Default false.
    /// Ignored when EntryMode = TrendOnly (which always goes with-trend).
    ReverseDirection: bool
    /// Entry trigger. Default BreakTrigger (v0/v1 — fire only on the bar
    /// that breaks the opposite channel). TrendOnly fires on the first
    /// bar where the 30-bar trend qualifier is satisfied (no break
    /// required), with-trend (uptrend → LONG, downtrend → SHORT). Re-arms
    /// after each violation that resets the counter.
    EntryMode: EntryMode
    /// MA-side entry filter — pre-trade gate enforcing the trade is on
    /// the mean-reverting side of the 1h MA at entry. Default 0.0:
    ///   LONG entries fire only when pct_1h_change < MaxPct1hChangeForLong
    ///     (default 0.0 = entry bar's close STRICTLY below the 1h MA, room
    ///     to revert UP toward the MA). Strict inequality is required for
    ///     MaCrossCover: an entry placed exactly at the MA would otherwise
    ///     cover instantly.
    /// Pass +infinity to disable.
    MaxPct1hChangeForLong: float
    /// SHORT entries fire only when pct_1h_change > MinPct1hChangeForShort.
    /// Default 0.0. Pass -infinity to disable.
    MinPct1hChangeForShort: float
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
      CoverMode = OppositeChannel
      ReverseDirection = false
      EntryMode = BreakTrigger
      MaxPct1hChangeForLong = -0.004
      MinPct1hChangeForShort = 0.004
      Notional = 1000.0
      TakerFee = 0.0004
      BucketUs = 0L
      MaxAdverseFraction = 0.0
      // Production-safety defaults (see CLAUDE.md / memory note feedback_engine_default_flags.md):
      // vol-target sizing + ADV gate + gap detector must be on. Pass 0 to disable.
      ReferenceVol = 0.1019
      MinShortAdv = 5_000_000.0
      MinLongAdv = 5_000_000.0
      VolWindowDays = 90
      MaxBarPriceRatio = 3.0 }

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
    // Fixed 5-minute pre-entry instrumentation window — independent of the
    // configurable cover-MA length. Used to surface short-horizon liquidity at
    // the entry bar for post-hoc fill-quality filtering.
    let nInstrument5m = minutesToBars 5
    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let warmupBars = hoursToBars 200

    let donHighMa = MaxMa(nDon)
    let donLowMa  = MinMa(nDon)
    let closeShortMa = SumMa(nShortRef)
    let closeLongMa  = SumMa(nLongRef)
    let volShortMa = SumMa(nShortRef)
    let volLongMa  = SumMa(nLongRef)
    let tradeCountShortMa = SumMa(nShortRef)
    // 5-minute pre-entry instrumentation MAs — parallel to the configurable
    // short-window MAs above, fixed at 5 minutes for cross-config comparability.
    let vol5mMa = SumMa(nInstrument5m)
    let tradeCount5mMa = SumMa(nInstrument5m)
    let advMa = SumMa(advWindowBars)
    let volMa = StdMa(volWindowBars)


    // Violation counters — read & updated against the PREVIOUS bar's
    // Donchian state (rolling state before the current bar is pushed).
    let mutable barsSinceUpViolation   = 0
    let mutable barsSinceDownViolation = 0
    // TrendOnly arming: each side latches "armed" when its corresponding
    // counter resets to 0 (i.e. a fresh violation just happened) and
    // un-latches when an entry fires from that side. This guarantees at
    // most one TrendOnly entry per trend run, mirroring the once-per-break
    // behaviour of BreakTrigger mode.
    let mutable armedUptrendLong   = true
    let mutable armedDowntrendShort = true

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
    let mutable dollarVolume1hAtEntry = 0.0
    let mutable tradeCount1hAtEntry = 0.0
    let mutable dollarVolume5mAtEntry = 0.0
    let mutable tradeCount5mAtEntry = 0.0

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
        (volRatio: float)
        (dollarVolume1h: float)
        (tradeCount1h: float)
        (dollarVolume5m: float)
        (tradeCount5m: float) =
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
        dollarVolume1hAtEntry <- dollarVolume1h
        tradeCount1hAtEntry <- tradeCount1h
        dollarVolume5mAtEntry <- dollarVolume5m
        tradeCount5mAtEntry <- tradeCount5m

    let closePos (fillPrice: float) (fillUs: int64) (reason: ExitReason) =
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
            DollarVolume1hAtEntry = dollarVolume1hAtEntry
            TradeCount1hAtEntry = tradeCount1hAtEntry
            DollarVolume5mAtEntry = dollarVolume5mAtEntry
            TradeCount5mAtEntry = tradeCount5mAtEntry
            ExitReason = reason
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
        dollarVolume5mAtEntry <- 0.0
        tradeCount5mAtEntry <- 0.0
        priceRatio72hOver1hAtEntry <- 0.0
        volRatio1hOver72hAtEntry <- 0.0
        dollarVolume1hAtEntry <- 0.0
        tradeCount1hAtEntry <- 0.0

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
            closePos lastClose lastUs Redenomination
            signalLockoutUntil <- barsSeen + warmupBars
        if gapFired then () else

        if side <> Flat && barsHeld > 0 then
            updateExcursion bar
            barsHeld <- barsHeld + 1

        // Stop / cover precedence: pct-stop first, then mode-specific cover.
        //   OppositeChannel    : ratcheted trailing stop on the just-broken
        //                        side; cover when price re-violates it
        //                        (short: bar.High > stop; long: bar.Low < stop).
        //   EntryChannelTarget : fixed take-profit at the with-trend extreme
        //                        captured at entry. No stops.
        //                        Long target above entry → cover when bar.High >= target.
        //                        Short target below entry → cover when bar.Low <= target.
        match pctStopPriceIfHit bar with
        | Some stopPx -> closePos stopPx bar.EndUs Normal
        | None ->
            match cfg.CoverMode with
            | OppositeChannel ->
                if side = Short && trailingStop > 0.0 && bar.High > trailingStop then
                    closePos bar.Close bar.EndUs Normal
                elif side = Long && trailingStop > 0.0 && bar.Low < trailingStop then
                    closePos bar.Close bar.EndUs Normal
            | EntryChannelTarget | EntryChannelTargetOnBreak ->
                // trailingStop > 0 gates correctly for both cases. In
                // EntryChannelTarget it's set at entry; in
                // EntryChannelTargetOnBreak it stays 0 until the with-trend
                // channel cracks, at which point the post-push block below
                // sets it to the opposite-side prevDonHigh/prevDonLow.
                if side = Long && trailingStop > 0.0 && bar.High >= trailingStop then
                    closePos bar.Close bar.EndUs Normal
                elif side = Short && trailingStop > 0.0 && bar.Low <= trailingStop then
                    closePos bar.Close bar.EndUs Normal
            | MaCrossCover ->
                // v4/v5: cover when bar.Close crosses the 1h MA from the
                // favorable side. Long covers when bar.Close >= 1h MA mean
                // (price reverted up to / past the MA). Short symmetric.
                // Pre-push state — closeShortMa contains the previous
                // nShortRef closes, so the comparison is "this bar's close
                // vs the trailing 1h mean ending the previous bar".
                if side <> Flat && closeShortMa.Count > 0 then
                    let maMean = closeShortMa.State / float closeShortMa.Count
                    if maMean > 0.0 then
                        if side = Long && bar.Close >= maMean then
                            closePos bar.Close bar.EndUs Normal
                        elif side = Short && bar.Close <= maMean then
                            closePos bar.Close bar.EndUs Normal

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
            // Pre-push trailing-1h DOLLAR volume sum + trade count. Reads
            // BEFORE the current bar is pushed, so the snapshot reflects the
            // 1h window ENDING with the bar before this one — the actual
            // run-up liquidity going into the trade. volShortMa is fed
            // BuyDollarVolume + SellDollarVolume per bar so its sum is in
            // USDT (comparable across symbols).
            let dollarVolume1hSum =
                if volShortMa.Count >= nShortRef then volShortMa.State else 0.0
            let tradeCount1hSum =
                if tradeCountShortMa.Count >= nShortRef then tradeCountShortMa.State else 0.0
            // Fixed 5m pre-entry instrumentation — independent of cover MA.
            let dollarVolume5mSum =
                if vol5mMa.Count >= nInstrument5m then vol5mMa.State else 0.0
            let tradeCount5mSum =
                if tradeCount5mMa.Count >= nInstrument5m then tradeCount5mMa.State else 0.0
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

            let advLong  = cfg.MinLongAdv  <= 0.0 || currentAdv () >= cfg.MinLongAdv
            let advShort = cfg.MinShortAdv <= 0.0 || currentAdv () >= cfg.MinShortAdv

            // BreakTrigger mode (v0/v1):
            //   uptrendBreakdown   = sustained uptrend + price pierces lower channel
            //   downtrendBreakup   = sustained downtrend + price pierces upper channel
            // In v0 (fade) mode, uptrend-breakdown opens SHORT and downtrend-breakup
            // opens LONG. With ReverseDirection=true, the sides are flipped to fade
            // the break itself (uptrend-breakdown → LONG snapback).
            //
            // TrendOnly mode (v2): with-trend entry the moment the 30-bar qualifier
            // is satisfied. Uptrend (no down-violation for >=N bars) → LONG. Downtrend
            // → SHORT. Each side is armed by a fresh violation reset and un-armed by
            // an entry, so at most one entry per trend run.
            //
            // (side, coverLevel) per fire — coverLevel is the prior 3-bar Donchian
            // extreme on the opposite side from the trade. For Short trades that's
            // prevDonHigh (above entry); for Long trades it's prevDonLow (below).
            // In OppositeChannel cover mode this becomes the ratcheted trailing stop;
            // in EntryChannelTarget it's the trailing take-profit.
            let pickedSide =
                match cfg.EntryMode with
                | BreakTrigger ->
                    let uptrendBreakdown =
                        barsSinceDownViolation >= nMinTrend && bar.Low < prevDonLow
                    let downtrendBreakup =
                        barsSinceUpViolation   >= nMinTrend && bar.High > prevDonHigh
                    if uptrendBreakdown then
                        let s = if cfg.ReverseDirection then Long else Short
                        let level = if s = Short then prevDonHigh else prevDonHigh
                        let allowed = (s = Short && cfg.AllowShort && advShort)
                                    || (s = Long && cfg.AllowLong && advLong)
                        if allowed then Some (s, level) else None
                    elif downtrendBreakup then
                        let s = if cfg.ReverseDirection then Short else Long
                        let level = if s = Short then prevDonLow else prevDonLow
                        let allowed = (s = Short && cfg.AllowShort && advShort)
                                    || (s = Long && cfg.AllowLong && advLong)
                        if allowed then Some (s, level) else None
                    else None
                | TrendOnly ->
                    // Uptrend qualifier (LONG with-trend): no down-violation for
                    // >=N bars AND this bar didn't itself violate the down-channel.
                    // The second clause prevents the bar that breaks the channel
                    // (which would normally fire a BreakTrigger entry) from also
                    // satisfying the trend qualifier.
                    let uptrendQual =
                        armedUptrendLong
                        && barsSinceDownViolation >= nMinTrend
                        && not (donLowReady && bar.Low < prevDonLow)
                    let downtrendQual =
                        armedDowntrendShort
                        && barsSinceUpViolation   >= nMinTrend
                        && not (donHighReady && bar.High > prevDonHigh)
                    if uptrendQual && cfg.AllowLong && advLong then
                        Some (Long, prevDonLow)
                    elif downtrendQual && cfg.AllowShort && advShort then
                        Some (Short, prevDonHigh)
                    else None

            // MA-side entry filter — only fire if the entry bar's close is
            // on the mean-reverting side of the 1h MA. Defaults are 0.0
            // (long requires pct_1h_change < 0; short requires > 0). STRICT
            // inequality: an entry placed exactly at the MA would otherwise
            // cover instantly under MaCrossCover, guaranteed loser to fees.
            // Pass +/-infinity to disable. Applied to whichever entry mode
            // is active. Does NOT consume the TrendOnly arming — a filtered
            // bar leaves the side armed for the next satisfying bar.
            let maSideOk =
                match pickedSide with
                | Some (Long, _)  -> pct1hChange < cfg.MaxPct1hChangeForLong
                | Some (Short, _) -> pct1hChange > cfg.MinPct1hChangeForShort
                | _ -> true

            match pickedSide with
            | Some (s, level) when maSideOk ->
                // Some cover modes don't use the prior-channel level at entry:
                //   EntryChannelTargetOnBreak: level is set later, when the
                //     with-trend channel cracks.
                //   MaCrossCover: level is the live 1h MA mean, recomputed
                //     each bar by the cover-check block.
                // Override entryLevel to 0.0 in those cases so the unused
                // trailingStop slot reads as "not in trailing-target territory."
                let entryLevel =
                    match cfg.CoverMode with
                    | EntryChannelTargetOnBreak | MaCrossCover -> 0.0
                    | _ -> level
                openPos s bar.Close bar.EndUs entryLevel
                    pct1hChange pct72hChange priceRatio72hOver1h volRatio1hOver72h
                    dollarVolume1hSum tradeCount1hSum
                    dollarVolume5mSum tradeCount5mSum
                // Un-arm the side that just fired (TrendOnly only).
                if cfg.EntryMode = TrendOnly then
                    if s = Long then armedUptrendLong <- false
                    else armedDowntrendShort <- false
            | _ -> ()

        // Update violation counters AFTER the entry check, so the entry
        // logic sees the run-length going INTO this bar. The breaking bar
        // resets the counter to 0 here, but openPos has already stashed
        // the pre-reset value into barsSinceUp/DownViolationAtEntry.
        //
        // Each violation also re-arms the matching TrendOnly side so the
        // engine can fire a fresh entry on the next satisfied qualifier:
        //   - up-violation (bar.High > prevDonHigh) ends a downtrend run
        //     and resets the downtrend-short setup → re-arm armedDowntrendShort.
        //   - down-violation (bar.Low < prevDonLow) ends an uptrend run
        //     → re-arm armedUptrendLong.
        if donHighReady then
            if bar.High > prevDonHigh then
                barsSinceUpViolation <- 0
                armedDowntrendShort <- true
                // EntryChannelTargetOnBreak: while in a SHORT, an upper-channel
                // pierce signals the downtrend has cracked. Arm the trailing
                // take-profit at the OPPOSITE channel extreme (the lower
                // Donchian, which is the favorable side for a short). Only
                // arm if not already armed (trailingStop = 0).
                if cfg.CoverMode = EntryChannelTargetOnBreak
                   && side = Short
                   && trailingStop = 0.0
                   && donLowReady then
                    trailingStop <- prevDonLow
            else barsSinceUpViolation <- barsSinceUpViolation + 1
        if donLowReady then
            if bar.Low < prevDonLow then
                barsSinceDownViolation <- 0
                armedUptrendLong <- true
                // Symmetric for LONG: lower-channel pierce signals uptrend
                // crack; arm trailing target at the upper channel.
                if cfg.CoverMode = EntryChannelTargetOnBreak
                   && side = Long
                   && trailingStop = 0.0
                   && donHighReady then
                    trailingStop <- prevDonHigh
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
        // Push DOLLAR volume into the short/long volume aggregates so the
        // recorded `Volume1hAtEntry` is in USDT, comparable across symbols.
        // Using BuyDollarVolume + SellDollarVolume (the same expression as
        // advMa above) — sums tick-level price·qty so it's accurate even
        // intra-bar, not VWAP·volume which could underweight large moves.
        volShortMa.Push totalDv
        volLongMa.Push  totalDv
        tradeCountShortMa.Push (float bar.TradeCount)
        // Fixed 5m instrumentation MAs — same per-bar values as the
        // configurable short MAs; just a parallel sliding window.
        vol5mMa.Push totalDv
        tradeCount5mMa.Push (float bar.TradeCount)

        // Ratchet the cover level. Both modes ratchet the level toward
        // the favorable side of the trade, but they're symmetric mirrors:
        //
        //   OppositeChannel    : stop ratchets toward ENTRY as the trade
        //                        moves favorable. Short stop = max-high
        //                        of recent N bars; tighten DOWN as new
        //                        lower highs print. Long mirror: tighten
        //                        UP as new higher lows print.
        //
        //   EntryChannelTarget : target ratchets toward ENTRY as the trade
        //                        moves ADVERSE. Long target = max-high
        //                        of recent N bars; if a new lower max-high
        //                        prints (price drifting down against the
        //                        long), tighten the target DOWN so the
        //                        trade can close sooner. Short mirror.
        //
        // Net effect: in both modes, the cover level converges toward the
        // current price along the favorable axis. The difference is which
        // axis is "favorable" — for the stop it's the direction of the
        // trade thesis; for the target it's the direction of price drift.
        match cfg.CoverMode with
        | OppositeChannel ->
            if side = Short && trailingStop > 0.0 then
                let newStop = donHighMa.State
                if newStop > 0.0 && newStop < trailingStop then
                    trailingStop <- newStop
            elif side = Long && trailingStop > 0.0 then
                let newStop = donLowMa.State
                if newStop > 0.0 && newStop > trailingStop then
                    trailingStop <- newStop
        | EntryChannelTarget | EntryChannelTargetOnBreak ->
            // For EntryChannelTargetOnBreak, trailingStop = 0 until the
            // channel-crack arming block above fires. Once armed, the ratchet
            // behaves identically to EntryChannelTarget.
            if side = Long && trailingStop > 0.0 then
                let newTarget = donHighMa.State
                if newTarget > 0.0 && newTarget < trailingStop then
                    trailingStop <- newTarget
            elif side = Short && trailingStop > 0.0 then
                let newTarget = donLowMa.State
                if newTarget > 0.0 && newTarget > trailingStop then
                    trailingStop <- newTarget
        | MaCrossCover ->
            // No ratcheting — the cover level is the live 1h MA mean and is
            // recomputed each bar by the cover-check block above.
            ()

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs EndOfStream

let run (cfg: DonchianFadeConfig) (bars: SignedBar[]) : DonchianRoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
