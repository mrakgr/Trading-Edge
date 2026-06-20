CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_rvol_sub1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price-1.0) ret FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.pct_up_at_entry>=0.10 AND raw.rvol_at_entry>=5;
.mode box
WITH g AS (
  SELECT 'A: rvol[5,15], move uncapped (CURRENT)' lbl, * FROM t WHERE rvol_at_entry<15
  UNION ALL SELECT 'B: rvol>=5 uncapped, move<30% (PROPOSED)', * FROM t WHERE pct_up_at_entry<0.30
  UNION ALL SELECT 'C: rvol[5,15] AND move<30% (BOTH)', * FROM t WHERE rvol_at_entry<15 AND pct_up_at_entry<0.30
)
SELECT lbl, COUNT(*) n, ROUND(100*MEDIAN(ret),2) med,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(net_pnl),0) tot,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM g GROUP BY lbl ORDER BY lbl;
