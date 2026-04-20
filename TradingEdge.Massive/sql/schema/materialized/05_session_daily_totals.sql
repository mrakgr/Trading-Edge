-- Per-(ticker, date) session volume + transaction totals, aggregated from
-- our filtered 10s bars under data/bulk/intraday_10s/. The bar builder
-- (scripts/build_all_10s_bars.fsx) already restricts to the session window
-- and applies the ORB live-system trade filter (size>0, SIP-delta<=50ms,
-- condition-code filter), so a single GROUP BY per (ticker, date) gives us
-- the totals.
--
-- We read the raw per-bar parquets directly rather than the intraday_10s_cum
-- view because materialized tables are executed before views, and reading
-- the same files twice is cheap (~a few seconds per 70-day aggregate).
--
-- Session window (baked into the bar file at build time, see
-- scripts/build_all_10s_bars.fsx):
--   * Regular-close days: [08:30, 15:59) ET  (buckets 0..2693)
--   * Early-close days:   [08:30, 12:59) ET  (buckets 0..1613)
-- The closing-auction minute (15:59 / 12:59) is deliberately dropped to
-- avoid the auction's lumpy print distorting downstream RVOLs.
--
-- Split-adjustment convention:
--   `volume` is the raw share count — we sum unchanged. Split adjustment
--   applies only to the 4w average downstream (06_session_volume_4w.sql),
--   matching daily_prices.volume vs. adj_volume.
--   `trade_count` is a count of filtered trades; splits do not affect counts,
--   so no adjustment anywhere downstream. The `session_transactions` column
--   name is preserved for downstream compatibility, but the numbers now
--   reflect *filtered* trades, not raw Polygon minute-agg print counts.
DROP TABLE IF EXISTS session_daily_totals;
CREATE TABLE session_daily_totals AS
SELECT
    ticker,
    date,
    SUM(volume)::BIGINT      AS session_raw_volume,
    SUM(trade_count)::BIGINT AS session_transactions
FROM read_parquet('data/bulk/intraday_10s/*.parquet')
GROUP BY ticker, date;
