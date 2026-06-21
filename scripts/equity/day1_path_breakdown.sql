-- Path dependency: how does the trade PF depend on DAY 1 (first day after entry)?
-- Entry = close of day 0; hold days 1-5; exit at the open of day 6 (MaxHoldBars=5).
-- "Day 1 return" = entry_close -> next-trading-day close (the first held bar).
-- Joins the day-1 close from split_adjusted_prices (next bar after entry_date per ticker).
-- PF on per-trade RETURN clipped at +50% (project standard).
-- Input: /tmp/v3_prod_intraday.csv (current production defaults incl. the -0.07 gate).
-- Run: duckdb -readonly data/trading.db < scripts/equity/day1_path_breakdown.sql
-- Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_intraday.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
-- per-ticker ordered bars to find the bar AFTER entry_date
nx AS (
  SELECT ticker, date AS d0, adj_close AS c0,
    LEAD(adj_close) OVER (PARTITION BY ticker ORDER BY date) AS c1   -- day-1 close
  FROM split_adjusted_prices
)
SELECT raw.symbol, raw.entry_date, raw.entry_price,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,                     -- full trade return
  nx.c1 / NULLIF(raw.entry_price,0) - 1.0 AS day1_ret                -- entry close -> day-1 close
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN nx ON nx.ticker=raw.symbol AND nx.d0=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND nx.c1 IS NOT NULL;

.mode box
-- 1) headline split: day 1 UP vs DOWN vs FLAT
SELECT '=== DAY 1 up vs down — trade PF (clip +50%) ===' z;
SELECT CASE WHEN day1_ret>0 THEN 'A: day1 UP' WHEN day1_ret<0 THEN 'B: day1 DOWN' ELSE 'C: day1 FLAT' END grp,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- 2) graded bands of day-1 return
SELECT '=== DAY 1 return bands ===' z;
SELECT CASE WHEN day1_ret< -0.10 THEN '1: < -10%'
            WHEN day1_ret< -0.05 THEN '2: -10..-5%'
            WHEN day1_ret< -0.02 THEN '3: -5..-2%'
            WHEN day1_ret<  0.00 THEN '4: -2..0%'
            WHEN day1_ret<  0.02 THEN '5: 0..2%'
            WHEN day1_ret<  0.05 THEN '6: 2..5%'
            WHEN day1_ret<  0.10 THEN '7: 5..10%'
            ELSE                     '8: 10%+' END band,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- 3) cumulative FLOOR: keep day1_ret >= N (could we cut losers on day 1?)
--    NOTE: this is a HYPOTHETICAL hold-through filter (you can't know day-1 close at
--    entry); it measures whether day-1 weakness PREDICTS a bad rest-of-trade.
SELECT '=== cumulative day1 floor (keep day1_ret >= N) — predictive check ===' z;
CREATE OR REPLACE TEMP MACRO fl(n) AS TABLE
SELECT COUNT(*) n_trips,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE day1_ret >= n;
SELECT 'all'       g,* FROM fl(-1e9);
SELECT 'day1>=-.05' g,* FROM fl(-0.05);
SELECT 'day1>=0'    g,* FROM fl(0.0);
SELECT 'day1>=.02'  g,* FROM fl(0.02);

SELECT '=== baseline (all) ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t;
