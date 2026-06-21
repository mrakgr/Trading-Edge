-- Sweep an INTRADAY-RETURN floor: avoid the trade unless close/open - 1 >= N.
-- Targets the "gap up then fade" cohort (the red-close band, close/open < 0 — the
-- candle-shape silhouette of the 30%+ over-extension). Cleaner than a "middle body"
-- gate: one threshold on a single intuitive quantity.
-- Input: /tmp/v3_prod_px1.csv (production defaults, price>=1).
-- Run: duckdb -readonly data/trading.db < scripts/equity/intraday_ret_floor_sweep.sql
-- PF on per-trade RETURN clipped at +50% (project standard). Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_px1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.entry_date, (raw.exit_price/raw.entry_price - 1.0) AS ret,
  s.adj_close / NULLIF(s.adj_open,0) - 1.0 AS intraday_ret    -- open->close (the candidate filter quantity)
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN split_adjusted_prices s ON s.ticker=raw.symbol AND s.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND s.adj_open > 0;

CREATE OR REPLACE TEMP MACRO floor(n) AS TABLE
SELECT COUNT(*) n_trips,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE intraday_ret >= n;

.mode box
SELECT '=== INTRADAY-RETURN floor: keep close/open-1 >= N (clip +50%) ===' z;
SELECT 'N=-inf (all)' g,* FROM floor(-1e9);
SELECT 'N=-0.15' g,* FROM floor(-0.15);
SELECT 'N=-0.10' g,* FROM floor(-0.10);
SELECT 'N=-0.05' g,* FROM floor(-0.05);
SELECT 'N= 0.00 (no red)' g,* FROM floor(0.0);
SELECT 'N= 0.02' g,* FROM floor(0.02);
SELECT 'N= 0.05' g,* FROM floor(0.05);
SELECT 'N= 0.10' g,* FROM floor(0.10);
SELECT 'N= 0.15' g,* FROM floor(0.15);

-- where does the dropped population sit? (everything BELOW each floor)
SELECT '=== the EXCLUDED tail (intraday_ret < N): what we would be cutting ===' z;
CREATE OR REPLACE TEMP MACRO cut(n) AS TABLE
SELECT COUNT(*) n_cut,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip_cut,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post_cut
FROM t WHERE intraday_ret < n;
SELECT 'cut < 0.00' g,* FROM cut(0.0);
SELECT 'cut < 0.02' g,* FROM cut(0.02);
SELECT 'cut < 0.05' g,* FROM cut(0.05);
