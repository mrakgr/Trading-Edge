-- TideFlyer — 20d rvol breakdown (entry_vol / avgVol[20 bars]), VOLFRAC DISABLED.
-- volfrac (entry_vol / 7d-rolling-vol-MAX) is a SHORT-horizon "is today loud vs the last week" measure;
-- rvol_20d (entry_vol / 20d avg) is "is today loud vs the name's normal". Different denominators.
-- User thesis: MODERATE volume beats a spike because a high-volume breakdown = a fundamental catalyst
-- (real news that keeps falling), not panic/technical selling that reverts. Does rvol_20d show the same
-- inverted-U as volfrac? Population = the production book with volfrac OFF (/tmp/tide_novolfrac.csv,
-- 28,186 / PF 1.711). RAW PF. rvol = rvol_at_entry (already 20d in the CSV).
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_rvol.sql

CREATE OR REPLACE TEMP TABLE t AS
SELECT rvol_at_entry AS rvol, entry_adj_volume/NULLIF(vol_max_7d_at_entry,0) AS volfrac,
       net_pnl AS pnl, exit_price/NULLIF(entry_price,0)-1.0 AS ret
FROM read_csv_auto('/tmp/tide_novolfrac.csv');

CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t WHERE cond;

.mode box
SELECT '=== rvol_20d distribution ===' z;
SELECT ROUND(quantile_cont(rvol,0.10),2) p10, ROUND(MEDIAN(rvol),2) med,
       ROUND(quantile_cont(rvol,0.90),2) p90, ROUND(quantile_cont(rvol,0.99),2) p99 FROM t WHERE rvol IS NOT NULL;

SELECT '=== A) rvol_20d bands (is there an inverted-U like volfrac?) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.5,'<0.5'),(0.5,1.0,'0.5-1'),(1.0,1.5,'1-1.5'),(1.5,2.0,'1.5-2'),
  (2.0,3.0,'2-3'),(3.0,5.0,'3-5'),(5.0,10.0,'5-10'),(10.0,1e9,'>10'))
SELECT b.lbl AS rvol_band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.rvol>=b.lo AND t.rvol<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== B) CUMULATIVE ceiling: keep rvol <= N (is moderate the edge?) ===' z;
SELECT 'rvol<1'   g,* FROM pf(rvol<1.0);
SELECT 'rvol<1.5' g,* FROM pf(rvol<1.5);
SELECT 'rvol<2'   g,* FROM pf(rvol<2.0);
SELECT 'rvol<3'   g,* FROM pf(rvol<3.0);
SELECT 'rvol<5'   g,* FROM pf(rvol<5.0);

SELECT '=== C) CUMULATIVE floor: keep rvol >= N (does a spike hurt?) ===' z;
SELECT 'rvol>=2' g,* FROM pf(rvol>=2.0);
SELECT 'rvol>=3' g,* FROM pf(rvol>=3.0);
SELECT 'rvol>=5' g,* FROM pf(rvol>=5.0);

-- band: cut both the dead-quiet tail and the catalyst-spike tail
SELECT '=== D) BAND rvol in [lo,hi] ===' z;
SELECT 'rvol[0.5,2]'   g,* FROM pf(rvol>=0.5 AND rvol<2.0);
SELECT 'rvol[0.5,3]'   g,* FROM pf(rvol>=0.5 AND rvol<3.0);
SELECT 'rvol[1,3]'     g,* FROM pf(rvol>=1.0 AND rvol<3.0);

-- how correlated is rvol_20d with volfrac? (are they the same lever or complementary?)
SELECT '=== E) rvol vs volfrac: do they overlap? corr + the 4 quadrants ===' z;
SELECT ROUND(corr(rvol, volfrac),3) corr_rvol_volfrac FROM t WHERE rvol IS NOT NULL AND volfrac IS NOT NULL;
SELECT 'rvol<2 & volfrac[0.5,1.5]' g,* FROM pf(rvol<2.0 AND volfrac>=0.5 AND volfrac<=1.5);
SELECT 'rvol<2 & volfrac OUT'      g,* FROM pf(rvol<2.0 AND (volfrac<0.5 OR volfrac>1.5));
SELECT 'rvol>=2 & volfrac[0.5,1.5]' g,* FROM pf(rvol>=2.0 AND volfrac>=0.5 AND volfrac<=1.5);

-- rvol bands WITHIN the production (volfrac[0.5,1.5]) book — is the rvol shape the same once
-- volfrac already conditions the population, or does volfrac absorb it?
SELECT '=== G) rvol_20d bands, volfrac[0.5,1.5] CONDITIONED (production book) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.5,'<0.5'),(0.5,1.0,'0.5-1'),(1.0,1.5,'1-1.5'),(1.5,2.0,'1.5-2'),
  (2.0,3.0,'2-3'),(3.0,5.0,'3-5'),(5.0,10.0,'5-10'),(10.0,1e9,'>10'))
SELECT b.lbl AS rvol_band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.volfrac>=0.5 AND t.volfrac<=1.5 AND t.rvol>=b.lo AND t.rvol<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== H) rvol ceilings, volfrac[0.5,1.5] CONDITIONED — does rvol add on top of production? ===' z;
SELECT 'vf[.5,1.5] & rvol<2' g,* FROM pf(volfrac>=0.5 AND volfrac<=1.5 AND rvol<2.0);
SELECT 'vf[.5,1.5] & rvol<3' g,* FROM pf(volfrac>=0.5 AND volfrac<=1.5 AND rvol<3.0);
SELECT 'vf[.5,1.5] & rvol<5' g,* FROM pf(volfrac>=0.5 AND volfrac<=1.5 AND rvol<5.0);

SELECT '=== F) baseline ===' z;
SELECT 'all (volfrac off)' g,* FROM pf(true);
SELECT 'volfrac[0.5,1.5] (production)' g,* FROM pf(volfrac>=0.5 AND volfrac<=1.5);
