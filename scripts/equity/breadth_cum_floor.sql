CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_default_B.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b20 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.net_pnl, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, br.b20
FROM raw JOIN br ON br.date=raw.entry_date
WHERE raw.entry_date>=DATE '2005-01-01' AND br.b20 IS NOT NULL;

CREATE OR REPLACE TEMP MACRO floor(f) AS TABLE
SELECT COUNT(*) n, ROUND(100*MEDIAN(ret),2) med, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) winr,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(net_pnl),0) tot,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t WHERE b20 >= f;
.mode box
SELECT 'b20>=0.0 (no gate)' g,* FROM floor(0.0)
 UNION ALL SELECT 'b20>=0.4',* FROM floor(0.4)
 UNION ALL SELECT 'b20>=0.5 (CURRENT)',* FROM floor(0.5)
 UNION ALL SELECT 'b20>=0.6',* FROM floor(0.6)
 UNION ALL SELECT 'b20>=0.65',* FROM floor(0.65)
 UNION ALL SELECT 'b20>=0.7',* FROM floor(0.7)
 UNION ALL SELECT 'b20>=0.75',* FROM floor(0.75)
 UNION ALL SELECT 'b20>=0.8',* FROM floor(0.8);
