module TradingEdge.MomentumBacktest.Signals

open System
open System.Data
open Dapper
open TradingEdge.MomentumBacktest.Types

/// Build the per-ticker signal query. The two rolling-window SIZES (lookback-high
/// and stop-low) cannot be DuckDB runtime parameters — `ROWS BETWEEN N PRECEDING`
/// requires a literal — so they are validated ints interpolated into the SQL
/// string. Everything else (thresholds, dates, liquidity floor, tradable filter)
/// is a real bound parameter. The `... AND 1 PRECEDING` upper bound on each window
/// is what EXCLUDES the current day, giving point-in-time correctness.
///
/// Scoped to a single ticker (`$ticker`) so the caller can stream ticker-by-ticker
/// and keep the .NET heap bounded. Returns the FULL per-day series for that ticker
/// over the date window (the stop-walk needs every bar between entry and exit);
/// `is_entry` marks entry days.
let private query (lookbackHigh: int) (stopLowWindow: int) : string =
    // Guard: only ever interpolate validated positive ints, never user strings.
    if lookbackHigh < 1 || stopLowWindow < 1 then
        invalidArg "window" "lookback/stop windows must be >= 1"
    $"""
    WITH base AS (
      SELECT
        p.ticker, p.date,
        p.adj_open, p.adj_high, p.adj_low, p.adj_close, p.adj_volume,
        LAG(p.adj_close) OVER w                       AS prev_adj_close,
        v.avg_volume_4w, v.avg_dollar_volume_4w,
        ROW_NUMBER() OVER w                           AS prior_idx,
        MAX(p.adj_close) OVER (PARTITION BY p.ticker ORDER BY p.date
            ROWS BETWEEN {lookbackHigh} PRECEDING AND 1 PRECEDING) AS hi_252_prior,
        MIN(p.adj_low)   OVER (PARTITION BY p.ticker ORDER BY p.date
            ROWS BETWEEN {stopLowWindow} PRECEDING AND 1 PRECEDING) AS low_15_prior,
        -- per-bar true range (needs prev close); NULL on a ticker's first bar.
        GREATEST(
            p.adj_high - p.adj_low,
            ABS(p.adj_high - LAG(p.adj_close) OVER w),
            ABS(p.adj_low  - LAG(p.adj_close) OVER w)
        )                                             AS tr
      FROM split_adjusted_prices p
      JOIN stock_volume_4w v ON v.ticker = p.ticker AND v.date = p.date
      WHERE p.ticker = $ticker
      WINDOW w AS (PARTITION BY p.ticker ORDER BY p.date)
    ),
    windowed AS (
      SELECT *,
        -- 14-day mean true range over the 14 PRIOR bars (excludes the current bar
        -- via the `AND 1 PRECEDING` bound), so all of these measure the name's
        -- state going INTO the breakout. No lookahead.
        AVG(tr) OVER w14 AS atr_abs_14,
        AVG(tr) OVER w14 / NULLIF(adj_close, 0) AS atr_pct_14,
        -- 14-day price span (consolidation width): highest high minus lowest low
        -- over the same prior window.
        ( MAX(adj_high) OVER w14 - MIN(adj_low) OVER w14 ) AS range_abs_14
      FROM base
      WINDOW w14 AS (PARTITION BY ticker ORDER BY date
                     ROWS BETWEEN 14 PRECEDING AND 1 PRECEDING)
    ),
    ratioed AS (
      SELECT *,
        range_abs_14 / NULLIF(adj_close, 0) AS range_pct_14,
        -- Consolidation tightness: 14-day span / cumulative daily travel
        -- (14 * ATR). ~1.0 = the window trended cleanly; well below 1 = price
        -- chopped in a tight band relative to how much it moved day-to-day
        -- (a "coiled spring"). Bounded, scale-free.
        range_abs_14 / NULLIF(14.0 * atr_abs_14, 0) AS tightness_14
      FROM windowed
    ),
    flagged AS (
      SELECT *,
        adj_close / prev_adj_close - 1.0       AS pct_up,
        adj_volume / NULLIF(avg_volume_4w, 0)  AS rvol,
        ( adj_close / prev_adj_close - 1.0 >= $upThr
          AND adj_volume / NULLIF(avg_volume_4w, 0) >= $rvolThr
          AND adj_close >= hi_252_prior
          AND prior_idx > $minPriorDays
          AND avg_dollar_volume_4w >= $minAdv ) AS is_entry
      FROM ratioed
    )
    SELECT
      ticker, date, adj_open, adj_high, adj_low, adj_close, adj_volume,
      prev_adj_close, avg_volume_4w, avg_dollar_volume_4w, prior_idx,
      hi_252_prior, low_15_prior, atr_pct_14, range_pct_14, tightness_14,
      pct_up, rvol, is_entry
    FROM flagged
    WHERE date >= $start AND date <= $end
    ORDER BY date
    """

/// Tickers that pass the security-type filter. CS/ADRC only when tradableOnly;
/// otherwise every ticker with daily data. Done once up front so per-ticker
/// signal queries don't each re-scan ticker_reference.
let eligibleTickers (conn: IDbConnection) (tradableOnly: bool) : string[] =
    let sql =
        if tradableOnly then
            // INTERSECT with daily data presence so we don't query tickers that
            // have a reference row but no prices.
            """
            SELECT DISTINCT p.ticker
            FROM split_adjusted_prices p
            JOIN ticker_reference r ON r.ticker = p.ticker
            WHERE r.type IN ('CS','ADRC')
            ORDER BY 1
            """
        else
            "SELECT DISTINCT ticker FROM split_adjusted_prices ORDER BY 1"
    conn.Query<string>(sql) |> Seq.toArray

/// Load the full per-day signal series for one ticker, ordered by date.
let loadTicker (conn: IDbConnection) (cfg: Config) (ticker: string) : SignalRow[] =
    let sql = query cfg.LookbackHigh cfg.StopLowWindow
    conn.Query<SignalRow>(
        sql,
        {| ticker = ticker
           upThr = cfg.UpThreshold
           rvolThr = cfg.RvolThreshold
           minPriorDays = int64 cfg.MinPriorDays
           minAdv = cfg.MinAvgDollarVolume
           start = cfg.StartDate.ToString("yyyy-MM-dd")
           ``end`` = cfg.EndDate.ToString("yyyy-MM-dd") |})
    |> Seq.toArray
