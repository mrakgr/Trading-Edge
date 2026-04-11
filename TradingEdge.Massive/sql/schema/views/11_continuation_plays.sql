-- Continuation Plays: for each breakout in stocks_in_play, walk forward day by
-- day and emit one row per trading day in the chain. The breakout day itself
-- is included (with number_of_days_since_breakout = 0), then every subsequent
-- trading day whose RVOL stays above min_rvol_fraction * the breakout RVOL.
-- The first day the condition is violated is ALSO included (so callers can
-- see where the chain died), but no days after it.
--
-- Each row carries `number_of_days_since_breakout` (0 for the breakout day,
-- 1 for the first continuation, etc.) so downstream consumers can filter
-- breakouts vs follow-through days from a single unified list.
--
-- All stocks_in_play parameters are forwarded so the same filters apply to
-- the underlying breakout source. Two new params control the walk:
--   min_rvol_fraction := 0.5    -- continuation if rvol > 0.5 * breakout_rvol
--   max_horizon_days  := 30     -- safety cap on chain length
DROP MACRO IF EXISTS continuation_plays;
CREATE MACRO continuation_plays(
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
    min_atr_ratio := 0.55,
    min_rvol_fraction := 0.5,
    max_horizon_days := 30
) AS TABLE
WITH breakouts AS (
    SELECT
        ticker,
        date AS breakout_date,
        rvol AS breakout_rvol
    FROM stocks_in_play(
        start_date := start_date,
        end_date := end_date,
        min_rvol := min_rvol,
        min_gap_pct := min_gap_pct,
        min_avg_dollar_volume := min_avg_dollar_volume,
        rvol_weight := rvol_weight,
        gap_weight := gap_weight,
        exclude_etfs := exclude_etfs,
        pre_window_days := pre_window_days,
        post_window_days := post_window_days,
        min_atr_ratio := min_atr_ratio
    )
),
-- All trading days from the breakout day up to `max_horizon_days` calendar
-- days later. The breakout day itself is included (offset 0).
candidate_days AS (
    SELECT
        b.ticker,
        b.breakout_date,
        b.breakout_rvol,
        p.date,
        p.adj_volume AS volume,
        v.avg_volume_4w,
        v.avg_dollar_volume_4w,
        (p.adj_close * p.adj_volume) / NULLIF(v.avg_dollar_volume_4w, 0) AS rvol,
        ((p.adj_close * p.adj_volume) / NULLIF(v.avg_dollar_volume_4w, 0))
            > (min_rvol_fraction * b.breakout_rvol) AS qualifies
    FROM breakouts b
    JOIN split_adjusted_prices p
        ON p.ticker = b.ticker
        AND p.date >= b.breakout_date
        AND p.date <= b.breakout_date + INTERVAL (max_horizon_days) DAY
    JOIN stock_volume_4w v
        ON v.ticker = p.ticker
        AND v.date  = p.date
),
with_offset AS (
    SELECT
        *,
        -- Trading-day offset from the breakout day. Always 0 for the breakout
        -- itself (since the breakout day is the lowest date in each partition
        -- and ROW_NUMBER starts at 1).
        ROW_NUMBER() OVER (
            PARTITION BY ticker, breakout_date
            ORDER BY date
        ) - 1 AS number_of_days_since_breakout
    FROM candidate_days
),
with_first_failure AS (
    SELECT
        *,
        -- For each chain, the date of the first day that failed the qualifies
        -- check. The breakout day itself can never fail (rvol > 0.5 * rvol is
        -- always true for positive rvol), so this only flags continuation days.
        -- NULL means every candidate day qualified within the horizon.
        MIN(CASE WHEN NOT qualifies THEN date END)
            OVER (PARTITION BY ticker, breakout_date) AS first_failure_date
    FROM with_offset
)
SELECT
    ticker,
    breakout_date,
    breakout_rvol,
    date,
    number_of_days_since_breakout,
    rvol,
    volume,
    avg_volume_4w,
    avg_dollar_volume_4w
FROM with_first_failure
-- Include the breakout day, all qualifying continuation days, AND the first
-- failing day (so the caller can see where the chain died). Stop strictly
-- after the first failure.
WHERE first_failure_date IS NULL OR date <= first_failure_date
ORDER BY breakout_date, ticker, date;
