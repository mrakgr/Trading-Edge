module TradingEdge.CryptoBacktest.Backtest

open System
open TradingEdge.CryptoBacktest.SignedBar
open TradingEdge.CryptoBacktest.OrderflowMA
open TradingEdge.CryptoBacktest.TradeLoader

// =============================================================================
// Per-(symbol, timeframe, MA-length) runner + metrics
// =============================================================================

type Metrics = {
    Symbol: string
    Timeframe: string
    MaLength: int
    AllowShort: bool
    BarsTotal: int
    Trades: int
    Wins: int
    WinRate: float
    /// Σ wins / Σ |losses|. Infinity when no losses, 0 when no wins.
    ProfitFactor: float
    NetPnL: float
    GrossWins: float
    GrossLosses: float    // absolute value
    /// Sharpe of per-trade return series (NetPnL / Notional), annualized
    /// using sqrt(trades-per-year) where trades-per-year is approximated
    /// from the actual round-trip cadence over the backtest window.
    Sharpe: float
    /// Largest peak-to-trough drawdown of the cumulative NetPnL curve.
    MaxDrawdown: float
    /// NetPnL / Notional. Per-trade dollar return scaled to a 1-unit notional
    /// for cross-symbol comparison.
    TotalReturnPct: float
    // Per-side breakdown — important for telling whether long-only profit
    // is genuine signal or just trend-capture in a rising market. If the
    // long side carries the entire result and shorts are flat or losing
    // 1-for-1, the signal is mostly directional bias, not orderflow edge.
    LongTrades: int
    LongWins: int
    LongNetPnL: float
    LongProfitFactor: float
    ShortTrades: int
    ShortWins: int
    ShortNetPnL: float
    ShortProfitFactor: float
    StartUs: int64
    EndUs: int64
}

let private mean (xs: float[]) =
    if xs.Length = 0 then 0.0
    else
        let mutable s = 0.0
        for x in xs do s <- s + x
        s / float xs.Length

let private stdev (xs: float[]) =
    if xs.Length < 2 then 0.0
    else
        let m = mean xs
        let mutable acc = 0.0
        for x in xs do
            let d = x - m
            acc <- acc + d * d
        sqrt (acc / float (xs.Length - 1))

let private maxDrawdown (pnls: float[]) : float =
    if pnls.Length = 0 then 0.0
    else
        let mutable cum = 0.0
        let mutable peak = 0.0
        let mutable dd = 0.0
        for p in pnls do
            cum <- cum + p
            if cum > peak then peak <- cum
            let drop = peak - cum
            if drop > dd then dd <- drop
        dd

/// Build Metrics from already-aggregated state. Caller supplies the bar
/// count, trip array, and the start/end timestamp range — none of which
/// require keeping the bar array around in memory.
let private profitFactor (pnls: float[]) : float =
    let gw = pnls |> Array.sumBy (fun p -> if p > 0.0 then p else 0.0)
    let gl = pnls |> Array.sumBy (fun p -> if p < 0.0 then -p else 0.0)
    if gl > 0.0 then gw / gl
    elif gw > 0.0 then infinity
    else 0.0

let buildMetrics
    (symbol: string)
    (timeframe: string)
    (cfg: StrategyConfig)
    (barsTotal: int)
    (startUs: int64)
    (endUs: int64)
    (trips: RoundTrip[])
    : Metrics =
    let pnls = trips |> Array.map (fun t -> t.NetPnL)
    let wins = pnls |> Array.filter (fun p -> p > 0.0)
    let losses = pnls |> Array.filter (fun p -> p < 0.0)
    let grossW = wins |> Array.sumBy id
    let grossL = losses |> Array.sumBy (fun p -> -p)
    let pf = profitFactor pnls
    let netPnL = pnls |> Array.sumBy id
    // With vol-based sizing, each trip deploys a different notional. Per-trip
    // returns must divide by the trip's effective notional, not the nominal
    // cfg.Notional. Falls back to cfg.Notional when ReferenceVol is disabled
    // (in which case EffectiveNotional == cfg.Notional anyway).
    let returns =
        trips |> Array.map (fun t ->
            let denom = if t.EffectiveNotional > 0.0 then t.EffectiveNotional else cfg.Notional
            t.NetPnL / denom)
    let totalDeployed =
        let s =
            trips |> Array.sumBy (fun t ->
                if t.EffectiveNotional > 0.0 then t.EffectiveNotional else cfg.Notional)
        if s > 0.0 then s else cfg.Notional
    let years =
        if endUs > startUs then float (endUs - startUs) / (365.25 * 86400.0 * 1_000_000.0)
        else 0.0
    let sharpe =
        let s = stdev returns
        let m = mean returns
        if s > 0.0 && years > 0.0 && returns.Length > 0 then
            let tradesPerYear = float returns.Length / years
            (m / s) * sqrt tradesPerYear
        else 0.0
    let longTrips = trips |> Array.filter (fun t -> t.Side = Long)
    let shortTrips = trips |> Array.filter (fun t -> t.Side = Short)
    let longPnls = longTrips |> Array.map (fun t -> t.NetPnL)
    let shortPnls = shortTrips |> Array.map (fun t -> t.NetPnL)
    {
        Symbol = symbol
        Timeframe = timeframe
        MaLength = cfg.MaLength
        AllowShort = cfg.AllowShort
        BarsTotal = barsTotal
        Trades = trips.Length
        Wins = wins.Length
        WinRate = if pnls.Length > 0 then float wins.Length / float pnls.Length else 0.0
        ProfitFactor = pf
        NetPnL = netPnL
        GrossWins = grossW
        GrossLosses = grossL
        Sharpe = sharpe
        MaxDrawdown = maxDrawdown pnls
        TotalReturnPct = netPnL / totalDeployed
        LongTrades = longTrips.Length
        LongWins = longPnls |> Array.filter (fun p -> p > 0.0) |> Array.length
        LongNetPnL = Array.sum longPnls
        LongProfitFactor = profitFactor longPnls
        ShortTrades = shortTrips.Length
        ShortWins = shortPnls |> Array.filter (fun p -> p > 0.0) |> Array.length
        ShortNetPnL = Array.sum shortPnls
        ShortProfitFactor = profitFactor shortPnls
        StartUs = startUs
        EndUs = endUs
    }

// =============================================================================
// Streaming per-cell driver
// =============================================================================
//
// One Cell = a single (timeframe, ma) backtest. It owns a TimeBarBuilder
// and an OrderflowMA.Engine. Trades are fed in via PushTrades; bars emitted
// by the builder are pushed to the engine on the fly. Memory footprint is
// O(MaLength) — independent of the input length.
//
// The bar count is tracked here (not via a held bars[] array) so we never
// materialize the full bar history.

type Cell(symbol: string, timeframe: string, cfg: StrategyConfig) =
    let bucketUs = bucketUsOfTimeframe timeframe
    let builder = TimeBarBuilder(bucketUs)
    // Inject the actual bucket length into the config so the engine's
    // liquidity gate can scale per-bar volume to a daily figure.
    let cfgWithBucket = { cfg with BucketUs = bucketUs }
    let engine = Engine(cfgWithBucket)
    let mutable barCount = 0
    let mutable startUs = 0L
    let mutable endUs = 0L
    let mutable hasAny = false

    let onBar (bar: SignedBar) =
        if not hasAny then
            startUs <- bar.StartUs
            hasAny <- true
        endUs <- bar.EndUs
        barCount <- barCount + 1
        engine.ProcessBar bar

    member _.Symbol = symbol
    member _.Timeframe = timeframe
    member _.Config = cfg

    /// Trade-stream input: feed each trade through the in-memory bar builder,
    /// which fires onBar at every bar close and pushes the bar to the engine.
    /// Used when reading from per-day trade parquets directly.
    member _.PushTrades(trades: TradingEdge.Simulation.BinanceLoader.Trade[]) =
        for t in trades do
            builder.Process(onBar, t)

    /// Pre-aggregated bar input: when bars have been preprocessed via
    /// CryptoData/build-bars, push them straight into the engine. Bypasses
    /// the in-memory TimeBarBuilder entirely.
    member _.PushBars(bars: SignedBar[]) =
        for b in bars do
            onBar b

    /// Close the trailing partial bar (if any) and force-exit any open
    /// position. Must be called once per cell at end-of-stream.
    member _.Close() =
        builder.Flush onBar
        engine.Flush()

    member _.BuildMetrics() =
        let trips = engine.Trips |> Seq.toArray
        buildMetrics symbol timeframe cfg barCount startUs endUs trips

    member _.Trips = engine.Trips |> Seq.toArray

// =============================================================================
// Symbol-level streaming driver
// =============================================================================

type RunInputs = {
    DataRoot: string
    Symbol: string
    Timeframe: string
    StartDate: DateTime
    EndDate: DateTime
    Config: StrategyConfig
}

/// Streaming multi-cell run: feeds the same trade stream through every
/// (timeframe, ma) cell. Loads each day's parquet exactly once regardless
/// of how many cells exist — that's the whole point of the day-by-day
/// streaming refactor.
///
/// onDay (when not None) fires once per processed day with (date, tradeCount).
/// Use it for progress reporting; days with no parquet still fire with
/// tradeCount = 0 so the caller sees forward motion through gaps.
let runCells
    (dataRoot: string)
    (symbol: string)
    (startDate: DateTime)
    (endDate: DateTime)
    (cells: Cell[])
    (onDay: (DateTime -> int -> unit) option)
    : Metrics[] =
    let mutable d = startDate.Date
    while d <= endDate.Date do
        let trades = loadDay dataRoot symbol d
        if trades.Length > 0 then
            for cell in cells do
                cell.PushTrades trades
        match onDay with
        | Some f -> f d trades.Length
        | None -> ()
        d <- d.AddDays 1.0
    for cell in cells do
        cell.Close()
    cells |> Array.map (fun c -> c.BuildMetrics())

/// Streaming single-cell run from trades: load each day's trade parquet in
/// turn, push the trades into one Cell, discard the trades, repeat. Memory
/// usage is bounded by the largest single day plus the rolling-window state.
let runOne (inp: RunInputs) (onDay: (DateTime -> int -> unit) option) : Metrics * RoundTrip[] =
    let cell = Cell(inp.Symbol, inp.Timeframe, inp.Config)
    let metrics = runCells inp.DataRoot inp.Symbol inp.StartDate inp.EndDate [| cell |] onDay
    metrics.[0], cell.Trips

// =============================================================================
// Pre-aggregated bar path
// =============================================================================
//
// When CryptoData/build-bars has produced per-(symbol, timeframe) bar
// parquets, the backtester loads ~17k bars per cell instead of streaming
// millions of trades per day. This is the fast path used by the sweep when
// --bars-root is set.

let runCellsFromBars
    (barsRoot: string)
    (symbol: string)
    (startDate: System.DateTime)
    (endDate: System.DateTime)
    (cells: Cell[])
    : Metrics[] =
    // Group cells by timeframe so each timeframe's bar parquet is loaded
    // exactly once. Inside a timeframe group, we only need to scan the bar
    // array once if we duplicate it — but cells already share the same input,
    // and pushing into N engines one-by-one is cheap. Keep it simple.
    //
    // Liquidity is gated per-bar inside OrderflowMA.Engine (entries only)
    // when StrategyConfig.MinDailyQuoteVolume > 0.
    let byTf =
        cells
        |> Array.groupBy (fun c -> c.Timeframe)
    for (tf, group) in byTf do
        let bars = BarLoader.loadByDate barsRoot tf symbol startDate endDate
        for cell in group do
            cell.PushBars bars
    for cell in cells do
        cell.Close()
    cells |> Array.map (fun c -> c.BuildMetrics())
