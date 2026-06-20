CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_rvol_wide.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price - 1.0) AS ret FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.pct_up_at_entry>=0.10;

.mode box
SELECT '== rvol DECILES — median vs mean return (move>=10, caps on) ==' z;
WITH q AS (SELECT *, NTILE(10) OVER (ORDER BY rvol_at_entry) decile FROM t)
SELECT decile, ROUND(MIN(rvol_at_entry),2) rvol_lo, ROUND(MAX(rvol_at_entry),2) rvol_hi, COUNT(*) n,
  ROUND(100*MEDIAN(ret),2) med_ret_pct,
  ROUND(100*AVG(ret),2) mean_ret_pct,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) winr,
  ROUND(100*MEDIAN(CASE WHEN entry_date<DATE '2015-01-01' THEN ret END),2) med_pre,
  ROUND(100*MEDIAN(CASE WHEN entry_date>=DATE '2015-01-01' THEN ret END),2) med_post,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf
FROM q GROUP BY decile ORDER BY decile;
