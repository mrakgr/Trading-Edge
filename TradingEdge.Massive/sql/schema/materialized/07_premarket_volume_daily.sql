-- Per-(ticker, date) premarket totals, aggregated from the 1m bar parquets
-- under data/minute_bars_1m/.
--
-- "Premarket" = 04:00-09:29 ET inclusive (buckets 0..329). Bucket 330 is the
-- 09:30 RTH opening auction print and is deliberately excluded — the auction
-- is the boundary into regular trading, not part of premarket.
--
-- The bar builder (TradingEdge.Massive.MinuteBarsBuild) already applies the
-- canonical lit-only filter (size > 0, trf_id = 0, conditions OK) and bucket
-- aligns to 04:00 ET DST-correctly, so a single GROUP BY does the job.
--
-- Date is derived from the filename rather than start_ns to avoid a per-row
-- timezone conversion. The bar builder names every file {YYYY-MM-DD}.parquet
-- where the date matches the local-ET trading day of bucket 0.
DROP TABLE IF EXISTS premarket_volume_daily;
CREATE TABLE premarket_volume_daily AS
SELECT
    ticker,
    regexp_extract(filename, '([0-9]{4}-[0-9]{2}-[0-9]{2})\.parquet', 1)::DATE
                                          AS date,
    SUM(volume)::BIGINT                   AS pm_volume,
    SUM(dollar_volume)                    AS pm_dollar_volume,
    SUM(trade_count)::BIGINT              AS pm_trade_count,
    SUM(dollar_volume) / SUM(volume)      AS pm_vwap,
    MAX(high)                             AS pm_high,
    MIN(low)                              AS pm_low
FROM read_parquet('data/minute_bars_1m/*.parquet', filename=true)
WHERE bucket < 330
GROUP BY ticker, date;

-- Primary-key surrogate: a unique index lets downstream joins use the
-- (ticker, date) pair efficiently. DuckDB doesn't enforce uniqueness here at
-- write time (CTAS doesn't), so the index is the only place the constraint
-- shows up. The GROUP BY guarantees uniqueness in practice.
CREATE UNIQUE INDEX premarket_volume_daily_ticker_date
    ON premarket_volume_daily (ticker, date);
