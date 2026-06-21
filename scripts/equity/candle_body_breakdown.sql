-- Candle-body shape on the ENTRY day vs trade PF. Hypothesis (from gap-over < reclaim):
-- a FAT GREEN body (opens low, closes high — conviction built through the day) beats a
-- DOJI / top-heavy candle (opens high, closes high — move already done at the open).
-- Joins entry-day adjusted OHLC from split_adjusted_prices (exact match: trip close ==
-- adj_close on (ticker,date)). PF on per-trade RETURN clipped at +50% (project standard).
-- Input: production-defaults trips (price>=1): /tmp/v3_prod_px1.csv
--   (regen: dotnet run -c Release --project TradingEdge.MomentumV2 -- --out /tmp/v3_prod_px1.csv)
-- Run: duckdb -readonly data/trading.db < scripts/equity/candle_body_breakdown.sql
-- Breadth lag1>0.5, >=2005, closed trips only.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_px1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.symbol, raw.entry_date, raw.entry_price,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,
  s.adj_open AS o, s.adj_high AS h, s.adj_low AS l, s.adj_close AS c,
  NULLIF(s.adj_high - s.adj_low, 0)                       AS rng,
  (s.adj_close - s.adj_open) / NULLIF(s.adj_high-s.adj_low,0) AS body_frac,   -- green body / range; <0 = red
  (s.adj_open  - s.adj_low ) / NULLIF(s.adj_high-s.adj_low,0) AS open_pos,    -- 0 = opened at low, 1 = opened at high
  (s.adj_close - s.adj_low ) / NULLIF(s.adj_high-s.adj_low,0) AS close_pos    -- 1 = closed at high
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN split_adjusted_prices s ON s.ticker=raw.symbol AND s.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND s.adj_high > s.adj_low;     -- drop zero-range bars (can't define body)

CREATE OR REPLACE TEMP MACRO pf(filter_col, lo, hi) AS TABLE
SELECT COUNT(*) n,
  ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE filter_col >= lo AND filter_col < hi;

.mode box
-- ============ 1) BODY FRACTION bands: (close-open)/range ============
SELECT '=== BODY FRACTION (close-open)/range — fat green vs doji vs red ===' z;
SELECT CASE WHEN body_frac<0.0  THEN '1: <0     (red close)'
            WHEN body_frac<0.2  THEN '2: 0.0-0.2 (doji-ish)'
            WHEN body_frac<0.4  THEN '3: 0.2-0.4'
            WHEN body_frac<0.6  THEN '4: 0.4-0.6'
            WHEN body_frac<0.8  THEN '5: 0.6-0.8'
            ELSE                     '6: 0.8-1.0 (fat green)' END band,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- cumulative FLOOR on body fraction (decisional)
SELECT '=== cumulative body_frac FLOOR (body_frac >= x) ===' z;
SELECT 'body>=-1' g,* FROM pf(body_frac,-1,2);
SELECT 'body>=0.0' g,* FROM pf(body_frac,0.0,2);
SELECT 'body>=0.2' g,* FROM pf(body_frac,0.2,2);
SELECT 'body>=0.4' g,* FROM pf(body_frac,0.4,2);
SELECT 'body>=0.6' g,* FROM pf(body_frac,0.6,2);
SELECT 'body>=0.8' g,* FROM pf(body_frac,0.8,2);

-- ============ 2) OPEN POSITION: did it open low (reclaim) or high (gap-over)? ============
SELECT '=== OPEN POSITION (open-low)/range — 0=opened at low (reclaim), 1=opened at high ===' z;
SELECT CASE WHEN open_pos<0.2 THEN '1: 0.0-0.2 (opened LOW)'
            WHEN open_pos<0.4 THEN '2: 0.2-0.4'
            WHEN open_pos<0.6 THEN '3: 0.4-0.6'
            WHEN open_pos<0.8 THEN '4: 0.6-0.8'
            ELSE                   '5: 0.8-1.0 (opened HIGH)' END band,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- ============ 3) THE KEY TEST: among trades that CLOSE HIGH, does opening LOW beat opening HIGH? ============
SELECT '=== among CLOSE-HIGH trades (close_pos>=0.8): open-low vs open-high ===' z;
SELECT CASE WHEN open_pos<0.2 THEN 'A: close-high + opened LOW  (fat body / reclaim)'
            WHEN open_pos<0.5 THEN 'B: close-high + opened mid'
            ELSE                   'C: close-high + opened HIGH (doji-at-top / gap-over)' END grp,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE close_pos>=0.8 GROUP BY 1 ORDER BY 1;

-- baseline for reference
SELECT '=== baseline: all trades (no body filter) ===' z;
SELECT COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t;
