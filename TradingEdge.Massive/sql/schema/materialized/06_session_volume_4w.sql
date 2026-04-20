-- 4-week rolling averages of session-scoped (08:30-15:58 ET) volume and
-- transaction counts, plus per-day RVOLs.
--
-- Split-adjustment convention mirrors 01_split_adjusted_prices.sql:
--   * session_raw_volume is the unadjusted share count on the trading day.
--   * session_adj_volume = session_raw_volume * cum_future_split_factor, so
--     that pre- and post-split days are directly comparable in share units.
--   * The 4w average (avg_session_adj_volume_4w) is computed over split-
--     adjusted values so a split in the lookback window doesn't discontinuously
--     halve the baseline.
-- Transactions are counts of distinct trades; splits do not affect counts,
-- so session_transactions is used as-is on both sides of the ratio.
--
-- The 4w window excludes the current day
-- (RANGE BETWEEN INTERVAL 28 DAYS PRECEDING AND INTERVAL 1 DAY PRECEDING)
-- to match the no-leakage convention established in 04_stock_volume_4w.sql.
--
-- Minimum-sample guard: a 28-day calendar window nominally contains 20 US
-- trading days, but holidays reduce that — e.g. the window ending the day
-- before a Thursday following Thanksgiving + an early close + MLK day can
-- drop to 17. We require at least 16 observed trading days in the window
-- before publishing an average/RVOL; below that, the columns are NULL and
-- downstream consumers (the stocks-in-play filter, the MiniZinc exporter)
-- drop the row. This guards against one-off flukes on early-history days
-- where a brand-new ticker has only a handful of samples in its lookback.
DROP TABLE IF EXISTS session_volume_4w;
CREATE TABLE session_volume_4w AS
WITH split_boundaries AS (
    SELECT
        ticker,
        execution_date,
        EXP(SUM(LN(split_ratio)) OVER (
            PARTITION BY ticker
            ORDER BY execution_date ASC
            ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING
        )) AS cum_split_factor
    FROM splits
),
adj AS (
    SELECT
        sdt.ticker,
        sdt.date,
        sdt.session_raw_volume,
        sdt.session_transactions,
        CAST(
            sdt.session_raw_volume * COALESCE(sb.cum_split_factor, 1.0)
            AS BIGINT
        ) AS session_adj_volume
    FROM session_daily_totals sdt
    ASOF LEFT JOIN split_boundaries sb
        ON sdt.ticker = sb.ticker
        AND sdt.date  < sb.execution_date
),
rolled AS (
    SELECT
        ticker,
        date,
        session_raw_volume,
        session_adj_volume,
        session_transactions,
        COUNT(*)                                  OVER w AS n_days_4w,
        AVG(session_adj_volume::DOUBLE)           OVER w AS raw_avg_vol_4w,
        AVG(session_transactions::DOUBLE)         OVER w AS raw_avg_txn_4w
    FROM adj
    WINDOW w AS (
        PARTITION BY ticker
        ORDER BY date
        RANGE BETWEEN INTERVAL 28 DAYS PRECEDING AND INTERVAL 1 DAY PRECEDING
    )
)
SELECT
    ticker,
    date,
    session_raw_volume,
    session_adj_volume,
    session_transactions,
    CASE WHEN n_days_4w >= 16 THEN raw_avg_vol_4w END AS avg_session_adj_volume_4w,
    CASE WHEN n_days_4w >= 16 THEN raw_avg_txn_4w END AS avg_session_transactions_4w,
    CASE WHEN n_days_4w >= 16
         THEN session_adj_volume::DOUBLE / NULLIF(raw_avg_vol_4w, 0)
    END AS session_volume_rvol,
    CASE WHEN n_days_4w >= 16
         THEN session_transactions::DOUBLE / NULLIF(raw_avg_txn_4w, 0)
    END AS session_transaction_rvol,
    n_days_4w
FROM rolled;
