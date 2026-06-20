ATTACH 'data/trading.db' AS db (READ_ONLY);
-- COLD = mean return of the BOTTOM 1% losers each day (mirror of heat)
CREATE OR REPLACE TEMP TABLE cold AS
WITH r AS (SELECT ticker, date, adj_close/LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date)-1.0 ret,
  AVG(adj_close*adj_volume) OVER (PARTITION BY ticker ORDER BY date RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) adv30
  FROM db.split_adjusted_prices WHERE adj_close>0),
q AS (SELECT date, ret FROM r WHERE adv30>=1000000 AND ret IS NOT NULL AND ret>=-1.0 AND ret<=10.0),
ranked AS (SELECT date, ret, PERCENT_RANK() OVER (PARTITION BY date ORDER BY ret) pr FROM q)
SELECT date, AVG(ret) cold FROM ranked WHERE pr<=0.01 GROUP BY date;
CREATE OR REPLACE TEMP TABLE cma AS SELECT date, AVG(cold) OVER (ORDER BY date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) c10 FROM cold;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_default_B.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.net_pnl, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, c.c10
FROM raw JOIN br ON br.date=raw.entry_date JOIN cma c ON c.date=raw.entry_date
WHERE br.b>0.5 AND raw.entry_date>=DATE '2005-01-01';

.mode box
SELECT '== COLD-10 distribution (bottom-1% mean, more negative = colder) ==' z;
SELECT ROUND(100*MIN(c10),1) min, ROUND(100*quantile_cont(c10,0.2),1) p20, ROUND(100*MEDIAN(c10),1) med, ROUND(100*quantile_cont(c10,0.8),1) p80, ROUND(100*MAX(c10),1) max FROM t WHERE c10 IS NOT NULL;
SELECT '== c10 QUINTILES (Q1 = coldest/most negative, Q5 = least cold) ==' z;
WITH q AS (SELECT *, NTILE(5) OVER (ORDER BY c10) quint FROM t WHERE c10 IS NOT NULL)
SELECT quint, COUNT(*) n, ROUND(100*MIN(c10),1) lo, ROUND(100*MAX(c10),1) hi,
  ROUND(100*MEDIAN(ret),2) med_ret, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) winr,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY quint ORDER BY quint;
-- add heat h10 to the same table to test independence
ATTACH IF NOT EXISTS 'data/trading.db' AS db (READ_ONLY);
CREATE OR REPLACE TEMP TABLE heat AS
WITH r AS (SELECT ticker, date, adj_close/LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date)-1.0 ret,
  AVG(adj_close*adj_volume) OVER (PARTITION BY ticker ORDER BY date RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) adv30
  FROM db.split_adjusted_prices WHERE adj_close>0),
q AS (SELECT date, ret FROM r WHERE adv30>=1000000 AND ret IS NOT NULL AND ret<=10.0),
ranked AS (SELECT date, ret, PERCENT_RANK() OVER (PARTITION BY date ORDER BY ret) pr FROM q)
SELECT date, AVG(ret) heat FROM ranked WHERE pr>=0.99 GROUP BY date;
CREATE OR REPLACE TEMP TABLE hma AS SELECT date, AVG(heat) OVER (ORDER BY date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) h10 FROM heat;

CREATE OR REPLACE TEMP TABLE t2 AS
SELECT t.*, hm.h10 FROM t JOIN hma hm ON hm.date=t.entry_date;
.mode box
SELECT '== corr(c10,h10) and does cold-Q1 survive WITHIN the heat-kept book? ==' z;
SELECT ROUND(corr(c10,h10),3) corr_cold_heat FROM t2;
SELECT '-- within heat-kept (h10<0.25): is coldest quintile still bad? --' z;
WITH k AS (SELECT * FROM t2 WHERE h10<0.25), q AS (SELECT *, NTILE(5) OVER (ORDER BY c10) cq FROM k)
SELECT CASE WHEN cq=1 THEN '1: coldest' ELSE '2-5: rest' END g, COUNT(*) n, ROUND(100*MEDIAN(ret),2) med,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY 1 ORDER BY 1;
