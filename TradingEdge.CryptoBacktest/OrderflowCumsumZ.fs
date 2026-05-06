module TradingEdge.CryptoBacktest.OrderflowCumsumZ

open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.OrderflowMA
open MathNet.Numerics

// =============================================================================
// Orderflow-Cumsum-Z strategy: short-term-conviction cumsum, persistence-gated
// =============================================================================
//
// Per-bar update: standardize the bar's net flow against its own 200h
// distribution and accumulate the standard-normal-CDF score (folded to
// [-1, +1] via erf) into a clamped cumsum.
//
//   delta_t        = bar.BuyDollarVolume - bar.SellDollarVolume
//   sigma_t        = StdMa_200h(delta).SampleStd  (sample std, leak-free-ish)
//   z_t            = delta_t / sigma_t
//   magnitude_t    = erf(z_t / sqrt 2.0)            ∈ (-1, +1)
//   cumsum_t       = clamp(cumsum_{t-1} + magnitude_t, [-threshold, +threshold])
//
// The cumsum + erf encoding is "short-term conviction": each bar contributes
// almost nothing if its delta is typical for the symbol, and saturates
// toward ±1 only when the per-bar net flow is many sigmas from the local
// mean.
//
// Persistence gate (the v0 inner signal): track 200h rolling sums of buy
// and sell dollar volume separately. A long FIRE requires:
//   (a) cumsum touches +threshold from inside, AND
//   (b) buyMa_200h.State > sellMa_200h.State  ("regime confirms")
// Symmetric for shorts. The cumsum says "short-term direction is now
// strongly bullish"; the persistence gate says "the 200h regime is also
// bullish" — both must agree.
//
// EXIT semantics:
//   - When cumsum touches the OPPOSITE clamp from inside, close the open
//     position regardless of the persistence gate at that moment.
//   - On the same bar, if the persistence gate CONFIRMS the new direction
//     (e.g. cumsum hit +threshold AND buyMa > sellMa for a short → long
//     flip), open the new position. If it does NOT confirm, stay flat
//     until the next clamp touch.
//
// Pre-fill: during the first MaWindowHours of bars, the rolling windows
// (StdMa, buyMa, sellMa) aren't full. Vote 0 — cumsum stays at 0 until
// the window has filled.

type CumsumZConfig = {
    /// Clamp magnitude. Long fires at +CumsumThreshold, short at -CumsumThreshold.
    /// Lower than the ±1 cumsum because per-bar magnitudes are bounded in
    /// (-1, +1) and average much smaller than 1 in normal conditions. 10
    /// is a starting guess.
    CumsumThreshold: float
    /// Rolling-window length in HOURS for both the std-of-delta normalizer
    /// and the persistence-gate buy/sell sums.
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

let defaultCumsumZConfig (threshold: float) : CumsumZConfig =
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

type Engine(cfg: CumsumZConfig) =
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

    // 200h rolling state. buyMa/sellMa drive the persistence gate. deltaStd
    // normalizes the per-bar net flow; its state is (ΣX, ΣX²) and SampleStd
    // is the leak-free running sample-std.
    let n = hoursToBars (max 1 cfg.MaWindowHours)
    let buyMa = SumMa(n)
    let sellMa = SumMa(n)
    let deltaStd = StdMa(n)

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

    let mutable lastClose = 0.0
    let mutable lastUs = 0L

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

    let openPos (newSide: Side) (fillPrice: float) (fillUs: int64) (cumAtFire: float) =
        side <- newSide
        entryPrice <- fillPrice
        entryUs <- fillUs
        effectiveNotional <- computeEffectiveNotional ()
        barsHeld <- 1
        mfeUsd <- 0.0
        maeUsd <- 0.0
        ratioAtEntry <- cumAtFire
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

        let gapFired = priceGapHit bar
        if gapFired then
            closePos lastClose lastUs
            cumsum <- 0.0
            signalLockoutUntil <- barsSeen + n
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
        let delta = bar.BuyDollarVolume - bar.SellDollarVolume
        buyMa.Push  bar.BuyDollarVolume
        sellMa.Push bar.SellDollarVolume
        deltaStd.Push delta

        barsSeen <- barsSeen + 1
        lastClose <- bar.Close
        lastUs <- bar.EndUs

        // Per-bar magnitude: erf(z / sqrt 2). Pre-fill (rolling window not
        // full) votes 0. Also vote 0 if the running std is degenerate
        // (zero or tiny) — avoids divide-by-zero when a symbol has been
        // perfectly balanced in its window.
        let magnitude =
            if barsSeen < n then 0.0
            else
                let sigma = deltaStd.SampleStd
                if sigma <= 0.0 then 0.0
                else
                    let z = delta / sigma
                    SpecialFunctions.Erf(z / sqrt 2.0)

        let prevCumsum = cumsum
        let next = cumsum + magnitude
        cumsum <-
            if next > threshold then threshold
            elif next < -threshold then -threshold
            else next

        if barsSeen >= signalLockoutUntil then
            // Detect first-touch of either clamp from strictly inside. Use
            // strict inequality on prevCumsum so re-entries while still
            // clamped don't re-fire.
            let hitTop    = prevCumsum < threshold && cumsum >= threshold
            let hitBottom = prevCumsum > -threshold && cumsum <= -threshold

            // Persistence gate: the 200h-regime sign must agree with the
            // direction of the cumsum-driven fire.
            let regimeBull = buyMa.State > sellMa.State
            let regimeBear = sellMa.State > buyMa.State

            if hitTop then
                // Top clamp: exit any open short, then potentially flip long
                // (only if regime confirms; otherwise stay flat).
                if side = Short then closePos bar.Close bar.EndUs
                if side = Flat && regimeBull then
                    let adv = currentAdv ()
                    let advGate = cfg.MinLongAdv <= 0.0 || adv >= cfg.MinLongAdv
                    if advGate then
                        openPos Long bar.Close bar.EndUs cumsum
            elif hitBottom then
                // Bottom clamp: exit any open long, then potentially flip
                // short (only if shorting allowed AND regime confirms).
                if side = Long then closePos bar.Close bar.EndUs
                if side = Flat && cfg.AllowShort && regimeBear then
                    let adv = currentAdv ()
                    let advGate = cfg.MinShortAdv <= 0.0 || adv >= cfg.MinShortAdv
                    if advGate then
                        openPos Short bar.Close bar.EndUs cumsum

    member _.Flush() =
        if side <> Flat && lastClose > 0.0 then
            closePos lastClose lastUs

let run (cfg: CumsumZConfig) (bars: SignedBar[]) : RoundTrip[] =
    let eng = Engine(cfg)
    for bar in bars do
        eng.ProcessBar bar
    eng.Flush()
    eng.Trips |> Seq.toArray
