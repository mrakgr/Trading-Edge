ATTACH 'data/trading.db' AS db (READ_ONLY);
CREATE OR REPLACE TEMP TABLE brlag AS
WITH base AS (SELECT s.ticker,s.date,s.adj_close,
  AVG(s.adj_close) OVER (PARTITION BY s.ticker ORDER BY s.date ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) ma20,
  AVG(s.adj_close*s.adj_volume) OVER (PARTITION BY s.ticker ORDER BY s.date RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) adv30,
  ROW_NUMBER() OVER (PARTITION BY s.ticker ORDER BY s.date) rn, r.type
  FROM db.split_adjusted_prices s LEFT JOIN db.ticker_reference r ON r.ticker=s.ticker WHERE s.adj_close>0),
u AS (SELECT date, AVG(CASE WHEN adj_close>ma20 THEN 1.0 ELSE 0.0 END) b20 FROM base WHERE rn>=20 AND type IN ('CS','ADRC') AND adv30>=1000000 GROUP BY date)
SELECT date, LAG(b20) OVER (ORDER BY date) b20 FROM u;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_default_B.csv') WHERE open=0)
SELECT raw.net_pnl, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, b.b20
FROM raw JOIN brlag b ON b.date=raw.entry_date
WHERE raw.entry_date>=DATE '2005-01-01' AND b.b20 IS NOT NULL;

CREATE OR REPLACE TEMP MACRO f(x) AS TABLE
SELECT COUNT(*) n, ROUND(100*MEDIAN(ret),2) med,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t WHERE b20>=x;
.mode box
SELECT 'b20>=0.5' g,* FROM f(0.5) UNION ALL SELECT 'b20>=0.6',* FROM f(0.6)
 UNION ALL SELECT 'b20>=0.65',* FROM f(0.65) UNION ALL SELECT 'b20>=0.7',* FROM f(0.7) UNION ALL SELECT 'b20>=0.75',* FROM f(0.75);
