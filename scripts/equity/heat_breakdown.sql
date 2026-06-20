ATTACH 'data/trading.db' AS db (READ_ONLY);
CREATE OR REPLACE TEMP TABLE heat AS
WITH r AS (
  SELECT ticker, date,
    adj_close/LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) - 1.0 AS ret,
    adj_close * adj_volume AS dollar_vol
  FROM db.split_adjusted_prices WHERE adj_close > 0
),
q AS (SELECT date, ret FROM r WHERE dollar_vol >= 100000 AND ret IS NOT NULL AND ret <= 10.0),
ranked AS (SELECT date, ret, PERCENT_RANK() OVER (PARTITION BY date ORDER BY ret) pr FROM q)
SELECT date, AVG(ret) AS heat FROM ranked WHERE pr >= 0.99 GROUP BY date;

-- trailing means of heat (5/10/15/20d), LAGGED 1 day (as-of prior close, no lookahead)
CREATE OR REPLACE TEMP TABLE heat_ma AS
SELECT date,
  AVG(heat) OVER (ORDER BY date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING)  AS h5,
  AVG(heat) OVER (ORDER BY date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) AS h10,
  AVG(heat) OVER (ORDER BY date ROWS BETWEEN 15 PRECEDING AND 1 PRECEDING) AS h15,
  AVG(heat) OVER (ORDER BY date ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS h20
FROM heat;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_default_B.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.net_pnl, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, hm.h5, hm.h10, hm.h15, hm.h20
FROM raw JOIN br ON br.date=raw.entry_date
JOIN heat_ma hm ON hm.date = raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';

CREATE OR REPLACE TEMP MACRO qbd(col) AS TABLE
WITH q AS (SELECT net_pnl, entry_date, ret, NTILE(5) OVER (ORDER BY col) quint FROM t WHERE col IS NOT NULL)
SELECT quint, COUNT(*) n, ROUND(100*MEDIAN(ret),2) med_ret,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY quint ORDER BY quint;

.mode box
SELECT '=== heat 5d quintiles ===' z;  FROM qbd(h5);
SELECT '=== heat 10d quintiles ===' z; FROM qbd(h10);
SELECT '=== heat 15d quintiles ===' z; FROM qbd(h15);
SELECT '=== heat 20d quintiles ===' z; FROM qbd(h20);
SELECT '=== effect of EXCLUDING top heat-20d quintile (the froth cut) ===' z;
WITH q AS (SELECT *, NTILE(5) OVER (ORDER BY h20) quint FROM t WHERE h20 IS NOT NULL)
SELECT CASE WHEN quint<=4 THEN 'keep Q1-4 (heat NOT hot)' ELSE 'Q5 only (hot, excluded)' END grp,
  COUNT(*) n, ROUND(100*MEDIAN(ret),2) med_ret,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(net_pnl),0) tot,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY 1 ORDER BY 1;
SELECT '--- baseline (all heat) for reference ---' z;
SELECT COUNT(*) n, ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf, ROUND(SUM(net_pnl),0) tot FROM t WHERE h20 IS NOT NULL;
