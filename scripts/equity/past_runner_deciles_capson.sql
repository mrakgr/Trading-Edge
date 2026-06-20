.read /tmp/feat_only.sql
CREATE OR REPLACE TEMP MACRO tripsL(measure) AS TABLE
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_grid_loose.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM read_parquet('/home/mrakgr/Trading-Edge/data/equity/momentum_v0/breadth.parquet'))
SELECT raw.net_pnl, raw.entry_date, (CASE WHEN measure='atr' THEN f.max_atr6m ELSE f.max_ret6m END) m
FROM raw JOIN br ON br.date=raw.entry_date JOIN feat f ON f.ticker=raw.symbol AND f.date=raw.signal_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5;
.mode box
SELECT '== LOOSE gate, caps ON — slope deciles ==' z;
WITH q AS (SELECT *, NTILE(10) OVER (ORDER BY m) decile FROM tripsL('ret'))
SELECT decile, ROUND(MIN(m),3) lo, ROUND(MAX(m),3) hi, COUNT(*) n, ROUND(AVG(net_pnl),0) mean,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY decile ORDER BY decile;
SELECT '== LOOSE gate, caps ON — max ATR% deciles ==' z;
WITH q AS (SELECT *, NTILE(10) OVER (ORDER BY m) decile FROM tripsL('atr'))
SELECT decile, ROUND(MIN(m),3) lo, ROUND(MAX(m),3) hi, COUNT(*) n, ROUND(AVG(net_pnl),0) mean,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY decile ORDER BY decile;
