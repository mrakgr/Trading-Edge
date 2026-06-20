CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_grid_loose.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.* FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.rvol_at_entry>=6 AND raw.rvol_at_entry<=20;

CREATE OR REPLACE TEMP MACRO m(flo) AS TABLE
SELECT COUNT(*) n, ROUND(100.0*AVG(CASE WHEN net_pnl>0 THEN 1 ELSE 0 END),1) winr, ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(net_pnl),0) tot,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t WHERE pct_up_at_entry >= flo;

.mode box
SELECT '0.10 (current)' floor, * FROM m(0.10);
SELECT '0.125' floor, * FROM m(0.125);
SELECT '0.15' floor, * FROM m(0.15);
SELECT '0.175' floor, * FROM m(0.175);
SELECT '0.20' floor, * FROM m(0.20);
SELECT '0.25' floor, * FROM m(0.25);
SELECT '0.30' floor, * FROM m(0.30);
SELECT '0.40' floor, * FROM m(0.40);
