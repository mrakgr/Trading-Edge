-- TideFlyer — ATR% (log-space) & tightness breakdown (the HighFlyer quality gates, OFF by default here).
-- In HighFlyer these are CEILINGS (cap jumpy/trending names — momentum wants coiled springs). For a deep-
-- washout MR book the relationship may INVERT (chaos = the sharp reversible dislocation). Find out.
-- Population = production book /tmp/tide_true.csv (19,587 / PF 1.924). RAW PF. atr_pct = LOG-space ATR(14).
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_atr_tightness.sql

CREATE OR REPLACE TEMP TABLE t AS
SELECT atr_pct_14_at_entry AS atr, range_pct_14_at_entry AS rng, tightness_14_at_entry AS tight,
       net_pnl AS pnl, exit_price/NULLIF(entry_price,0)-1.0 AS ret
FROM read_csv_auto('/tmp/tide_true.csv');

CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t WHERE cond;

.mode box
SELECT '=== distributions (p10 / med / p90) ===' z;
SELECT 'atr_pct' m, ROUND(quantile_cont(atr,0.10),4) p10, ROUND(MEDIAN(atr),4) med, ROUND(quantile_cont(atr,0.90),4) p90 FROM t WHERE atr IS NOT NULL
UNION ALL SELECT 'tightness', ROUND(quantile_cont(tight,0.10),3), ROUND(MEDIAN(tight),3), ROUND(quantile_cont(tight,0.90),3) FROM t WHERE tight IS NOT NULL;

SELECT '=== A) ATR% (log) bands ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.03,'<0.03'),(0.03,0.05,'0.03-0.05'),(0.05,0.08,'0.05-0.08'),
  (0.08,0.10,'0.08-0.10'),(0.10,0.15,'0.10-0.15'),(0.15,0.25,'0.15-0.25'),(0.25,1e9,'>0.25'))
SELECT b.lbl AS atr_band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.atr>=b.lo AND t.atr<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== B) ATR% CUMULATIVE ceiling (HighFlyer default 0.10) ===' z;
SELECT 'atr<0.05' g,* FROM pf(atr<0.05);
SELECT 'atr<0.08' g,* FROM pf(atr<0.08);
SELECT 'atr<0.10' g,* FROM pf(atr<0.10);
SELECT 'atr<0.15' g,* FROM pf(atr<0.15);
SELECT '=== B2) ATR% CUMULATIVE floor (does chaos WIN here?) ===' z;
SELECT 'atr>=0.08' g,* FROM pf(atr>=0.08);
SELECT 'atr>=0.10' g,* FROM pf(atr>=0.10);
SELECT 'atr>=0.15' g,* FROM pf(atr>=0.15);

SELECT '=== C) TIGHTNESS bands (low=coiled, high=trending) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,2.0,'<2'),(2.0,3.0,'2-3'),(3.0,4.5,'3-4.5'),(4.5,6.0,'4.5-6'),
  (6.0,9.0,'6-9'),(9.0,15.0,'9-15'),(15.0,1e9,'>15'))
SELECT b.lbl AS tight_band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.tight>=b.lo AND t.tight<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== D) TIGHTNESS cumulative (HighFlyer default <4.5) ===' z;
SELECT 'tight<3'   g,* FROM pf(tight<3.0);
SELECT 'tight<4.5' g,* FROM pf(tight<4.5);
SELECT 'tight<6'   g,* FROM pf(tight<6.0);
SELECT 'tight>=4.5' g,* FROM pf(tight>=4.5);
SELECT 'tight>=6'   g,* FROM pf(tight>=6.0);

SELECT '=== E) baseline ===' z;
SELECT 'all' g,* FROM pf(true);
