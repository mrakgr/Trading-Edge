-- Stocks In Play: Top 10 stocks per day based on relative volume, gap, and liquidity
--
-- Filters applied (in order):
--   1. RVOL, gap, dollar-volume thresholds (the original SIP definition)
--   2. ETF exclusion via LEFT ANTI JOIN against ticker_reference (if exclude_etfs = true
--      and the table is non-empty)
--   3. Buyout filter: a stock whose post-event ATR collapses is treated as acquired/dead
--      and excluded. ATR = mean of true_range / close over the window, where
--      true_range = max(high-low, abs(high-prev_close), abs(low-prev_close)).
--      The breakout day is excluded from both windows -- pre = [date-N, date-1],
--      post = [date+1, date+M] -- so the gap event itself doesn't pollute either side.
--
-- The pre/post ATR columns are returned in the result set so callers can inspect
-- and tune the threshold.
DROP MACRO IF EXISTS stocks_in_play;
CREATE MACRO stocks_in_play(
    start_date := DATE '1900-01-01',
    end_date := DATE '2999-12-31',
    min_rvol := 3,
    min_gap_pct := 0.05,
    min_avg_dollar_volume := 100000000,
    rvol_weight := 0.95,
    gap_weight := 0.05,
    exclude_etfs := true,
    pre_window_days := 20,
    post_window_days := 5,
    min_atr_ratio := 0.55
) AS TABLE
WITH daily_metrics AS (
    SELECT
        p.ticker,
        p.date,
        p.adj_open,
        p.adj_high,
        p.adj_low,
        p.adj_close,
        p.adj_volume,
        p_prev.adj_close AS prev_close,
        v.avg_dollar_volume_4w,
        -- Opening gap: % change from previous close to today's open
        (p.adj_open - p_prev.adj_close) / p_prev.adj_close AS gap_pct,
        -- Daily range as % of price
        (p.adj_high - p.adj_low) / p.adj_low AS range_pct,
        -- Today's dollar volume
        p.adj_close * p.adj_volume AS dollar_volume,
        -- Relative volume: today's dollar volume vs 4-week average
        (p.adj_close * p.adj_volume) / NULLIF(v.avg_dollar_volume_4w, 0) AS rvol
    FROM split_adjusted_prices p
    JOIN trading_calendar tc ON p.date = tc.current_date
    JOIN split_adjusted_prices p_prev
        ON p_prev.ticker = p.ticker
        AND p_prev.date = tc.date_prev
    JOIN stock_dollar_volume_4w v
        ON v.ticker = p.ticker
        AND v.date = p.date
    WHERE v.avg_dollar_volume_4w >= min_avg_dollar_volume
      AND p.date >= start_date
      AND p.date <= end_date
),
filtered AS (
    SELECT *
    FROM daily_metrics dm
    WHERE dm.rvol >= min_rvol
      AND ABS(dm.gap_pct) >= min_gap_pct
      -- ETF exclusion: keep only tickers NOT in ticker_reference (when enabled)
      AND (
          NOT exclude_etfs
          OR NOT EXISTS (
              SELECT 1 FROM ticker_reference tr WHERE tr.ticker = dm.ticker
          )
      )
),
-- Pre/post ATR averages computed only for the (small) filtered set.
-- ATR = mean of true_range / close, where true_range needs the previous day's close.
-- We get prev_close via a join through trading_calendar so that gaps over weekends/
-- holidays are handled correctly.
--
-- Pre window:  [date - pre_window_days, date - 2] -- excludes the breakout day AND
--              the day before it. The day-before is excluded because any pre-event
--              rumor leakage (premarket positioning, options activity) tends to
--              distort that day's range and would pollute the "normal" baseline.
-- Post window: [date + 1, date + post_window_days] -- excludes the breakout day
--              itself, since that's the gap event we're measuring against.
with_atrs AS (
    SELECT
        f.*,
        (
            SELECT AVG(
                GREATEST(
                    p.adj_high - p.adj_low,
                    ABS(p.adj_high - p_prev.adj_close),
                    ABS(p.adj_low - p_prev.adj_close)
                ) / NULLIF(p.adj_close, 0)
            )
            FROM split_adjusted_prices p
            JOIN trading_calendar tc
                ON tc.current_date = p.date
            JOIN split_adjusted_prices p_prev
                ON p_prev.ticker = p.ticker
                AND p_prev.date = tc.date_prev
            WHERE p.ticker = f.ticker
              AND p.date >= f.date - INTERVAL (pre_window_days) DAY
              -- Exclude the breakout day AND the trading day immediately prior.
              -- Look up the prior trading day via trading_calendar so weekends and
              -- holidays don't accidentally exclude an extra day or fail to exclude
              -- when the breakout falls on a Monday.
              AND p.date < (SELECT date_prev FROM trading_calendar WHERE current_date = f.date)
        ) AS pre_atr_pct,
        (
            SELECT AVG(
                GREATEST(
                    p.adj_high - p.adj_low,
                    ABS(p.adj_high - p_prev.adj_close),
                    ABS(p.adj_low - p_prev.adj_close)
                ) / NULLIF(p.adj_close, 0)
            )
            FROM split_adjusted_prices p
            JOIN trading_calendar tc
                ON tc.current_date = p.date
            JOIN split_adjusted_prices p_prev
                ON p_prev.ticker = p.ticker
                AND p_prev.date = tc.date_prev
            WHERE p.ticker = f.ticker
              AND p.date > f.date
              AND p.date <= f.date + INTERVAL (post_window_days) DAY
        ) AS post_atr_pct
    FROM filtered f
),
with_ratio AS (
    SELECT
        *,
        CASE
            WHEN pre_atr_pct IS NULL OR pre_atr_pct = 0 THEN NULL
            ELSE post_atr_pct / pre_atr_pct
        END AS atr_ratio
    FROM with_atrs
),
ranked AS (
    SELECT
        *,
        -- Composite score: weighted combination of RVOL and absolute gap
        (rvol * rvol_weight + ABS(gap_pct) * 100 * gap_weight) AS in_play_score,
        ROW_NUMBER() OVER (PARTITION BY date ORDER BY (rvol * rvol_weight + ABS(gap_pct) * 100 * gap_weight) DESC) AS rank
    FROM with_ratio
    -- Buyout filter: drop rows whose post-event ATR collapsed to <= min_atr_ratio
    -- of pre-event ATR. NULL ratios pass through (e.g. brand-new tickers with no
    -- pre-window data, or breakouts where the post-window has no data).
    WHERE (
        min_atr_ratio <= 0.0
        OR atr_ratio IS NULL
        OR atr_ratio > min_atr_ratio
    )
)
SELECT
    ticker,
    date,
    adj_open,
    adj_close,
    prev_close,
    gap_pct,
    range_pct,
    rvol,
    avg_dollar_volume_4w,
    pre_atr_pct,
    post_atr_pct,
    atr_ratio,
    in_play_score,
    rank
FROM ranked
WHERE rank <= 10
ORDER BY date, rank;
