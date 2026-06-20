CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_rvol_sub1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price-1.0) ret FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5 AND raw.pct_up_at_entry>=0.10;

.mode box
SELECT '== return DISPERSION: >15 vs 6-15 (deal-locked = low dispersion?) ==' z;
SELECT CASE WHEN rvol_at_entry>=15 THEN '>15' WHEN rvol_at_entry>=6 THEN '6-15' END grp,
  COUNT(*) n, ROUND(100*MEDIAN(ret),2) med, ROUND(100*AVG(ret),2) mean,
  ROUND(100*STDDEV(ret),2) sd, ROUND(100*quantile_cont(ret,0.1),1) p10, ROUND(100*quantile_cont(ret,0.9),1) p90,
  ROUND(100.0*AVG(CASE WHEN ABS(ret)<0.02 THEN 1 ELSE 0 END),1) pct_flat_2pct
FROM t WHERE rvol_at_entry>=6 GROUP BY 1 ORDER BY 1;

SELECT '== are >15 entries clustered in few names? ==' z;
SELECT COUNT(*) n_trips, COUNT(DISTINCT symbol) n_symbols,
  ROUND(100.0*AVG(pct_up_at_entry),1) avg_move_pct, ROUND(AVG(rvol_at_entry),1) avg_rvol, ROUND(MAX(rvol_at_entry),1) max_rvol
FROM t WHERE rvol_at_entry>=15;
SELECT '== top-20 highest-rvol >15 trades (deal candidates = flat post-entry) ==' z;
SELECT symbol, entry_date, ROUND(rvol_at_entry,0) rvol, ROUND(100*pct_up_at_entry,0) move_pct,
  ROUND(entry_price,2) entry, ROUND(exit_price,2) exit, ROUND(100*ret,1) ret_pct
FROM t WHERE rvol_at_entry>=15 ORDER BY rvol_at_entry DESC LIMIT 20;
