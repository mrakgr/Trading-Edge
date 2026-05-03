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

let computeMetrics
    (symbol: string)
    (timeframe: string)
    (cfg: StrategyConfig)
    (bars: SignedBar[])
    (trips: RoundTrip[])
    : Metrics =
    let pnls = trips |> Array.map (fun t -> t.NetPnL)
    let wins = pnls |> Array.filter (fun p -> p > 0.0)
    let losses = pnls |> Array.filter (fun p -> p < 0.0)
    let grossW = wins |> Array.sumBy id
    let grossL = losses |> Array.sumBy (fun p -> -p)
    let pf =
        if grossL > 0.0 then grossW / grossL
        elif grossW > 0.0 then infinity
        else 0.0
    let netPnL = pnls |> Array.sumBy id
    let returns = pnls |> Array.map (fun p -> p / cfg.Notional)
    // Annualize trade-level Sharpe by sqrt(trades_per_year). Estimate
    // trades_per_year from the actual round-trip cadence: trades / years.
    let startUs = if bars.Length > 0 then bars.[0].StartUs else 0L
    let endUs = if bars.Length > 0 then bars.[bars.Length - 1].EndUs else 0L
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
    {
        Symbol = symbol
        Timeframe = timeframe
        MaLength = cfg.MaLength
        AllowShort = cfg.AllowShort
        BarsTotal = bars.Length
        Trades = trips.Length
        Wins = wins.Length
        WinRate = if pnls.Length > 0 then float wins.Length / float pnls.Length else 0.0
        ProfitFactor = pf
        NetPnL = netPnL
        GrossWins = grossW
        GrossLosses = grossL
        Sharpe = sharpe
        MaxDrawdown = maxDrawdown pnls
        TotalReturnPct = netPnL / cfg.Notional
        StartUs = startUs
        EndUs = endUs
    }

type RunInputs = {
    DataRoot: string
    Symbol: string
    Timeframe: string
    StartDate: DateTime
    EndDate: DateTime
    Config: StrategyConfig
}

let runOne (inp: RunInputs) : Metrics * RoundTrip[] =
    let trades = loadRange inp.DataRoot inp.Symbol inp.StartDate inp.EndDate
    if trades.Length = 0 then
        let empty = computeMetrics inp.Symbol inp.Timeframe inp.Config [||] [||]
        empty, [||]
    else
        let bucketUs = bucketUsOfTimeframe inp.Timeframe
        let bars = buildBars bucketUs trades
        let trips = OrderflowMA.run inp.Config bars
        let metrics = computeMetrics inp.Symbol inp.Timeframe inp.Config bars trips
        metrics, trips
