CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_grid_loose.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.* FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.rvol_at_entry>=6 AND raw.rvol_at_entry<=20
  AND raw.pct_up_at_entry>=0.10;       -- current floor; bucket WITHIN

.mode box
SELECT '== DECILES of entry-day move (prod gate) ==' z;
WITH q AS (SELECT *, NTILE(10) OVER (ORDER BY pct_up_at_entry) decile FROM t)
SELECT decile, ROUND(100*MIN(pct_up_at_entry),1) lo_pct, ROUND(100*MAX(pct_up_at_entry),1) hi_pct, COUNT(*) n,
  ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY decile ORDER BY decile;

SELECT '== FIXED move bands (prod gate) — the high end resolved ==' z;
SELECT CASE WHEN pct_up_at_entry<0.15 THEN '1: 10-15%' WHEN pct_up_at_entry<0.20 THEN '2: 15-20%'
            WHEN pct_up_at_entry<0.25 THEN '3: 20-25%' WHEN pct_up_at_entry<0.30 THEN '4: 25-30%'
            WHEN pct_up_at_entry<0.40 THEN '5: 30-40%' WHEN pct_up_at_entry<0.55 THEN '6: 40-55%'
            ELSE '7: 55%+' END AS band,
  ROUND(100*MIN(pct_up_at_entry),1) lo, ROUND(100*MAX(pct_up_at_entry),1) hi, COUNT(*) n,
  ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t GROUP BY 1 ORDER BY 1;
