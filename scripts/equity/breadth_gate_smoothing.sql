ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
-- breadth series with no-lookahead smoothings: each MA ENDS at lag-1 (prior close),
-- i.e. AVG over [t-w, t-1], so an entry on day t only sees breadth through t-1.
CREATE OR REPLACE TEMP TABLE br AS
SELECT date,
  LAG(pct_above_20,1) OVER (ORDER BY date) raw1,
  AVG(pct_above_20) OVER (ORDER BY date ROWS BETWEEN 5 PRECEDING AND 1 PRECEDING) ma5,
  AVG(pct_above_20) OVER (ORDER BY date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) ma10
FROM 'data/equity/momentum_v0/breadth.parquet';
CREATE OR REPLACE TEMP TABLE hn AS SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet';

-- production trips + heat + >=2005 (this is the population; the breadth gate is the VARIABLE).
CREATE OR REPLACE TEMP TABLE t AS
SELECT raw.symbol, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret,
  br.raw1, br.ma5, br.ma10
FROM read_csv_auto('/tmp/v4_breadth_test.csv') raw
JOIN br ON br.date = raw.entry_date
LEFT JOIN hn ON hn.date = raw.entry_date
WHERE raw.entry_date >= DATE '2005-01-01' AND (hn.h10 IS NULL OR hn.h10 < 0.25);

CREATE OR REPLACE TEMP MACRO pf(filter_col, thr) AS TABLE
 SELECT COUNT(*) trips,
   ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win,
   ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
   ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post2015
 FROM t WHERE filter_col > thr;

SELECT '=== CURRENT: raw lag-1 > 0.50 (production baseline) ===' z;
SELECT 'raw1>0.50' g,* FROM pf(raw1, 0.50);
SELECT '=== no gate (all trips, heat only) ===' z;
SELECT 'nogate' g, COUNT(*) trips, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post2015 FROM t;
SELECT '=== MA5 gate sweep ===' z;
SELECT 'ma5>0.45' g,* FROM pf(ma5,0.45); SELECT 'ma5>0.50' g,* FROM pf(ma5,0.50); SELECT 'ma5>0.55' g,* FROM pf(ma5,0.55); SELECT 'ma5>0.60' g,* FROM pf(ma5,0.60);
SELECT '=== MA10 gate sweep ===' z;
SELECT 'ma10>0.45' g,* FROM pf(ma10,0.45); SELECT 'ma10>0.50' g,* FROM pf(ma10,0.50); SELECT 'ma10>0.55' g,* FROM pf(ma10,0.55); SELECT 'ma10>0.60' g,* FROM pf(ma10,0.60);
SELECT '=== RAW lag-1 at higher thresholds (is it threshold or smoothing?) ===' z;
SELECT 'raw1>0.55' g,* FROM pf(raw1,0.55);
SELECT 'raw1>0.60' g,* FROM pf(raw1,0.60);
