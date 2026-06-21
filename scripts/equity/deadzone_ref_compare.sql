-- Dead-zone REFERENCE comparison: should the dead zone be measured from the max 52w
-- CLOSE (pct_52w_at_entry, "d52c", the current [3,10] definition) or the max 52w
-- intraday HIGH (pct_52w_high_at_entry, "d52h", candidate [0,X])? And what is X (7? 10?).
--
-- Population: [5,10]% move, rvol>=2, full production gates (ATR%<0.10, tight<4.5,
-- price>=1, intraday>=-0.07, 52w>=0.95) + breadth + heat. PF clip +50%. >=2005.
-- Input: /tmp/v3_510_rvol1.csv. Run: duckdb -readonly data/trading.db < this.sql

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT (raw.exit_price/raw.entry_price-1.0) ret, raw.entry_date,
  raw.pct_52w_at_entry d52c,        -- vs max 52w CLOSE
  raw.pct_52w_high_at_entry d52h    -- vs max 52w intraday HIGH
FROM raw JOIN br ON br.date=raw.entry_date LEFT JOIN hn ON hn.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND (hn.h10 IS NULL OR hn.h10<0.25)
  AND raw.pct_up_at_entry>=0.05 AND raw.pct_up_at_entry<0.10 AND raw.rvol_at_entry>=2;

.mode box
SELECT '=== how the two refs relate ===' z;
SELECT ROUND(CORR(d52c,d52h),3) corr, ROUND(AVG(d52h-d52c),4) mean_gap, ROUND(MEDIAN(d52h-d52c),4) median_gap, COUNT(*) n FROM t;

-- band tables inlined per ref (a macro can't portably take a column reference)
SELECT '=== A) bands vs max 52w CLOSE (d52c) ===' z;
SELECT CASE WHEN d52c<0 THEN '0:<0' WHEN d52c<0.03 THEN '1:0-3 fresh' WHEN d52c<0.05 THEN '2:3-5' WHEN d52c<0.07 THEN '3:5-7' WHEN d52c<0.10 THEN '4:7-10' ELSE '5:10+' END b, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post
FROM t GROUP BY 1 ORDER BY 1;
SELECT '=== B) bands vs max 52w intraday HIGH (d52h) ===' z;
SELECT CASE WHEN d52h<0 THEN '0:<0' WHEN d52h<0.03 THEN '1:0-3 fresh' WHEN d52h<0.05 THEN '2:3-5' WHEN d52h<0.07 THEN '3:5-7' WHEN d52h<0.10 THEN '4:7-10' ELSE '5:10+' END b, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post
FROM t GROUP BY 1 ORDER BY 1;

-- cumulative CEILING (the decision lens): keep below N, both refs
SELECT '=== CEILING keep d52c < N (close ref) ===' z;
CREATE OR REPLACE TEMP MACRO cc(n) AS TABLE SELECT COUNT(*) trips, ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t),1) pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post FROM t WHERE d52c<n;
SELECT 'd52c<0.03' g,* FROM cc(0.03); SELECT 'd52c<0.05' g,* FROM cc(0.05); SELECT 'd52c<0.07' g,* FROM cc(0.07); SELECT 'd52c<0.10 (all)' g,* FROM cc(0.10);
SELECT '=== CEILING keep d52h < N (intraday-high ref) ===' z;
CREATE OR REPLACE TEMP MACRO ch(n) AS TABLE SELECT COUNT(*) trips, ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t),1) pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post FROM t WHERE d52h<n;
SELECT 'd52h<0.03' g,* FROM ch(0.03); SELECT 'd52h<0.05' g,* FROM ch(0.05); SELECT 'd52h<0.07' g,* FROM ch(0.07); SELECT 'd52h<0.10 (all)' g,* FROM ch(0.10);
