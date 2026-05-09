module TradingEdge.CryptoBacktest.OrderflowConsolBreakout

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.RollingMa
open TradingEdge.CryptoBacktest.OrderflowMA

// =============================================================================
// Volatility-compression breakout engine — "coiled spring"
// =============================================================================
//
// Structurally opposite to the Donchian-fade family: we wait for price to
// COMPRESS into a low-volatility 3h window while volume is ELEVATED, then
// enter WITH the direction of the first range break.
//
// Single-bar-at-the-moment-of-break check (no history counter). On the entry
// bar:
//
//   compressionRatio = std(log close) over ConsolMinutes
//                      / std(log returns) over ConsolMinutes
//
// is below `VolCompressionMaxRatio`. Numerator measures how far the price
// LEVEL has wandered (low when prices stay near each other); denominator is
// realised vol of bar-to-bar moves. Ratio ≈ √(N/3) for a pure random walk
// (~7.75 at N=180); pure mean-reverting (level pinned) approaches 0; pure
// trending gives ratios > 7.75. Genuine consolidation lives in the 1..5 range.
//
// AND volume rvol elevated:
//   volRvol = ConsolMinutes-window mean volume
//             / RvolDenomDays mean volume
// must be >= `VolRvolMin`. Numerator window is the SAME as the consolidation
// window — "is there unusually high volume in the same window over which
// prices have been consolidating?"
//
// Entry trigger (on the same bar):
//   LONG : bar.High >= rolling ConsolMinutes-bar max-high (3h Donchian high)
//          AND bar.Volume >= BarVolMinMultiple * trailing ConsolMinutes vol mean
//   SHORT: bar.Low  <= rolling ConsolMinutes-bar min-low  (3h Donchian low)
//          AND bar.Volume >= BarVolMinMultiple * trailing ConsolMinutes vol mean
//
// Direction: WITH the break. No reverse-direction flag for v0.
//
// Cover — two modes (CLI flag):
//   DonchianStop : ratcheted 3-bar trailing Donchian stop on the with-trade
//                  side. Long stop = current MinMa(3) of bar.Low, ratchets
//                  UP only. Short symmetric. Cover when bar.Low < trailingStop
//                  (long) / bar.High > trailingStop (short).
//   CvdFlip      : cover when the rolling 1h CVD (Σ(buyDV - sellDV) over
//                  CvdMinutes) crosses to the unfavourable sign. Long covers
//                  when cvdMa.State < 0; short covers when cvdMa.State > 0.
//                  Strict inequality — flat CVD doesn't trigger.
//
// No stops, no time-stop, no MA-side filter for v0. Cover mode IS the
// protection. The point of v0 is to test the consolidation+breakout signal
// cleanly.
//
// Read-before-write convention (same as Donchian-fade): all signal logic
// runs against rolling state from the PREVIOUS N bars (not including this
// one). MAs are pushed at the very end of ProcessBar.

type ConsolBreakoutCoverMode =
    | DonchianStop
    | CvdFlip

type ConsolBreakoutRoundTrip = {
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
    // Engine-specific post-hoc fields:
    VolCompressionRatioAtEntry: float
    VolRvolAtEntry: float
    BarVolMultipleAtEntry: float
    RangeWidthPctAtEntry: float
    Pct1hChangeAtEntry: float
    Pct24hChangeAtEntry: float
    CoverModeStr: string
}

type ConsolBreakoutConfig = {
    /// Consolidation/break window in MINUTES (180 = 3h).
    ConsolMinutes: int
    /// Vol-compression ratio threshold. Entry requires
    ///   std(log close) over ConsolMinutes  /  std(log returns) over ConsolMinutes  <  this.
    /// Expected scale: a pure random walk gives ratio ~ sqrt(N/3) (~7.75 at N=180).
    /// Pure mean-reverting (level pinned) prices approach 0; pure trending prices
    /// give ratios >7.75. "Consolidation" lives in the 1..5 range. Default 1.0.
    VolCompressionMaxRatio: float
    /// Volume rvol gate at entry — ConsolMinutes-window mean over RvolDenomDays
    /// mean must be >= this. Numerator is the consolidation-window volume mean
    /// (reused from the per-bar-volume gate). Default 3.0.
    VolRvolMin: float
    /// Denominator window for volume rvol, in DAYS. Default 30.
    RvolDenomDays: int
    /// Per-bar-volume gate at entry: bar.Volume >= this * (trailing ConsolMinutes vol mean).
    /// Default 2.0.
    BarVolMinMultiple: float
    /// 1h MA window in minutes (for the post-hoc Pct1hChangeAtEntry field). Default 60.
    ShortRefMinutes: int
    /// 24h MA window in hours (for the post-hoc Pct24hChangeAtEntry field). Default 24.
    DayRefHours: int
    /// Cover rule. Default DonchianStop.
    CoverMode: ConsolBreakoutCoverMode
    /// Donchian-stop window in BARS (only used when CoverMode = DonchianStop). Default 3.
    StopDonchianBars: int
    /// CVD horizon in MINUTES (only used when CoverMode = CvdFlip). Default 60 (1h).
    CvdMinutes: int
    AllowLong: bool
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

let defaultConsolBreakoutConfig () : ConsolBreakoutConfig =
    { ConsolMinutes = 180
      VolCompressionMaxRatio = 1.0
      VolRvolMin = 3.0
      RvolDenomDays = 30
      BarVolMinMultiple = 2.0
      ShortRefMinutes = 60
      DayRefHours = 24
      CoverMode = DonchianStop
      StopDonchianBars = 3
      CvdMinutes = 60
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

let coverModeStr (m: ConsolBreakoutCoverMode) : string =
    match m with
    | DonchianStop -> "donchian-stop"
    | CvdFlip -> "cvd-flip"

type Engine(cfg: ConsolBreakoutConfig) =
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

    let nConsol     = minutesToBars (max 1 cfg.ConsolMinutes)
    let nRvolDen    = daysToBars    (max 1 cfg.RvolDenomDays)
    let nShortRef   = minutesToBars (max 1 cfg.ShortRefMinutes)
    let nDayRef     = hoursToBars   (max 1 cfg.DayRefHours)
    let nCvd        = minutesToBars (max 1 cfg.CvdMinutes)
    let nStopDon    = max 1 cfg.StopDonchianBars
    let advWindowBars = daysToBars 90
    let volWindowBars = daysToBars (max 1 cfg.VolWindowDays)
    let warmupBars = hoursToBars 200

    // Compression-ratio numerator/denominator over the consolidation window.
    let logCloseStd = StdMa(nConsol)
    let logRetStd   = StdMa(nConsol)
    // 3h Donchian channels — break levels.
    let consolHighMa = MaxMa(nConsol)
    let consolLowMa  = MinMa(nConsol)
    // Consolidation-window volume sum — both per-bar-volume gate denom and
    // the rvol numerator (we just divide by nConsol to get the mean).
    let consolVolSum = SumMa(nConsol)
    // Rvol denominator (30d volume mean).
    let volRvolDen = SumMa(nRvolDen)
    // 1h close MA (for Pct1hChangeAtEntry).
    let closeShortMa = SumMa(nShortRef)
    // 24h close MA (for Pct24hChangeAtEntry).
    let closeDayMa = SumMa(nDayRef)
    // 1h CVD (signed). For CvdFlip cover.
    let cvdMa = SumMa(nCvd)
    // 3-bar Donchian for the with-trade trailing stop (DonchianStop cover).
    let donHighMa = MaxMa(nStopDon)
    let donLowMa  = MinMa(nStopDon)
    // Standard 90d ADV.
    let advMa = SumMa(advWindowBars)
    // Vol-targeting std (per-bar log-return).
    let volMa = StdMa(volWindowBars)

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

    // Per-trip stash — filled at entry, written at close.
    let mutable volCompressionRatioAtEntry = 0.0
    let mutable volRvolAtEntry = 0.0
    let mutable barVolMultipleAtEntry = 0.0
    let mutable rangeWidthPctAtEntry = 0.0
    let mutable pct1hChangeAtEntry = 0.0
    let mutable pct24hChangeAtEntry = 0.0

    let mutable lastClose = 0.0
    let mutable lastUs = 0L
    let mutable signalLockoutUntil = 0

    let mutable fundingEvents : (int64 * float)[] = [||]
    let mutable fundingPtr = 0

    let trips = ResizeArray<ConsolBreakoutRoundTrip>()

    let coverModeS = coverModeStr cfg.CoverMode

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

    let openPos
        (newSide: Side)
        (fillPrice: float)
        (fillUs: int64)
        (stopLevel: float)
        (compressionRatio: float)
        (volRvol: float)
        (barVolMultiple: float)
        (rangeWidthPct: float)
        (pct1hChange: float)
        (pct24hChange: float) =
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
        volCompressionRatioAtEntry <- compressionRatio
        volRvolAtEntry <- volRvol
        barVolMultipleAtEntry <- barVolMultiple
        rangeWidthPctAtEntry <- rangeWidthPct
        pct1hChangeAtEntry <- pct1hChange
        pct24hChangeAtEntry <- pct24hChange

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
            VolCompressionRatioAtEntry = volCompressionRatioAtEntry
            VolRvolAtEntry = volRvolAtEntry
            BarVolMultipleAtEntry = barVolMultipleAtEntry
            RangeWidthPctAtEntry = rangeWidthPctAtEntry
            Pct1hChangeAtEntry = pct1hChangeAtEntry
            Pct24hChangeAtEntry = pct24hChangeAtEntry
            CoverModeStr = coverModeS
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
        volCompressionRatioAtEntry <- 0.0
        volRvolAtEntry <- 0.0
        barVolMultipleAtEntry <- 0.0
        rangeWidthPctAtEntry <- 0.0
        pct1hChangeAtEntry <- 0.0
        pct24hChangeAtEntry <- 0.0

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
    member _.Trips = trips :> seq<ConsolBreakoutRoundTrip>
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

        // Cover precedence: pct-stop first (off by default), then mode-specific.
        match pctStopPriceIfHit bar with
        | Some stopPx -> closePos stopPx bar.EndUs
        | None ->
            match cfg.CoverMode with
            | DonchianStop ->
                if side = Long && trailingStop > 0.0 && bar.Low < trailingStop then
                    closePos bar.Close bar.EndUs
                elif side = Short && trailingStop > 0.0 && bar.High > trailingStop then
                    closePos bar.Close bar.EndUs
            | CvdFlip ->
                if side <> Flat then
                    let cvdState = cvdMa.State
                    if side = Long && cvdState < 0.0 then
                        closePos bar.Close bar.EndUs
                    elif side = Short && cvdState > 0.0 then
                        closePos bar.Close bar.EndUs

        // Pre-push reads — the rolling state reflects the previous N bars
        // not including this one, which is exactly the right reading for
        // "did this bar break the prior 3h range."
        let consolReady = consolHighMa.Count >= nConsol
                          && consolLowMa.Count >= nConsol
                          && consolVolSum.Count >= nConsol
                          && logCloseStd.Count >= nConsol
                          && logRetStd.Count >= nConsol
        let rvolDenReady = volRvolDen.Count >= nRvolDen
        let shortRefReady = closeShortMa.Count >= nShortRef
        let dayRefReady = closeDayMa.Count >= nDayRef

        let prevConsolHigh = if consolReady then consolHighMa.State else 0.0
        let prevConsolLow  = if consolReady then consolLowMa.State  else 0.0
        let prevConsolVolMean =
            if consolReady then consolVolSum.State / float nConsol else 0.0
        let prevLogCloseStd = if consolReady then logCloseStd.SampleStd else 0.0
        let prevLogRetStd   = if consolReady then logRetStd.SampleStd   else 0.0
        let prevVolRvolDen =
            if rvolDenReady then volRvolDen.State / float nRvolDen else 0.0
        let prevCloseShortMean =
            if shortRefReady then closeShortMa.State / float nShortRef else 0.0
        let prevCloseDayMean =
            if dayRefReady then closeDayMa.State / float nDayRef else 0.0

        // Pre-push 3-bar Donchian for the with-trade trailing stop.
        let prevDonHigh = if donHighMa.Count >= nStopDon then donHighMa.State else 0.0
        let prevDonLow  = if donLowMa.Count  >= nStopDon then donLowMa.State  else 0.0

        // Entry checks.
        let windowsReady =
            barsSeen          >= warmupBars
            && barsSeen       >= signalLockoutUntil
            && consolReady
            && rvolDenReady
            && shortRefReady
            && dayRefReady

        if windowsReady && side = Flat then
            let volCompressionRatio =
                if prevLogRetStd > 0.0 then prevLogCloseStd / prevLogRetStd else 0.0
            let volRvol =
                if prevVolRvolDen > 0.0 then prevConsolVolMean / prevVolRvolDen else 0.0
            let barVolMultiple =
                if prevConsolVolMean > 0.0 then bar.Volume / prevConsolVolMean else 0.0
            let rangeWidthPct =
                if bar.Close > 0.0 then (prevConsolHigh - prevConsolLow) / bar.Close else 0.0
            let pct1hChange =
                if prevCloseShortMean > 0.0 then bar.Close / prevCloseShortMean - 1.0 else 0.0
            let pct24hChange =
                if prevCloseDayMean > 0.0 then bar.Close / prevCloseDayMean - 1.0 else 0.0

            let compressionGate = volCompressionRatio < cfg.VolCompressionMaxRatio
            let rvolGate = volRvol >= cfg.VolRvolMin
            let barVolGate = barVolMultiple >= cfg.BarVolMinMultiple

            let advLong  = cfg.MinLongAdv  <= 0.0 || currentAdv () >= cfg.MinLongAdv
            let advShort = cfg.MinShortAdv <= 0.0 || currentAdv () >= cfg.MinShortAdv

            let longBreak  = bar.High >= prevConsolHigh
            let shortBreak = bar.Low  <= prevConsolLow

            let coreGates = compressionGate && rvolGate && barVolGate

            let longFire  = coreGates && longBreak  && cfg.AllowLong  && advLong
            let shortFire = coreGates && shortBreak && cfg.AllowShort && advShort

            // Mutual exclusion: skip the bar entirely if both sides fire
            // (only when an extreme outlier bar simultaneously sets a new
            // 3h high AND a new 3h low).
            if longFire && not shortFire then
                let stopLevel =
                    match cfg.CoverMode with
                    | DonchianStop -> prevDonLow
                    | CvdFlip -> 0.0
                openPos Long bar.Close bar.EndUs stopLevel
                    volCompressionRatio volRvol barVolMultiple rangeWidthPct
                    pct1hChange pct24hChange
            elif shortFire && not longFire then
                let stopLevel =
                    match cfg.CoverMode with
                    | DonchianStop -> prevDonHigh
                    | CvdFlip -> 0.0
                openPos Short bar.Close bar.EndUs stopLevel
                    volCompressionRatio volRvol barVolMultiple rangeWidthPct
                    pct1hChange pct24hChange

        // Push bars into all rolling MAs — last, after entry/exit consumed
        // prior state.
        if prevClose > 0.0 && bar.Close > 0.0 then
            let logRet = log (bar.Close / prevClose)
            volMa.Push logRet
            logRetStd.Push logRet
        if bar.Close > 0.0 then
            logCloseStd.Push (log bar.Close)
        prevClose <- bar.Close
        let totalDv = bar.BuyDollarVolume + bar.SellDollarVolume
        advMa.Push totalDv
        consolHighMa.Push bar.High
        consolLowMa.Push  bar.Low
        consolVolSum.Push bar.Volume
        volRvolDen.Push   bar.Volume
        closeShortMa.Push bar.Close
        closeDayMa.Push   bar.Close
        cvdMa.Push (bar.BuyDollarVolume - bar.SellDollarVolume)
        donHighMa.Push bar.High
        donLowMa.Push  bar.Low

        // Ratchet the with-trade trailing stop (DonchianStop cover only).
        if cfg.CoverMode = DonchianStop then
            if side = Long && trailingStop > 0.0 then
                let newStop = donLowMa.State
                if newStop > 0.0 && newStop > trailingStop then
                    trailingStop <- newStop
            elif side = Short && trailingStop > 0.0 then
                let newStop = donHighMa.State
                if newStop > 0.0 && newStop < trailingStop then
                    trailingStop <- newStop

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: ConsolBreakoutConfig) (bars: SignedBar[]) : ConsolBreakoutRoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
