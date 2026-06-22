ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik=fs.cik WHERE fs.value>0;
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v4_rvol3.csv')),
wf AS (SELECT raw.symbol, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, fl.float_usd, fl.period_end flt_pe
  FROM raw ASOF LEFT JOIN flt fl ON fl.ticker=raw.symbol AND fl.known_date<=raw.entry_date)
SELECT w.symbol, w.entry_date, w.ret,
  CASE WHEN w.float_usd IS NOT NULL AND pe.adj_close>0 AND en.adj_close>0
       THEN w.float_usd*en.adj_close/pe.adj_close END float_usd_at_entry
FROM wf w
ASOF LEFT JOIN split_adjusted_prices pe ON pe.ticker=w.symbol AND pe.date<=w.flt_pe
LEFT JOIN split_adjusted_prices en ON en.ticker=w.symbol AND en.date=w.entry_date
WHERE w.entry_date>=DATE '2005-01-01';   -- breadth + heat OFF (per experiment)

SELECT '=== coverage (rvol>=3, NO breadth/heat) ===' z;
SELECT COUNT(*) trips, COUNT(*) FILTER(WHERE float_usd_at_entry IS NOT NULL) with_float,
  ROUND(100.0*COUNT(*) FILTER(WHERE float_usd_at_entry IS NOT NULL)/COUNT(*),1) pct_cov FROM t;

SELECT '=== A) per-bucket PF by float$ at entry ===' z;
SELECT CASE WHEN float_usd_at_entry IS NULL THEN '0:NO DATA'
   WHEN float_usd_at_entry<50e6 THEN '1:<50M' WHEN float_usd_at_entry<150e6 THEN '2:50-150M'
   WHEN float_usd_at_entry<300e6 THEN '3:150-300M' WHEN float_usd_at_entry<750e6 THEN '4:300-750M'
   WHEN float_usd_at_entry<2e9 THEN '5:750M-2B' ELSE '6:>2B' END float_bucket,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post2015
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== B) CUMULATIVE keep float < N ===' z;
CREATE OR REPLACE TEMP MACRO kb(n) AS TABLE
 SELECT COUNT(*) trips, ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t WHERE float_usd_at_entry IS NOT NULL),1) pct_cov,
   ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
   ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post2015,
   ROUND(SUM(CASE WHEN entry_date>=DATE '2013-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2013-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) y2013p
   FROM t WHERE float_usd_at_entry IS NOT NULL AND float_usd_at_entry<n;
SELECT '<150M' g,* FROM kb(150e6); SELECT '<300M' g,* FROM kb(300e6); SELECT '<750M' g,* FROM kb(750e6);

SELECT '=== C) baselines ===' z;
SELECT 'all covered' g, COUNT(*) n, ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip FROM t WHERE float_usd_at_entry IS NOT NULL
UNION ALL SELECT 'no-data', COUNT(*), ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) FROM t WHERE float_usd_at_entry IS NULL
UNION ALL SELECT 'ENTIRE pop', COUNT(*), ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) FROM t;
