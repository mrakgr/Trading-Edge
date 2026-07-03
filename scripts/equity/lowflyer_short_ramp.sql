-- LowFlyer SHORT (mirrored: fade the new-session-HIGH pop) — selection ramp.
--
-- The short is the opposite philosophy to the long pullback-fade: we fade EXTENDED
-- names (up big multi-day AND up big today), so the selection RAMPS UP positive
-- thresholds instead of flooring negative ones. 1m-flush + ATR gates are DISABLED
-- for now (input CSV = --short only, ungated). Baseline: 3d > +50% AND 1d > +30%,
-- then ramp each of 3d, 1d, 20m.
--
-- ret is sign-flipped for the short: short profit = price falls = -(exit/entry-1).
-- chg_1d/chg_3d/chg_20m = entry vs 1d/3d/20m-ago (POSITIVE = up into the fade).
-- PF = +50%-winner-clipped on the short-correct return.
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_short_ramp.sql

CREATE OR REPLACE TEMP TABLE t AS
SELECT symbol, trade_date, YEAR(trade_date) yr,
       -r.ret_moc AS ret,           -- short sign flip
       r.chg_1d, r.chg_3d, r.chg_7d, r.chg_20m, r.bar_rvol_15m,
       mc.avgvol20 * r.day_close AS adv20
FROM read_csv_auto('/tmp/lowflyer_short_ungated.csv') r
JOIN mr_candidate mc ON mc.ticker = r.symbol AND mc.date = r.trade_date;

CREATE OR REPLACE TEMP MACRO pf_stats(tbl) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM query_table(tbl);

.mode box
-- population sanity at the baseline
SELECT '=== baseline: 3d > +50% AND 1d > +30% (ungated short) ===' z;
SELECT COUNT(*) n_total,
  COUNT(*) FILTER (WHERE chg_3d > 0.50) n_3d50,
  COUNT(*) FILTER (WHERE chg_3d > 0.50 AND chg_1d > 0.30) n_base
FROM t;

SELECT '=== whole ungated short book ===' z;
SELECT * FROM pf_stats('t');

-- (1) ramp 3d, holding 1d > +30%
SELECT '=== (1) ramp 3d floor (1d > +30% held) ===' z;
WITH d3(x) AS (VALUES (0.0),(0.30),(0.50),(0.75),(1.0),(1.5),(2.0),(3.0))
SELECT printf('3d > %+.0f%%', d3.x*100) AS d3_floor,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM t p, d3 WHERE p.chg_1d > 0.30 AND p.chg_3d > d3.x GROUP BY d3.x ORDER BY d3.x;

-- (2) ramp 1d, holding 3d > +50%
SELECT '=== (2) ramp 1d floor (3d > +50% held) ===' z;
WITH d1(x) AS (VALUES (0.0),(0.20),(0.30),(0.50),(0.75),(1.0),(1.5))
SELECT printf('1d > %+.0f%%', d1.x*100) AS d1_floor,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM t p, d1 WHERE p.chg_3d > 0.50 AND p.chg_1d > d1.x GROUP BY d1.x ORDER BY d1.x;

-- (3) ramp 20m, holding 3d > +50% AND 1d > +30%
SELECT '=== (3) ramp 20m floor (3d > +50%, 1d > +30% held) ===' z;
WITH d20(x) AS (VALUES (-1.0),(0.0),(0.02),(0.05),(0.10),(0.20))
SELECT CASE WHEN d20.x<=-1 THEN 'any' ELSE printf('20m > %+.0f%%', d20.x*100) END AS d20_floor,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM t p, d20 WHERE p.chg_3d > 0.50 AND p.chg_1d > 0.30 AND (d20.x<=-1 OR p.chg_20m > d20.x)
GROUP BY d20.x ORDER BY d20.x;
