-- Materialized table for split-and-dividend-adjusted prices
-- Uses subtractive method: adj_price = split_adj_price - cumulative_future_dividends
-- Only stores adjusted values, not original OHLCV (available in daily_prices)
DROP TABLE IF EXISTS split_adjusted_prices;
CREATE TABLE split_adjusted_prices AS

-- Step 1: Compute per-ticker cumulative split factors using window functions
WITH split_boundaries AS (
    -- For each split, compute the cumulative product of all split ratios
    -- from that date forward (reverse cumulative product)
    SELECT
        ticker,
        execution_date,
        EXP(SUM(LN(split_ratio)) OVER (
            PARTITION BY ticker
            ORDER BY execution_date DESC
            ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING
        )) AS cum_split_factor
    FROM splits
),
-- Step 2: Pre-compute split-adjusted dividend amounts using ASOF join
div_split_adjusted AS (
    SELECT
        d.ticker,
        d.ex_dividend_date,
        d.cash_amount / COALESCE(sb.cum_split_factor, 1.0) AS adj_cash_amount
    FROM dividends d
    ASOF LEFT JOIN split_boundaries sb
        ON d.ticker = sb.ticker
        AND d.ex_dividend_date < sb.execution_date
),
-- Step 3: Reverse cumulative sum of future dividends
div_cumulative AS (
    SELECT
        ticker,
        ex_dividend_date,
        SUM(adj_cash_amount) OVER (
            PARTITION BY ticker
            ORDER BY ex_dividend_date DESC
            ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING
        ) AS cum_future_dividends
    FROM div_split_adjusted
),
-- Step 4: Join prices with split boundaries via ASOF to get split factor
price_with_splits AS (
    SELECT
        dp.ticker,
        dp.date,
        dp.open,
        dp.high,
        dp.low,
        dp.close,
        dp.volume,
        COALESCE(sb.cum_split_factor, 1.0) AS split_adj_factor
    FROM daily_prices dp
    ASOF LEFT JOIN split_boundaries sb
        ON dp.ticker = sb.ticker
        AND dp.date < sb.execution_date
)
-- Step 5: Apply split adjustment and dividend adjustment
SELECT
    ps.ticker,
    ps.date,
    ps.open / ps.split_adj_factor - COALESCE(dc.cum_future_dividends, 0.0) AS adj_open,
    ps.high / ps.split_adj_factor - COALESCE(dc.cum_future_dividends, 0.0) AS adj_high,
    ps.low / ps.split_adj_factor - COALESCE(dc.cum_future_dividends, 0.0) AS adj_low,
    ps.close / ps.split_adj_factor - COALESCE(dc.cum_future_dividends, 0.0) AS adj_close,
    CAST(ps.volume * ps.split_adj_factor AS BIGINT) AS adj_volume
FROM price_with_splits ps
ASOF LEFT JOIN div_cumulative dc
    ON ps.ticker = dc.ticker
    AND ps.date < dc.ex_dividend_date;
