-- Intraday 10-second cumulative volume + transactions, computed on demand
-- from the per-day parquet files under data/bulk/intraday_10s/.
--
-- Instead of materializing ~1B rows into trading.db, we expose a view that
-- reads the parquets and computes cumulatives via a window function at query
-- time. DuckDB's Parquet reader pushes date/ticker predicates down into the
-- file scan, so consumer queries that filter to a single date + single
-- ticker (or a small set) don't pay for the whole dataset.
--
-- Raw vs filtered: the per-day parquets are ALREADY filtered via the ORB
-- filter (scripts/build_10s_bars.fsx: size>0, SIP-delta<=50ms, conditions
-- filter). So the volume/trade_count columns here match what the live ORB
-- system will see.
--
-- Universe filter is NOT applied in this view. Consumers that need
-- CS/ADRC + $25M ADV should join against stock_volume_4w + ticker_reference
-- themselves. That way the view stays maximally general.
--
-- Columns:
--   date, ticker, bucket      -- bucket 0 = 08:30 ET, 10s increments
--   volume, trade_count       -- this bucket only
--   cum_volume, cum_trade_count -- running sum within (ticker, date)
DROP VIEW IF EXISTS intraday_10s_cum;
CREATE VIEW intraday_10s_cum AS
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
FROM read_parquet('data/bulk/intraday_10s/*.parquet');
