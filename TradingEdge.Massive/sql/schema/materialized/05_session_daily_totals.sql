-- Per-(ticker, date) session volume + transaction totals from minute aggregates.
--
-- Session window:
--   * Regular-close days: 08:30 ET inclusive through 15:58 ET inclusive
--     (i.e. bars whose ET local time is in [08:30, 15:59) )
--   * Early-close days (NYSE/Nasdaq 1:00 PM ET half-days):
--     08:30 ET inclusive through 12:58 ET inclusive
--     (i.e. bars whose ET local time is in [08:30, 12:59) )
--
-- We deliberately exclude the final bar of the session (15:59-16:00 on regular
-- days, 12:59-13:00 on half-days) because the closing auction produces a single
-- lumpy print that distorts downstream RVOLs — same rationale as omitting the
-- premarket from the volume profile.
--
-- Early-close calendar is hardcoded here to match TradingEdge.Orb.Timezone.early_closes.
-- Source: NYSE Group Holiday and Early Closings Calendar.
--
-- `volume` is the raw share count for the bar; we sum it unchanged. Split
-- adjustment applies only to the 4w average (see 06_session_volume_4w.sql),
-- matching the convention used for daily_prices.volume vs. adj_volume.
-- `transactions` is a count of distinct trades in the bar; split factors do
-- not affect counts, so no adjustment is needed anywhere downstream.
DROP TABLE IF EXISTS session_daily_totals;
CREATE TABLE session_daily_totals AS
WITH early_closes(dt) AS (
    VALUES
        (DATE '2023-07-03'),
        (DATE '2023-11-24'),
        (DATE '2024-07-03'),
        (DATE '2024-11-29'),
        (DATE '2024-12-24'),
        (DATE '2025-07-03'),
        (DATE '2025-11-28'),
        (DATE '2025-12-24'),
        (DATE '2026-11-27'),
        (DATE '2026-12-24'),
        (DATE '2027-11-26'),
        (DATE '2028-07-03'),
        (DATE '2028-11-24')
),
session_bars AS (
    SELECT
        m.ticker,
        to_timestamp(m.window_start::DOUBLE / 1e9) AT TIME ZONE 'America/New_York' AS et_ts,
        m.volume,
        m.transactions
    FROM read_parquet('data/minute_aggs/*.parquet') m
)
SELECT
    sb.ticker,
    CAST(sb.et_ts AS DATE)       AS date,
    SUM(sb.volume)::BIGINT       AS session_raw_volume,
    SUM(sb.transactions)::BIGINT AS session_transactions
FROM session_bars sb
LEFT JOIN early_closes ec ON CAST(sb.et_ts AS DATE) = ec.dt
WHERE (hour(sb.et_ts) * 60 + minute(sb.et_ts)) >= (8 * 60 + 30)
  AND (hour(sb.et_ts) * 60 + minute(sb.et_ts)) <=
      CASE WHEN ec.dt IS NOT NULL THEN (12 * 60 + 58) ELSE (15 * 60 + 58) END
GROUP BY sb.ticker, CAST(sb.et_ts AS DATE);
