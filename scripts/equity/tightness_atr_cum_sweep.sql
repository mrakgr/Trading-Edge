-- Cumulative CEILING sweeps for tightness (2.5..7) and ATR% (0.04..0.11), on a
-- WIDE dump with BOTH gates opened (everything else production).
-- Regenerate the input first (~17s, 9,090 trips):
--   dotnet run -c Release --project TradingEdge.HighFlyer -- \
--     --out /tmp/v2_wide_tight_atr.csv --max-tightness 1000 --max-atr-pct 1000
-- Then: duckdb -readonly data/trading.db < scripts/equity/tightness_atr_cum_sweep.sql
--
-- *** PF on per-trade RETURN clipped at +50% (LEAST(ret,0.50)); loss side raw. ***
--   Project-standard convention (see docs "Winner-clip convention"). Decide on
--   the CUMULATIVE PF; bands are diagnostic only.
-- Population: move[0.10,0.30) / rvol>=5 / 52w>=0.95 / price>=5 / adv>=100k / 5d-stop,
--   breadth lag1>0.5, era>=2005, closed trips only.
-- Tightness sweep holds ATR% < 0.11; ATR% sweep holds tightness < 4.5 (each at the
-- OTHER axis's production ceiling, so comparable to the shipped gate).
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_wide_tight_atr.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price - 1.0) AS ret
FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';

-- ============ TIGHTNESS ceiling 2.5..7 (ATR% held < 0.11) ============
CREATE OR REPLACE TEMP MACRO tceil(c) AS TABLE
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE atr_pct_14_at_entry<0.11 AND tightness_14_at_entry < c;

.mode box
SELECT '=== TIGHTNESS ceiling (tight < c, ATR% < 0.11), clip +50% ===' z;
SELECT 'tight<2.5' g,* FROM tceil(2.5);
SELECT 'tight<3.0' g,* FROM tceil(3.0);
SELECT 'tight<3.5' g,* FROM tceil(3.5);
SELECT 'tight<4.0' g,* FROM tceil(4.0);
SELECT 'tight<4.5' g,* FROM tceil(4.5);
SELECT 'tight<5.0' g,* FROM tceil(5.0);
SELECT 'tight<5.5' g,* FROM tceil(5.5);
SELECT 'tight<6.0' g,* FROM tceil(6.0);
SELECT 'tight<7.0' g,* FROM tceil(7.0);

SELECT '=== TIGHTNESS non-cumulative bands (ATR% < 0.11), clip +50% ===' z;
SELECT CASE WHEN tightness_14_at_entry<2.5 THEN '1: <2.5'
            WHEN tightness_14_at_entry<3.0 THEN '2: 2.5-3.0'
            WHEN tightness_14_at_entry<3.5 THEN '3: 3.0-3.5'
            WHEN tightness_14_at_entry<4.0 THEN '4: 3.5-4.0'
            WHEN tightness_14_at_entry<4.5 THEN '5: 4.0-4.5'
            WHEN tightness_14_at_entry<5.5 THEN '6: 4.5-5.5'
            WHEN tightness_14_at_entry<7.0 THEN '7: 5.5-7.0'
            ELSE '8: 7.0+' END band,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE atr_pct_14_at_entry<0.11 GROUP BY 1 ORDER BY 1;

-- ============ ATR% ceiling 0.04..0.11 (tightness held < 4.5) ============
CREATE OR REPLACE TEMP MACRO aceil(c) AS TABLE
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE tightness_14_at_entry<4.5 AND atr_pct_14_at_entry < c;

SELECT '=== ATR% ceiling (atr% < c, tight < 4.5), clip +50% ===' z;
SELECT 'atr<0.04' g,* FROM aceil(0.04);
SELECT 'atr<0.05' g,* FROM aceil(0.05);
SELECT 'atr<0.06' g,* FROM aceil(0.06);
SELECT 'atr<0.07' g,* FROM aceil(0.07);
SELECT 'atr<0.08' g,* FROM aceil(0.08);
SELECT 'atr<0.09' g,* FROM aceil(0.09);
SELECT 'atr<0.10' g,* FROM aceil(0.10);
SELECT 'atr<0.11' g,* FROM aceil(0.11);

SELECT '=== ATR% non-cumulative bands (tight < 4.5), clip +50% ===' z;
SELECT CASE WHEN atr_pct_14_at_entry<0.04 THEN '1: <0.04'
            WHEN atr_pct_14_at_entry<0.05 THEN '2: 0.04-0.05'
            WHEN atr_pct_14_at_entry<0.06 THEN '3: 0.05-0.06'
            WHEN atr_pct_14_at_entry<0.07 THEN '4: 0.06-0.07'
            WHEN atr_pct_14_at_entry<0.08 THEN '5: 0.07-0.08'
            WHEN atr_pct_14_at_entry<0.09 THEN '6: 0.08-0.09'
            WHEN atr_pct_14_at_entry<0.10 THEN '7: 0.09-0.10'
            WHEN atr_pct_14_at_entry<0.11 THEN '8: 0.10-0.11'
            ELSE '9: 0.11+' END band,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE tightness_14_at_entry<4.5 GROUP BY 1 ORDER BY 1;
