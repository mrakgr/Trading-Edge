-- Per-(ticker, date) session volume + transaction totals, aggregated from
-- our filtered 10s bars (intraday_10s_cum, file 03). The bar builder
-- (scripts/build_10s_bars.fsx) already restricts to the session window and
-- applies the ORB filter (size>0, SIP-delta<=50ms, condition-code filter),
-- so a single GROUP BY per (ticker, date) gives us the totals. Equivalently,
-- the final (max-bucket) cum_volume/cum_trade_count row per (ticker, date)
-- in intraday_10s_cum is the same total; we use SUM for clarity and because
-- the per-bucket `volume`/`trade_count` columns are directly in the table.
--
-- Session window (baked into the bar file at build time):
--   * Regular-close days: 08:30 ET inclusive through 15:58 ET inclusive
--     (buckets 0..2748, 10s each).
--   * Early-close days: 08:30 ET inclusive through 12:58 ET inclusive
--     (buckets 0..1608).
-- The final bar of the session is deliberately dropped to avoid the closing
-- auction's lumpy print — same rationale as omitting the premarket from the
-- volume profile. See scripts/build_10s_bars.fsx for the source of truth.
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
FROM intraday_10s_cum
GROUP BY ticker, date;
