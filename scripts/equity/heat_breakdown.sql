ATTACH 'data/trading.db' AS db (READ_ONLY);
CREATE OR REPLACE TEMP TABLE heat AS
WITH r AS (SELECT ticker, date, adj_close/LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date)-1.0 ret,
  AVG(adj_close*adj_volume) OVER (PARTITION BY ticker ORDER BY date RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) adv30
  FROM db.split_adjusted_prices WHERE adj_close>0),
q AS (SELECT date, ret FROM r WHERE adv30>=1000000 AND ret IS NOT NULL AND ret<=10.0),
ranked AS (SELECT date, ret, PERCENT_RANK() OVER (PARTITION BY date ORDER BY ret) pr FROM q)
SELECT date, AVG(ret) heat FROM ranked WHERE pr>=0.99 GROUP BY date;
CREATE OR REPLACE TEMP TABLE hma AS SELECT date, AVG(heat) OVER (ORDER BY date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) h10 FROM heat;
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_default_B.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.net_pnl, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, hm.h10
FROM raw JOIN br ON br.date=raw.entry_date JOIN hma hm ON hm.date=raw.entry_date
WHERE br.b>0.5 AND raw.entry_date>=DATE '2005-01-01';
.mode box
WITH qq AS (SELECT *, NTILE(5) OVER (ORDER BY h10) quint FROM t WHERE h10 IS NOT NULL)
SELECT quint, COUNT(*) n, ROUND(100*MIN(h10),1) lo, ROUND(100*MAX(h10),1) hi, ROUND(100*MEDIAN(ret),2) med, ROUND(100*AVG(ret),2) mean FROM qq GROUP BY quint ORDER BY quint;
SELECT '-- froth cut --' z;
WITH qq AS (SELECT *, NTILE(5) OVER (ORDER BY h10) quint FROM t WHERE h10 IS NOT NULL)
SELECT CASE WHEN quint<=4 THEN 'keep Q1-4 (<~25%)' ELSE 'Q5 cut (>=~25%)' END g, COUNT(*) n,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(net_pnl),0) tot,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM qq GROUP BY 1 ORDER BY 1;
