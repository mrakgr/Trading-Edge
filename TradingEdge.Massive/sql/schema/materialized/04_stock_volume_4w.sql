-- Materialized table for 4-week average share volume AND dollar volume.
-- Uses RANGE window with 28-day interval to handle gaps correctly.
--
-- The window EXCLUDES the current day (BETWEEN INTERVAL 28 DAYS PRECEDING
-- AND INTERVAL 1 DAY PRECEDING) so that consumers like the rvol formula
-- compare today's volume against a clean prior baseline. Including today
-- in its own denominator silently dilutes RVOL on high-volume days (e.g.
-- a true 10x day computes as ~7.6x).
--
-- Brand-new tickers with fewer than 1 prior trading day get NULL averages,
-- which propagate to NULL rvol and are filtered out by stocks_in_play's
-- `rvol >= min_rvol` predicate. That's the correct behavior -- a stock
-- with no baseline shouldn't be flagged as "in play".
DROP TABLE IF EXISTS stock_dollar_volume_4w;
DROP TABLE IF EXISTS stock_volume_4w;
CREATE TABLE stock_volume_4w AS
SELECT
    ticker,
    date,
    AVG(adj_volume) OVER (
        PARTITION BY ticker
        ORDER BY date
        RANGE BETWEEN INTERVAL 28 DAYS PRECEDING AND INTERVAL 1 DAY PRECEDING
    ) AS avg_volume_4w,
    AVG(adj_close * adj_volume) OVER (
        PARTITION BY ticker
        ORDER BY date
        RANGE BETWEEN INTERVAL 28 DAYS PRECEDING AND INTERVAL 1 DAY PRECEDING
    ) AS avg_dollar_volume_4w
FROM split_adjusted_prices;
