module TradingEdge.Hmm.VolumeBar

open System.Collections.Immutable
open TradingEdge.Hmm.BinanceLoader

/// One volume bar: a fixed-volume slice of the trade tape. VWAP and StdDev
/// are volume-weighted; TradeCount / BuyCount are whole-trade counts (not
/// split when a trade straddles a bar boundary, see VolumeBarBuilder for the
/// split rule). StartUs / EndUs are the timestamps of the first and last
/// trade fragment that contributed to the bar — Duration is just End - Start.
type VolumeBar = {
    VWAP: float
    StdDev: float
    Volume: float
    High: float
    Low: float
    TradeCount: int           // n: number of trades whose first fragment landed here
    BuyCount: int             // k: of those, how many were buyer-aggressive (sign = +1)
    StartUs: int64
    EndUs: int64
    Trades: ImmutableArray<struct (float * float)>   // (price, fragmentVolume)
}

/// Reduce a list of (price, fragmentVolume) trades into a closed bar.
/// Lifted from the old VWAP project; we add High/Low alongside the
/// volume-weighted VWAP/StdDev, and let the caller pass the count fields
/// (which cannot be derived from the fragments alone).
type VolumeBarOfTrades() =
    member inline self.Process
        ( onNext,
          trades: ImmutableArray<struct (float * float)>,
          tradeCount: int,
          buyCount: int,
          startUs: int64,
          endUs: int64 ) =
        let mutable priceVolumeSum = 0.0
        let mutable priceSquaredVolumeSum = 0.0
        let mutable volumeSum = 0.0
        let mutable hi = System.Double.NegativeInfinity
        let mutable lo = System.Double.PositiveInfinity
        for struct (price, volume) in trades do
            priceVolumeSum <- priceVolumeSum + price * volume
            priceSquaredVolumeSum <- priceSquaredVolumeSum + price * price * volume
            volumeSum <- volumeSum + volume
            if price > hi then hi <- price
            if price < lo then lo <- price
        let vwap = priceVolumeSum / volumeSum
        let variance = priceSquaredVolumeSum / volumeSum - vwap * vwap
        onNext {
            VWAP = vwap
            StdDev = sqrt (max 0.0 variance)
            Volume = volumeSum
            High = hi
            Low = lo
            TradeCount = tradeCount
            BuyCount = buyCount
            StartUs = startUs
            EndUs = endUs
            Trades = trades
        }

/// Accumulate trades into fixed-volume buckets. When a trade's quantity
/// overflows the remaining bar capacity, we *split the volume* between the
/// current and next bar (so VWAP / StdDev / Volume stay exact), but the
/// trade is *only counted once* — toward the bar in which it started. This
/// keeps (n, k) a clean Binomial trial count for the emission model.
type GroupTrades(barSize: float) =
    member val BarSize = barSize
    member val CurrentTrades = ImmutableArray.CreateBuilder<struct (float * float)>()
    member val CurrentVolumeSum = 0.0 with get, set
    member val CurrentTradeCount = 0 with get, set
    member val CurrentBuyCount = 0 with get, set
    member val CurrentStartUs = 0L with get, set
    member val CurrentEndUs = 0L with get, set
    member val HasStarted = false with get, set

    member inline self.Process(onNext, trade: Trade) =
        let price = trade.Price
        let mutable remaining = trade.Quantity
        // Count this whole trade exactly once, against the bar that holds its
        // first fragment.
        let mutable counted = false
        while remaining > 0.0 do
            // On entering an empty bar, stamp the start time.
            if self.CurrentVolumeSum = 0.0 && self.CurrentTrades.Count = 0 then
                self.CurrentStartUs <- trade.TimestampUs
                self.HasStarted <- true
            if not counted then
                self.CurrentTradeCount <- self.CurrentTradeCount + 1
                if trade.Sign > 0.0 then
                    self.CurrentBuyCount <- self.CurrentBuyCount + 1
                counted <- true
            self.CurrentEndUs <- trade.TimestampUs
            let spaceLeft = self.BarSize - self.CurrentVolumeSum
            if remaining <= spaceLeft then
                self.CurrentTrades.Add (struct (price, remaining))
                self.CurrentVolumeSum <- self.CurrentVolumeSum + remaining
                remaining <- 0.0
            else
                if spaceLeft > 0.0 then
                    self.CurrentTrades.Add (struct (price, spaceLeft))
                    self.CurrentVolumeSum <- self.CurrentVolumeSum + spaceLeft
                    remaining <- remaining - spaceLeft
                if self.CurrentVolumeSum >= self.BarSize then
                    onNext (
                        self.CurrentTrades.ToImmutableArray(),
                        self.CurrentTradeCount,
                        self.CurrentBuyCount,
                        self.CurrentStartUs,
                        self.CurrentEndUs)
                    self.CurrentTrades.Clear()
                    self.CurrentVolumeSum <- 0.0
                    self.CurrentTradeCount <- 0
                    self.CurrentBuyCount <- 0

/// Top-level builder: feed trades, get VolumeBars via onNext.
type VolumeBarBuilder(barSize: float) =
    member val BarSize = barSize
    member val Grouper = GroupTrades(barSize)
    member val BarBuilder = VolumeBarOfTrades()

    member inline self.Process(onNext, trade: Trade) =
        self.Grouper.Process(
            (fun (trades, n, k, startUs, endUs) ->
                self.BarBuilder.Process(onNext, trades, n, k, startUs, endUs)),
            trade)

/// Convenience: build the full bar array from a Trade[].
let buildBars (barSize: float) (trades: Trade[]) : VolumeBar[] =
    let result = ResizeArray<VolumeBar>()
    let builder = VolumeBarBuilder(barSize)
    for trade in trades do
        builder.Process((fun bar -> result.Add bar), trade)
    result.ToArray()
