-- LowFlyer SHORT — brv20d as the PRIMARY lever (deep dive).
--
-- brv20d = breakout_bar_vol / (avgvol20*adj_ratio/390) -- breakout bar vs the avg 1m
-- volume implied by the 20d ADJUSTED daily avg. brv20d>=100 = 2,760 trips PF 6.65 beats
-- the current brv15>=40 default (533 trips, PF 4.37) on BOTH axes. This is the new main lever.
-- Push the ladder higher, break down 1d return, and by-year. ret=-ret_moc, RAW PF.
-- Base: ATR%>=0.03. Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_short_brv20d.sql

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.trade_date, YEAR(r.trade_date) yr,
       -r.ret_moc AS ret, r.rvol, r.bar_rvol_15m AS brv15,
       r.intraday_atr_pct_at_entry AS iatr, r.chg_1d, r.chg_20m,
       r.breakout_bar_vol / NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0) AS brv20d
FROM read_csv_auto('/tmp/lowflyer_short_ungated.csv') r
JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0;

.mode box

-- ---------------------------------------------------------------------------
-- 1. Extend the brv20d floor ladder WELL past 100 (find the top of the escalation)
-- ---------------------------------------------------------------------------
SELECT '=== (1) brv20d FLOOR ladder (extended) ===' z;
WITH f(x) AS (VALUES (40.0),(100.0),(150.0),(200.0),(300.0),(500.0),(800.0),(1200.0),(2000.0))
SELECT printf('brv20d >= %g', f.x) g, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),2) avg_pct,
  ROUND(SUM(t.ret)*10000.0/COUNT(*)*COUNT(*)/1000.0,0) AS net_at10k_k  -- net $ @ $10k/trip, in $k
FROM t, f WHERE t.brv20d>=f.x GROUP BY f.x ORDER BY f.x;

-- ---------------------------------------------------------------------------
-- 2. Per-band breakdown of brv20d (non-cumulative) -- is it monotone all the way up?
-- ---------------------------------------------------------------------------
SELECT '=== (2) brv20d per-band (non-cumulative) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,40.0,'0-40'),(40.0,100.0,'40-100'),(100.0,200.0,'100-200'),
  (200.0,400.0,'200-400'),(400.0,800.0,'400-800'),(800.0,1e9,'>800'))
SELECT b.lbl AS brv20d_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),2) avg_pct
FROM t, b WHERE t.brv20d>=b.lo AND t.brv20d<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- ---------------------------------------------------------------------------
-- 3. brv20d>=100 bucket: 1d-return breakdown (does extension add on top?)
-- ---------------------------------------------------------------------------
SELECT '=== (3) brv20d>=100: 1d-return breakdown ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (-1e9,0.0,'<0% (down day)'),(0.0,0.10,'0-10%'),(0.10,0.25,'10-25%'),
  (0.25,0.50,'25-50%'),(0.50,1.0,'50-100%'),(1.0,1.5,'100-150%'),
  (1.5,3.0,'150-300%'),(3.0,1e9,'>=300%'))
SELECT b.lbl AS chg_1d_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),2) avg_pct
FROM t, b WHERE t.brv20d>=100 AND t.chg_1d>=b.lo AND t.chg_1d<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- 1d FLOOR sweep within brv20d>=100
SELECT '=== (3b) brv20d>=100: 1d FLOOR sweep ===' z;
WITH f(x) AS (VALUES (-1e9),(0.0),(0.25),(0.50),(1.0),(1.5))
SELECT CASE WHEN f.x<-1e8 THEN 'any' ELSE printf('1d >= %+.0f%%', f.x*100) END AS floor, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),2) avg_pct
FROM t, f WHERE t.brv20d>=100 AND (f.x<-1e8 OR t.chg_1d>=f.x)
GROUP BY f.x ORDER BY f.x;

-- ---------------------------------------------------------------------------
-- 4. by-year robustness of brv20d>=100 (is the 5x capacity spread across years?)
-- ---------------------------------------------------------------------------
SELECT '=== (4) brv20d>=100: by-year ===' z;
SELECT yr, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) raw_pf,
  ROUND(100.0*AVG(ret),1) avg_pct
FROM t WHERE brv20d>=100 GROUP BY yr ORDER BY yr;
