-- Intraday 10-second cumulative volume + trade_count, exposed as a view over
-- per-day parquet files under data/bulk/intraday_10s_cum/. The cumulatives
-- are pre-computed by scripts/build_10s_cum.fsx (one parquet per day, rows
-- sorted by bucket so DuckDB's row-group zone maps turn `WHERE bucket = N`
-- into a tiny scan).
--
-- Why a view, not a materialized table:
-- The global ORDER BY bucket,ticker,date sort over ~500M rows (70 days) OOMs
-- on 16 GB RAM even with disk spill; the per-day-then-glob approach avoids
-- the global sort entirely. Per-bucket queries still complete in ~10 ms
-- because each day file is tiny (~25 MB) and bucket-zone-mapped.
--
-- Source files are already filtered by the ORB live-system trade filter
-- (size>0, SIP-delta<=50ms, condition-code exclusions); volume/trade_count
-- match what the live ORB system would see.
--
-- Universe filter is NOT applied here. Consumers that need CS/ADRC + $25M
-- ADV should join against stock_volume_4w + ticker_reference themselves.
--
-- Columns:
--   date, ticker, bucket         -- bucket 0 = 08:30 ET, 10s increments
--   volume, trade_count          -- this bucket only
--   cum_volume, cum_trade_count  -- running sum within (ticker, date)
DROP VIEW IF EXISTS intraday_10s_cum;
CREATE VIEW intraday_10s_cum AS
SELECT date, ticker, bucket, volume, trade_count, cum_volume, cum_trade_count
FROM read_parquet('data/bulk/intraday_10s_cum/*.parquet');
