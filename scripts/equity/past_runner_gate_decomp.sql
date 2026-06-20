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

-- trips with the REAL entry caps (ATR%<0.11, tight<4.5) always applied; rvol/move varied
CREATE OR REPLACE TEMP MACRO trips(minmove, rlo, rhi) AS TABLE
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_grid_loose.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1
       FROM read_parquet('/home/mrakgr/Trading-Edge/data/equity/momentum_v0/breadth.parquet'))
SELECT raw.net_pnl, raw.entry_date, f.max_atr6m, f.max_ret6m
FROM raw JOIN br ON br.date = raw.entry_date
JOIN feat f ON f.ticker = raw.symbol AND f.date = raw.signal_date
WHERE br.b_lag1 > 0.5 AND raw.entry_date >= DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry < 0.11 AND raw.tightness_14_at_entry < 4.5      -- REAL caps ON
  AND raw.pct_up_at_entry >= minmove AND raw.rvol_at_entry >= rlo AND raw.rvol_at_entry <= rhi;

CREATE OR REPLACE TEMP MACRO dec5(minmove, rlo, rhi, measure) AS TABLE
WITH t AS (SELECT net_pnl, entry_date, (CASE WHEN measure='atr' THEN max_atr6m ELSE max_ret6m END) m FROM trips(minmove,rlo,rhi)),
q AS (SELECT *, NTILE(5) OVER (ORDER BY m) AS qtile FROM t)
SELECT qtile, ROUND(MIN(m),3) lo, ROUND(MAX(m),3) hi, COUNT(*) n, ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY qtile ORDER BY qtile;

.mode box
SELECT '== whole-system PF by gate (ATR%<0.11, tight<4.5 ON) ==' z;
SELECT 'loose: move>=5, rvol[3,20]' g, COUNT(*) n, ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf FROM trips(0.05,3,20)
UNION ALL SELECT 'move-only: move>=10, rvol[3,20]', COUNT(*), ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) FROM trips(0.10,3,20)
UNION ALL SELECT 'rvol-only: move>=5, rvol[6,20]', COUNT(*), ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) FROM trips(0.05,6,20)
UNION ALL SELECT 'PROD: move>=10, rvol[6,20]', COUNT(*), ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) FROM trips(0.10,6,20);
SELECT '== slope Q5 (top) across gates, caps ON ==' z;
SELECT 'loose' g, qtile, n, mean_pnl, pf, pf_post FROM dec5(0.05,3,20,'ret') WHERE qtile=5
UNION ALL SELECT 'move-only', qtile, n, mean_pnl, pf, pf_post FROM dec5(0.10,3,20,'ret') WHERE qtile=5
UNION ALL SELECT 'rvol-only', qtile, n, mean_pnl, pf, pf_post FROM dec5(0.05,6,20,'ret') WHERE qtile=5
UNION ALL SELECT 'PROD', qtile, n, mean_pnl, pf, pf_post FROM dec5(0.10,6,20,'ret') WHERE qtile=5;

SELECT '== full slope quintiles, PROD gate, caps ON ==' z;
FROM dec5(0.10,6,20,'ret');
SELECT '== full slope quintiles, loose gate, caps ON ==' z;
FROM dec5(0.05,3,20,'ret');
