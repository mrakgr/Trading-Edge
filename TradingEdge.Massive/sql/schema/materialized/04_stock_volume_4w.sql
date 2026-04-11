-- Materialized table for 4-week average share volume AND dollar volume.
-- Uses RANGE window with 28-day interval to handle gaps correctly.
DROP TABLE IF EXISTS stock_dollar_volume_4w;
DROP TABLE IF EXISTS stock_volume_4w;
CREATE TABLE stock_volume_4w AS
SELECT
    ticker,
    date,
    AVG(adj_volume) OVER (
        PARTITION BY ticker
        ORDER BY date
        RANGE BETWEEN INTERVAL 28 DAYS PRECEDING AND CURRENT ROW
    ) AS avg_volume_4w,
    AVG(adj_close * adj_volume) OVER (
        PARTITION BY ticker
        ORDER BY date
        RANGE BETWEEN INTERVAL 28 DAYS PRECEDING AND CURRENT ROW
    ) AS avg_dollar_volume_4w
FROM split_adjusted_prices;
