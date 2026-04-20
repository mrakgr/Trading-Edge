-- Intraday 10-second cumulative volume + transactions, materialized for
-- fast per-bucket scans.
--
-- Source: the per-day parquet files under data/bulk/intraday_10s/, already
-- filtered via scripts/build_10s_bars.fsx (size>0, SIP-delta<=50ms,
-- condition-code filter). volume/trade_count match what the live ORB
-- system sees.
--
-- Transpose: ORDER BY bucket, ticker, date. DuckDB preserves insert order
-- in row-group chunks and uses zone-maps for predicate pushdown, so
-- `WHERE bucket = N` becomes a contiguous range scan of ~1/2700 of the
-- table — the query shape the MiniZinc exporter uses for every checkpoint.
--
-- Universe filter is NOT applied here. Consumers that need CS/ADRC + $25M
-- ADV should join against stock_volume_4w + ticker_reference themselves.
--
-- Columns:
--   date, ticker, bucket         -- bucket 0 = 08:30 ET, 10s increments
--   volume, trade_count          -- this bucket only
--   cum_volume, cum_trade_count  -- running sum within (ticker, date)
DROP TABLE IF EXISTS intraday_10s_cum;
CREATE TABLE intraday_10s_cum AS
SELECT
    date,
    ticker,
    bucket,
    volume,
    trade_count,
    SUM(volume) OVER (
        PARTITION BY ticker, date
        ORDER BY bucket
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) AS cum_volume,
    SUM(trade_count) OVER (
        PARTITION BY ticker, date
        ORDER BY bucket
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) AS cum_trade_count
FROM read_parquet('data/bulk/intraday_10s/*.parquet')
ORDER BY bucket, ticker, date;
