-- Mark-to-market monthly P&L decomposition for cumsum-z short variants.
--
-- For each trip spanning months M0..Mk, allocates net_pnl across months so
--   sum(month_pnl over Mi for trip T) == T.net_pnl   exactly.
--
-- Components per-month:
--   price delta:   (p_start - p_end) * qty * sign     for shorts (sign=+1)
--                  (p_end - p_start) * qty            for longs
--   p_start = entry_price if entry_month, else anchor_close(symbol, month)
--   p_end   = exit_price  if exit_month,  else anchor_close(symbol, next_month)
--   fees:    full in same-month trip; half in entry month + half in exit month
--   funding: pro-rated by days held in that month / total days held
--
-- Run via: duckdb < scripts/crypto/mtm_decompose.sql
--
-- Outputs: data/crypto/cumsum_z_no_gate/mtm_monthly.csv

CREATE OR REPLACE TEMP TABLE trips AS
WITH base AS (
  SELECT 'z100'   AS variant, * FROM read_csv_auto('data/crypto/cumsum_z_baseline/results_trips_1m_th100_ls.csv')      WHERE side='short'
  UNION ALL
  SELECT 'ng200'  AS variant, * FROM read_csv_auto('data/crypto/cumsum_z_no_gate_low/results_trips_1m_th200_ls.csv')   WHERE side='short'
  UNION ALL
  SELECT 'ng450'  AS variant, * FROM read_csv_auto('data/crypto/cumsum_z_no_gate_low/results_trips_1m_th450_ls.csv')   WHERE side='short'
  UNION ALL
  SELECT 'ng4500' AS variant, * FROM read_csv_auto('data/crypto/cumsum_z_no_gate/results_trips_1m_th4500_ls.csv')      WHERE side='short'
  UNION ALL
  SELECT 'ng6750' AS variant, * FROM read_csv_auto('data/crypto/cumsum_z_no_gate/results_trips_1m_th6750_ls.csv')      WHERE side='short'
)
SELECT variant, symbol, side, entry_price, exit_price, effective_notional, fees,
       funding_pnl, net_pnl,
       effective_notional / entry_price AS qty,
       TO_TIMESTAMP(entry_us/1e6)::DATE AS entry_date,
       TO_TIMESTAMP(exit_us/1e6)::DATE  AS exit_date,
       DATE_TRUNC('month', TO_TIMESTAMP(entry_us/1e6))::DATE AS entry_month,
       DATE_TRUNC('month', TO_TIMESTAMP(exit_us/1e6))::DATE  AS exit_month,
       (TO_TIMESTAMP(exit_us/1e6)::DATE - TO_TIMESTAMP(entry_us/1e6)::DATE) + 1 AS days_held,
       ROW_NUMBER() OVER () AS trip_id
FROM base;

CREATE OR REPLACE TEMP TABLE anchors AS
SELECT symbol, month, anchor_close FROM read_parquet('data/crypto/cumsum_z_no_gate/anchors.parquet');

CREATE OR REPLACE TEMP TABLE monthly AS
WITH e AS (
  SELECT t.*, unnest(generate_series(t.entry_month, t.exit_month, INTERVAL 1 MONTH))::DATE AS month
  FROM trips t
),
joined AS (
  SELECT
    e.*,
    a_s.anchor_close AS anchor_start,
    a_e.anchor_close AS anchor_end,
    CASE WHEN e.month = e.entry_month THEN e.entry_date ELSE e.month END AS seg_start_date,
    CASE WHEN e.month = e.exit_month  THEN e.exit_date
         ELSE (e.month + INTERVAL 1 MONTH)::DATE - 1 END AS seg_end_date
  FROM e
  LEFT JOIN anchors a_s ON a_s.symbol=e.symbol AND a_s.month=e.month
  LEFT JOIN anchors a_e ON a_e.symbol=e.symbol AND a_e.month=(e.month + INTERVAL 1 MONTH)::DATE
)
SELECT
  variant, trip_id, symbol, month,
  CASE WHEN side='short'
       THEN (CASE WHEN month = entry_month THEN entry_price ELSE anchor_start END
             - CASE WHEN month = exit_month THEN exit_price ELSE anchor_end END) * qty
            - (CASE WHEN month = entry_month AND month = exit_month THEN fees
                    WHEN month = entry_month OR  month = exit_month THEN fees / 2.0
                    ELSE 0.0 END)
            + funding_pnl * ((seg_end_date - seg_start_date + 1)::FLOAT / days_held)
       ELSE (CASE WHEN month = exit_month THEN exit_price ELSE anchor_end END
             - CASE WHEN month = entry_month THEN entry_price ELSE anchor_start END) * qty
            - (CASE WHEN month = entry_month AND month = exit_month THEN fees
                    WHEN month = entry_month OR  month = exit_month THEN fees / 2.0
                    ELSE 0.0 END)
            + funding_pnl * ((seg_end_date - seg_start_date + 1)::FLOAT / days_held)
  END AS month_pnl,
  net_pnl
FROM joined;

COPY (
  SELECT variant, month, ROUND(SUM(month_pnl), 2) AS pnl
  FROM monthly
  GROUP BY 1, 2
  ORDER BY 1, 2
) TO 'data/crypto/cumsum_z_no_gate/mtm_monthly.csv' (HEADER, DELIMITER ',');

SELECT 'reconciliation' AS check, variant,
       ROUND(SUM(month_pnl), 2) AS mtm_sum,
       ROUND((SELECT SUM(net_pnl) FROM trips t2 WHERE t2.variant = m.variant), 2) AS net_pnl_sum
FROM monthly m
GROUP BY variant ORDER BY variant;
