-- max-log-ATR FLOOR sweep to pick the production threshold. The BOTTOM of the
-- past-runner ladder is well-distributed (dead-quiet base = uniformly bad), so a
-- floor lifts the whole distribution, not just the tail. Confirm with the ex-top-5
-- base PF (rises too => broad gain, not a concentration artifact).
--
-- "max log ATR" = 126-bar max of the 14-bar log-ATR (log TR = ln(max(hi,pc)/min(lo,pc))),
-- lagged 1 bar. Population: FULL production (move[10,30], rvol>=5, breadth+heat).
-- RAW PF (lottery tail kept). Input: /tmp/v3_510_rvol1.csv. >=2005, closed.
-- Chosen threshold: 0.04 (~p20) -> raw PF 2.205->2.346, post-2015 2.066->2.216,
-- keeps 82% of trips; wired into the engine as EntryConfig.MinMaxAtrLog.
-- Run: duckdb -readonly data/trading.db < scripts/equity/maxlogatr_floor_sweep.sql

CREATE OR REPLACE TEMP TABLE meas AS
WITH base AS (SELECT ticker,date,ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY date) rn, adj_high,adj_low,adj_close,
    LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) pc
  FROM split_adjusted_prices WHERE ticker IN (SELECT DISTINCT symbol FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0)),
tr AS (SELECT ticker,date,rn, CASE WHEN adj_high>0 AND adj_low>0 AND pc>0 THEN ln(GREATEST(adj_high,pc)/LEAST(adj_low,pc)) END logtr FROM base),
w AS (SELECT ticker,date,rn, AVG(logtr) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) logatr14 FROM tr),
m AS (SELECT ticker,date, MAX(logatr14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_atr6,
    COUNT(*) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) nbars FROM w)
SELECT ticker,date, LAG(max_atr6) OVER (PARTITION BY ticker ORDER BY date) max_atr6, LAG(nbars) OVER (PARTITION BY ticker ORDER BY date) nbars FROM m;
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT (raw.exit_price/raw.entry_price-1.0) ret, raw.entry_date, m.max_atr6
FROM raw JOIN br ON br.date=raw.entry_date LEFT JOIN hn ON hn.date=raw.entry_date JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND (hn.h10 IS NULL OR hn.h10<0.25)
  AND raw.pct_up_at_entry>=0.10 AND raw.pct_up_at_entry<0.30 AND raw.rvol_at_entry>=5 AND m.nbars>=120;

.mode box
SELECT '=== max_atr6 distribution (for threshold) ===' z;
SELECT ROUND(quantile_cont(max_atr6,0.05),4) p5, ROUND(quantile_cont(max_atr6,0.10),4) p10,
  ROUND(quantile_cont(max_atr6,0.20),4) p20, ROUND(MEDIAN(max_atr6),4) p50 FROM t;

SELECT '=== cumulative FLOOR: keep max_atr6 >= N (raw PF + ex-top5 base + post) ===' z;
CREATE OR REPLACE TEMP MACRO fl(n) AS TABLE
WITH c AS (SELECT *, ROW_NUMBER() OVER (ORDER BY ret DESC) rk FROM t WHERE max_atr6>=n)
SELECT (SELECT COUNT(*) FROM c) trips,
  ROUND((SELECT 100.0*COUNT(*) FROM c)/(SELECT COUNT(*) FROM t),1) pct_kept,
  ROUND((SELECT SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0) FROM c),3) pf_raw,
  ROUND((SELECT SUM(CASE WHEN ret>0 AND rk>5 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>5 THEN ret ELSE 0 END),0) FROM c),3) pf_ex5,
  ROUND((SELECT SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0) FROM c),3) post15;
SELECT 'all'     g,* FROM fl(0.0);
SELECT '>=0.035' g,* FROM fl(0.035);
SELECT '>=0.04 (CHOSEN)' g,* FROM fl(0.04);
SELECT '>=0.05' g,* FROM fl(0.05);
SELECT '>=0.06' g,* FROM fl(0.06);
