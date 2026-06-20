CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_rvol_sub1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price - 1.0) AS ret FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.pct_up_at_entry>=0.10;

.mode box
SELECT '== rvol bands incl. SUB-1 (move>=10, caps on) ==' z;
SELECT CASE WHEN rvol_at_entry<0.5 THEN '01: <0.5' WHEN rvol_at_entry<0.75 THEN '02: 0.5-0.75'
            WHEN rvol_at_entry<1.0 THEN '03: 0.75-1' WHEN rvol_at_entry<1.5 THEN '04: 1-1.5'
            WHEN rvol_at_entry<2.0 THEN '05: 1.5-2' WHEN rvol_at_entry<3.0 THEN '06: 2-3'
            WHEN rvol_at_entry<5.0 THEN '07: 3-5' WHEN rvol_at_entry<9.0 THEN '08: 5-9'
            WHEN rvol_at_entry<15.0 THEN '09: 9-15' ELSE '10: 15+' END AS band,
  COUNT(*) n, ROUND(100*MEDIAN(ret),2) med_ret_pct, ROUND(100*AVG(ret),2) mean_ret_pct,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) winr,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(100*MEDIAN(CASE WHEN entry_date>=DATE '2015-01-01' THEN ret END),2) med_post
FROM t GROUP BY 1 ORDER BY 1;

SELECT '== sub-1 vs >=1 summary ==' z;
SELECT CASE WHEN rvol_at_entry<1.0 THEN 'rvol < 1' ELSE 'rvol >= 1' END grp,
  COUNT(*) n, ROUND(100*MEDIAN(ret),2) med_ret_pct, ROUND(100*AVG(ret),2) mean_ret_pct,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) winr,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t GROUP BY 1 ORDER BY 1;
