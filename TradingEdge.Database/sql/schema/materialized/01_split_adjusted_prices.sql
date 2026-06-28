-- Materialized table for split-and-dividend-adjusted prices.
-- Splits: multiplicative cumulative factor (reverse cumulative product of ratios).
-- Dividends: MULTIPLICATIVE back-adjustment. Each ex-dividend gets a factor
--   f = 1 - adj_div / split_adj_close_on_exdate   (in (0,1) for any sane dividend),
-- and every bar BEFORE the ex-date is multiplied by the product of all such future
-- factors. This is the correct total-return back-adjustment and, unlike the old
-- subtractive method (adj = price - Σdiv), can never drive a price to <= 0.
-- All dividend types (ordinary + special) are adjusted.
-- Only stores adjusted values, not original OHLCV (available in daily_prices).
DROP TABLE IF EXISTS split_adjusted_prices;
CREATE TABLE split_adjusted_prices AS

-- Step 1: Per-ticker cumulative split factor (reverse cumulative product of ratios).
WITH split_boundaries AS (
    SELECT
        ticker,
        execution_date,
        EXP(SUM(LN(split_ratio)) OVER (
            PARTITION BY ticker
            ORDER BY execution_date ASC
            ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING
        )) AS cum_split_factor
    FROM splits
),
-- Step 2: Split-adjust each dividend's cash amount (same split scale as prices).
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
-- Step 3: Per-dividend MULTIPLICATIVE factor f = 1 - adj_div / split_adj_close_on_exdate.
--   - Use the split-adjusted close on the ex-date so the dividend and the price are on
--     the same scale (both divided by the same cum_split_factor cancels, but we keep it
--     explicit and robust).
--   - Guard: if the ex-date has no price row, or the implied factor is non-positive
--     (dividend >= price, an economic absurdity / bad data), clamp the factor to a small
--     positive epsilon so the product never zeroes or flips sign. COALESCE missing prices
--     to factor 1.0 (no adjustment) rather than dropping the dividend.
div_factor AS (
    SELECT
        dsa.ticker,
        dsa.ex_dividend_date,
        CASE
            WHEN p.close IS NULL THEN 1.0
            ELSE GREATEST(
                1.0 - dsa.adj_cash_amount
                      / NULLIF(p.close / COALESCE(sb.cum_split_factor, 1.0), 0.0),
                0.000001)
        END AS div_factor
    FROM div_split_adjusted dsa
    LEFT JOIN daily_prices p
        ON p.ticker = dsa.ticker
        AND p.date = dsa.ex_dividend_date
    ASOF LEFT JOIN split_boundaries sb
        ON dsa.ticker = sb.ticker
        AND dsa.ex_dividend_date < sb.execution_date
),
-- Step 4: Reverse cumulative PRODUCT of future dividend factors (a future dividend's
-- factor applies to all bars before its ex-date). Done in log space for stability.
div_cumulative AS (
    SELECT
        ticker,
        ex_dividend_date,
        EXP(SUM(LN(div_factor)) OVER (
            PARTITION BY ticker
            ORDER BY ex_dividend_date ASC
            ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING
        )) AS cum_future_div_factor
    FROM div_factor
),
-- Step 5: Join prices with split boundaries via ASOF to get the split factor.
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
-- Step 6: Apply split adjustment (divide) then dividend adjustment (multiply by the
-- product of all FUTURE dividend factors). A bar on/after the last ex-date has factor 1.
SELECT
    ps.ticker,
    ps.date,
    ps.open  / ps.split_adj_factor * COALESCE(dc.cum_future_div_factor, 1.0) AS adj_open,
    ps.high  / ps.split_adj_factor * COALESCE(dc.cum_future_div_factor, 1.0) AS adj_high,
    ps.low   / ps.split_adj_factor * COALESCE(dc.cum_future_div_factor, 1.0) AS adj_low,
    ps.close / ps.split_adj_factor * COALESCE(dc.cum_future_div_factor, 1.0) AS adj_close,
    CAST(ps.volume * ps.split_adj_factor AS BIGINT) AS adj_volume,
    ps.volume AS raw_volume
FROM price_with_splits ps
ASOF LEFT JOIN div_cumulative dc
    ON ps.ticker = dc.ticker
    AND ps.date < dc.ex_dividend_date;
