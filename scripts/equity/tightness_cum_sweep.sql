-- Cumulative tightness CEILING sweep on the PRODUCTION-defaults trip set.
-- Regenerate the input first (production defaults, ~16s, 5,883 trips):
--   dotnet run -c Release --project TradingEdge.MomentumV2 -- --out /tmp/v2_prod.csv
-- Then: duckdb -readonly data/trading.db < scripts/equity/tightness_cum_sweep.sql
-- Population: /tmp/v2_prod.csv (move[0.10,0.30) rvol>=5 atr%<0.11 tight<4.5 5d-stop),
--   closed trips only (open=0), breadth lag1>0.5, era>=2005.
-- *** PF is computed on per-trade RETURN CLIPPED at +50% (LEAST(ret,0.50)) ***
--   This is now the project-standard PF convention: a single lottery winner
--   (buyout pop, +800% name) must not decide whether a bucket has edge. The
--   loss side is left untouched. Total-P&L is understated by design (conservative).
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_prod.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price - 1.0) AS ret
FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';

-- cumulative ceiling: all trades with tightness < c, PF on clipped return
CREATE OR REPLACE TEMP MACRO ceil(c, cap) AS TABLE
SELECT COUNT(*) n,
  ROUND(AVG(LEAST(ret,cap)),4) mean_ret_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,cap) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' THEN (CASE WHEN ret>0 THEN LEAST(ret,cap) ELSE 0 END) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_clip_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' THEN (CASE WHEN ret>0 THEN LEAST(ret,cap) ELSE 0 END) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_clip_post
FROM t WHERE tightness_14_at_entry < c;

.mode box
SELECT '=== cumulative tightness CEILING (tight < c), return clipped at +50% ===' z;
SELECT 'tight<2.5' g, * FROM ceil(2.5,0.50);
SELECT 'tight<3.0' g, * FROM ceil(3.0,0.50);
SELECT 'tight<3.5' g, * FROM ceil(3.5,0.50);
SELECT 'tight<4.0' g, * FROM ceil(4.0,0.50);
SELECT 'tight<4.5' g, * FROM ceil(4.5,0.50);

-- Non-cumulative bands (diagnostic only — expect noise at edges)
SELECT '=== NON-CUMULATIVE tightness bands (clipped +50%) ===' z;
SELECT CASE WHEN tightness_14_at_entry<2.0 THEN '1: <2.0'
            WHEN tightness_14_at_entry<2.5 THEN '2: 2.0-2.5'
            WHEN tightness_14_at_entry<3.0 THEN '3: 2.5-3.0'
            WHEN tightness_14_at_entry<3.5 THEN '4: 3.0-3.5'
            WHEN tightness_14_at_entry<4.0 THEN '5: 3.5-4.0'
            ELSE '6: 4.0-4.5' END band,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' THEN (CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_clip_post
FROM t GROUP BY 1 ORDER BY 1;
