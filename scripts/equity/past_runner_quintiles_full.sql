.read /tmp/feat_only.sql
CREATE OR REPLACE TEMP MACRO trips(minmove, rlo, rhi) AS TABLE
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_grid_loose.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM read_parquet('/home/mrakgr/Trading-Edge/data/equity/momentum_v0/breadth.parquet'))
SELECT raw.net_pnl, raw.entry_date, f.max_atr6m, f.max_ret6m
FROM raw JOIN br ON br.date=raw.entry_date JOIN feat f ON f.ticker=raw.symbol AND f.date=raw.signal_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.pct_up_at_entry>=minmove AND raw.rvol_at_entry>=rlo AND raw.rvol_at_entry<=rhi;

CREATE OR REPLACE TEMP MACRO q5(minmove, rlo, rhi, measure) AS TABLE
WITH t AS (SELECT net_pnl, entry_date, (CASE WHEN measure='atr' THEN max_atr6m ELSE max_ret6m END) m FROM trips(minmove,rlo,rhi)),
q AS (SELECT *, NTILE(5) OVER (ORDER BY m) AS qtile FROM t)
SELECT qtile, ROUND(MIN(m),3) lo, ROUND(MAX(m),3) hi, COUNT(*) n, ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY qtile ORDER BY qtile;

.mode box
SELECT '###### LOOSE (move>=5, rvol[3,20]) — max ATR% 6mo ######' z;  FROM q5(0.05,3,20,'atr');
SELECT '###### LOOSE (move>=5, rvol[3,20]) — max SLOPE 6mo ######' z;  FROM q5(0.05,3,20,'ret');
SELECT '###### MOVE-ONLY (move>=10, rvol[3,20]) — max ATR% 6mo ######' z;  FROM q5(0.10,3,20,'atr');
SELECT '###### MOVE-ONLY (move>=10, rvol[3,20]) — max SLOPE 6mo ######' z;  FROM q5(0.10,3,20,'ret');
SELECT '###### RVOL-ONLY (move>=5, rvol[6,20]) — max ATR% 6mo ######' z;  FROM q5(0.05,6,20,'atr');
SELECT '###### RVOL-ONLY (move>=5, rvol[6,20]) — max SLOPE 6mo ######' z;  FROM q5(0.05,6,20,'ret');
