CREATE OR REPLACE TEMP TABLE feat AS
WITH base AS (
  SELECT ticker, date, adj_high h, adj_low l, adj_close c,
         LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) pc,
         LAG(adj_close,14) OVER (PARTITION BY ticker ORDER BY date) c14
  FROM split_adjusted_prices WHERE adj_close > 0 AND adj_low > 0
),
win AS (
  SELECT *,
    AVG( GREATEST(h, COALESCE(pc,h)) - LEAST(l, COALESCE(pc,l)) ) OVER w14 / NULLIF(c,0) AS atr14,
    CASE WHEN c14 > 0 THEN c/c14 - 1.0 END AS ret14
  FROM base WINDOW w14 AS (PARTITION BY ticker ORDER BY date ROWS BETWEEN 13 PRECEDING AND CURRENT ROW)
)
SELECT ticker, date, MAX(atr14) OVER w126 AS max_atr6m, MAX(ret14) OVER w126 AS max_ret6m
FROM win WINDOW w126 AS (PARTITION BY ticker ORDER BY date ROWS BETWEEN 126 PRECEDING AND 1 PRECEDING);

CREATE OR REPLACE TEMP MACRO trips(csv) AS TABLE
WITH raw AS (SELECT * FROM read_csv_auto(csv) WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1
       FROM read_parquet('/home/mrakgr/Trading-Edge/data/equity/momentum_v0/breadth.parquet'))
SELECT raw.net_pnl, raw.entry_date, f.max_atr6m, f.max_ret6m
FROM raw JOIN br ON br.date = raw.entry_date
JOIN feat f ON f.ticker = raw.symbol AND f.date = raw.signal_date
WHERE br.b_lag1 > 0.5 AND raw.entry_date >= DATE '2005-01-01';

CREATE OR REPLACE TEMP MACRO deciles(csv, measure) AS TABLE
WITH t AS (SELECT net_pnl, entry_date, (CASE WHEN measure='atr' THEN max_atr6m ELSE max_ret6m END) m FROM trips(csv)),
q AS (SELECT *, NTILE(10) OVER (ORDER BY m) AS dec FROM t)
SELECT dec, ROUND(MIN(m),3) lo, ROUND(MAX(m),3) hi, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN net_pnl>0 THEN 1 ELSE 0 END),1) winr, ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY dec ORDER BY dec;

.mode box
SELECT '== PRODUCTION — max ATR% 6mo (deciles) ==' z;  FROM deciles('/tmp/v2_default_45.csv','atr');
SELECT '== PRODUCTION — max ret 6mo / slope (deciles) ==' z;  FROM deciles('/tmp/v2_default_45.csv','ret');
SELECT '== LOOSE — max ATR% 6mo (deciles) ==' z;  FROM deciles('/tmp/v2_grid_loose.csv','atr');
SELECT '== LOOSE — max ret 6mo / slope (deciles) ==' z;  FROM deciles('/tmp/v2_grid_loose.csv','ret');
