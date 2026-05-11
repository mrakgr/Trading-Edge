-- MTM decomposition for the long-MA ShortFadeMA mirror (pr=0.14, ma=4374h).
-- Same structure as mtm_shortfade_mirror.sql.

CREATE OR REPLACE TEMP TABLE anchors AS
SELECT symbol, month, anchor_close
FROM read_parquet('data/crypto/cumsum_z_no_gate/anchors.parquet');

CREATE OR REPLACE TEMP TABLE trips AS
SELECT 'shortlongma' AS variant, symbol, side, entry_price, exit_price, effective_notional, fees,
       funding_pnl, net_pnl,
       effective_notional / entry_price AS qty,
       TO_TIMESTAMP(entry_us/1e6)::DATE AS entry_date,
       TO_TIMESTAMP(exit_us/1e6)::DATE  AS exit_date,
       DATE_TRUNC('month', TO_TIMESTAMP(entry_us/1e6))::DATE AS entry_month,
       DATE_TRUNC('month', TO_TIMESTAMP(exit_us/1e6))::DATE  AS exit_month,
       (TO_TIMESTAMP(exit_us/1e6)::DATE - TO_TIMESTAMP(entry_us/1e6)::DATE) + 1 AS days_held,
       ROW_NUMBER() OVER () AS trip_id
FROM read_csv_auto('data/crypto/short_fade_ma_mirror_xlong/results_trips_1m_pr0.14_ma4374h_cvd240m_rvol0.75_ed0.2_short.csv');

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
  (CASE WHEN month = entry_month THEN entry_price ELSE anchor_start END
   - CASE WHEN month = exit_month THEN exit_price ELSE anchor_end END) * qty
  - (CASE WHEN month = entry_month AND month = exit_month THEN fees
          WHEN month = entry_month OR  month = exit_month THEN fees / 2.0
          ELSE 0.0 END)
  + funding_pnl * ((seg_end_date - seg_start_date + 1)::FLOAT / days_held)
  AS month_pnl,
  net_pnl
FROM joined;

SELECT 'recon' AS check,
       ROUND(SUM(month_pnl), 2) AS mtm_sum,
       ROUND((SELECT SUM(net_pnl) FROM trips), 2) AS net_pnl_sum,
       ROUND(SUM(month_pnl) - (SELECT SUM(net_pnl) FROM trips), 2) AS err
FROM monthly;

COPY (
  SELECT variant, month, ROUND(SUM(month_pnl), 2) AS pnl
  FROM monthly GROUP BY 1, 2 ORDER BY 1, 2
) TO 'data/crypto/short_fade_ma_mirror_xlong/mtm_monthly_pr0.14_ma4374h.csv' (HEADER, DELIMITER ',');
