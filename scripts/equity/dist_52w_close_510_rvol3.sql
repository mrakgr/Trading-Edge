-- How does distance from the 52w MAX CLOSE (pct_52w_at_entry) affect PF, on the
-- [5,10]% move / rvol>3 system, with FULL production settings intact (ATR%<0.10,
-- tight<4.5, price>=1, 52w>=0.95, -0.07 intraday gate, 5d stop) PLUS the
-- production regime gates: breadth lag1>0.5 AND heat (CS/ADRC) h10<0.25.
--
-- Reference: pct_52w_at_entry = close / max(252d closes) - 1. NEGATIVE = the close
-- is still below the prior 52w max CLOSE (recovering); ~0 = fresh new close-high;
-- POSITIVE = extended above the prior max close.  (This is the close-vs-close-high
-- reference, NOT the intraday-high one used by the reclaim/gap split.)
--
-- Input: /tmp/v3_510_rvol3.csv  (regen: dotnet run -c Release --project
--   TradingEdge.MomentumV2 -- --up-threshold 0.05 --rvol-min 3 --out /tmp/v3_510_rvol3.csv)
-- PF on per-trade RETURN clipped at +50% (project standard). >=2005, closed.
-- Run: duckdb -readonly data/trading.db < scripts/equity/dist_52w_close_510_rvol3.sql

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol3.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price,
  raw.pct_up_at_entry, raw.rvol_at_entry, raw.pct_52w_at_entry AS d52,
  (raw.exit_price/raw.entry_price - 1.0) AS ret
FROM raw
JOIN br ON br.date=raw.entry_date
LEFT JOIN hn ON hn.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND (hn.h10 IS NULL OR hn.h10 < 0.25)               -- production heat gate
  AND raw.pct_up_at_entry>=0.05 AND raw.pct_up_at_entry<0.10   -- the [5,10]% band
  AND raw.rvol_at_entry>=3;                                    -- rvol>3

.mode box
SELECT '=== baseline: [5,10]% rvol>3, full prod incl heat+breadth ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t;

-- 1) BANDS of distance from 52w max close (diagnostic — non-cumulative)
SELECT '=== distance-from-52w-max-close BANDS ===' z;
SELECT CASE WHEN d52< -0.03 THEN '1: < -3% (below max close)'
            WHEN d52< -0.01 THEN '2: -3..-1%'
            WHEN d52<  0.00 THEN '3: -1..0% (just under)'
            WHEN d52<  0.01 THEN '4: 0..1% (fresh close-high)'
            WHEN d52<  0.03 THEN '5: 1..3%'
            WHEN d52<  0.05 THEN '6: 3..5%'
            ELSE                 '7: 5%+ (extended)' END band,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- 2) CUMULATIVE FLOOR: keep d52 >= N (decision view — require at least this close-extension)
SELECT '=== cumulative FLOOR: keep d52 >= N ===' z;
CREATE OR REPLACE TEMP MACRO fl(n) AS TABLE
SELECT COUNT(*) nn,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE d52 >= n;
SELECT 'd52>=-0.05 (all)' g,* FROM fl(-0.05);
SELECT 'd52>=-0.03' g,* FROM fl(-0.03);
SELECT 'd52>=-0.01' g,* FROM fl(-0.01);
SELECT 'd52>=0'    g,* FROM fl(0.0);
SELECT 'd52>=0.01' g,* FROM fl(0.01);
SELECT 'd52>=0.03' g,* FROM fl(0.03);

-- 3) CUMULATIVE CEILING: keep d52 < N (require NOT too extended)
SELECT '=== cumulative CEILING: keep d52 < N ===' z;
CREATE OR REPLACE TEMP MACRO cl(n) AS TABLE
SELECT COUNT(*) nn,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE d52 < n;
SELECT 'd52<0.01' g,* FROM cl(0.01);
SELECT 'd52<0.03' g,* FROM cl(0.03);
SELECT 'd52<0.05' g,* FROM cl(0.05);
SELECT 'd52<0.10 (all)' g,* FROM cl(0.10);
