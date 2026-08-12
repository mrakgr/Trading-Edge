-- daily_episodes_causal — the CAUSAL twin of `daily_episodes` (S43br).
--
-- Identical episode logic (a >45-CALENDAR-day gap starts a new episode, so no
-- rolling window can span a listing gap and a recycled symbol cannot contaminate
-- its successor), but sourced from `daily_adjusted` instead of
-- `split_adjusted_prices`. It therefore carries RAW OHLCV plus the two causal
-- factors `n` and `cum_div` rather than back-adjusted prices.
--
-- ⭐ WHY A SECOND VIEW rather than changing `daily_episodes` in place: the old
-- view's consumers (build_mr_candidate.fsx, the tideflyer_*.sql family,
-- live_scan.py) all read `adj_close`/`adj_volume` and would silently change
-- meaning. Migration is staged — see docs/price_adjustment.md.
--
-- ⚠ Consumers must NOT use these columns as if they were adjusted prices. Combine
-- them per docs/price_adjustment.md §2:
--     price of day t in day D's raw scale   ->  close(t) * n(t)/n(D)
--     volume of day t in day D's share scale ->  volume(t) * n(D)/n(t)     [reciprocal —
--                                                 so price*volume is scale-invariant]
--     return t1 -> t2  ->  [ (P2*n2 - P1*n1) + (C2 - C1) ] / (P1*n1)
--
-- A VIEW, not a table: the episode assignment is one LAG plus a running SUM, so
-- it is always fresh when daily_adjusted is rebuilt and cannot go stale.
--
-- Apply:  duckdb data/trading.db < scripts/equity/build_daily_episodes_causal_view.sql

CREATE OR REPLACE VIEW daily_episodes_causal AS
WITH marked AS (
    SELECT
        a.ticker,
        a.date,
        a.open,
        a.high,
        a.low,
        a.close,
        a.volume,
        a.n,
        a.cum_div,
        -- a break = a >45-calendar-day gap since the prior bar for this ticker.
        -- The first bar of each ticker has a NULL LAG => is_break = 0.
        CASE WHEN a.date - LAG(a.date) OVER w > 45 THEN 1 ELSE 0 END AS is_break
    FROM daily_adjusted a
    WHERE EXISTS (SELECT 1 FROM ticker_reference r
                  WHERE r.ticker = a.ticker AND r.type IN ('CS','ADRC'))
    WINDOW w AS (PARTITION BY a.ticker ORDER BY a.date)
)
SELECT
    ticker,
    date,
    open,
    high,
    low,
    close,
    volume,
    n,
    cum_div,
    SUM(is_break) OVER (PARTITION BY ticker ORDER BY date) AS episode
FROM marked;
