-- LowFlyer SHORT — 15m-rvol vs 20d-rvol baseline comparison.
--
-- The current exhaustion feature is bar_rvol_15m = breakout_bar_vol / MEAN(1m vol over
-- [9:30,9:45) ET). Concern (user): that 15m baseline is unstable when the premarket / first
-- 15m is unusually heavy or light. A 20-DAY baseline might be a more stable denominator.
--
-- bar_rvol_20d = breakout_bar_vol / (avgvol20_raw / 390), where avgvol20 is the 20d ADJUSTED
-- daily avg volume and 390 = RTH minutes. Volume adjusts INVERSELY to price on splits
-- (adj_vol = raw_vol/adj_ratio), so raw-equivalent 20d avg = avgvol20 * adj_ratio.
--   => per-minute 20d baseline (raw) = avgvol20 * adj_ratio / 390
--   => bar_rvol_20d = breakout_bar_vol / (avgvol20 * adj_ratio / 390)
--
-- open_15m_vs_20d = the OPENING-15m tempo itself vs the 20d baseline:
--   (vol_0945 / nbar_0945)  /  (avgvol20*adj_ratio/390)  -- >1 = heavy open, <1 = light open
-- This is the "is today's open abnormal" gauge. If bar_rvol_15m degrades when this is extreme,
-- the 15m baseline is the unstable one; if bar_rvol_20d holds up, it's the better denominator.
--
-- Base: ATR%>=0.03 (S-spec). ret = -ret_moc. RAW PF. Run:
--   duckdb -readonly data/trading.db < scripts/equity/lowflyer_short_rvol_baseline.sql

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.trade_date, YEAR(r.trade_date) yr,
       -r.ret_moc AS ret,
       r.rvol,
       r.bar_rvol_15m AS brv15,
       r.breakout_bar_vol AS bbv,
       r.intraday_atr_pct_at_entry AS iatr,
       r.chg_1d,
       mc.avgvol20, mc.adj_ratio, mc.vol_0945, mc.nbar_0945,
       -- 20d per-minute raw baseline
       (mc.avgvol20 * mc.adj_ratio / 390.0) AS base20d_permin,
       r.breakout_bar_vol / NULLIF(mc.avgvol20 * mc.adj_ratio / 390.0, 0) AS brv20d,
       -- opening-15m tempo vs 20d baseline (the "abnormal open" gauge)
       (mc.vol_0945::DOUBLE / NULLIF(mc.nbar_0945,0))
         / NULLIF(mc.avgvol20 * mc.adj_ratio / 390.0, 0) AS open15_vs_20d
FROM read_csv_auto('/tmp/lowflyer_short_ungated.csv') r
JOIN mr_candidate mc ON mc.ticker = r.symbol AND mc.date = r.trade_date
WHERE r.intraday_atr_pct_at_entry >= 0.03
  AND mc.avgvol20 > 0 AND mc.adj_ratio > 0;

CREATE OR REPLACE TEMP MACRO pf(tbl) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(ret),2) avg_pct
FROM query_table(tbl);

.mode box

-- ---------------------------------------------------------------------------
-- 0. Sanity: distributions + correlation of the two rvol measures
-- ---------------------------------------------------------------------------
SELECT '=== distributions (ATR>=0.03 base) ===' z;
SELECT
  ROUND(MEDIAN(brv15),2) med_brv15, ROUND(quantile_cont(brv15,0.90),1) p90_brv15,
  ROUND(MEDIAN(brv20d),2) med_brv20d, ROUND(quantile_cont(brv20d,0.90),1) p90_brv20d,
  ROUND(MEDIAN(open15_vs_20d),2) med_open15v20d,
  ROUND(quantile_cont(open15_vs_20d,0.10),2) p10_openv, ROUND(quantile_cont(open15_vs_20d,0.90),2) p90_openv,
  ROUND(CORR(LN(brv15), LN(brv20d)),3) corr_ln
FROM t WHERE brv15>0 AND brv20d>0;

-- ---------------------------------------------------------------------------
-- 1. Head-to-head: bar_rvol_15m floor ladder vs bar_rvol_20d floor ladder
--    (same book, which denominator discriminates better?)
-- ---------------------------------------------------------------------------
SELECT '=== (1a) bar_rvol_15m FLOOR ladder ===' z;
WITH f(x) AS (VALUES (0.0),(4.0),(8.0),(12.0),(20.0),(40.0),(100.0))
SELECT printf('brv15 >= %g', f.x) g, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),2) avg_pct
FROM t, f WHERE t.brv15>=f.x GROUP BY f.x ORDER BY f.x;

SELECT '=== (1b) bar_rvol_20d FLOOR ladder ===' z;
WITH f(x) AS (VALUES (0.0),(4.0),(8.0),(12.0),(20.0),(40.0),(100.0))
SELECT printf('brv20d >= %g', f.x) g, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),2) avg_pct
FROM t, f WHERE t.brv20d>=f.x GROUP BY f.x ORDER BY f.x;

-- ---------------------------------------------------------------------------
-- 2. THE KEY TEST: split by whether the OPEN was abnormal vs 20d, then see how
--    each rvol discriminates WITHIN each regime.
--    Heavy open  = open15_vs_20d high  (15m baseline INFLATED -> brv15 deflated)
--    Light open  = open15_vs_20d low   (15m baseline DEFLATED -> brv15 inflated)
-- ---------------------------------------------------------------------------
SELECT '=== (2) open15_vs_20d buckets: does brv15 stay honest when the open is abnormal? ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.75,'light open (<0.75x 20d)'),
  (0.75,1.5,'normal (0.75-1.5x)'),
  (1.5,3.0,'heavy (1.5-3x)'),
  (3.0,1e9,'very heavy (>3x)'))
SELECT b.lbl AS open_regime, COUNT(*) n,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) pf_all,
  -- brv15>=12 vs brv20d>=12 discrimination within this open-regime
  COUNT(*) FILTER (WHERE t.brv15>=12) n_b15,
  ROUND(SUM(CASE WHEN t.brv15>=12 AND t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.brv15>=12 AND t.ret<0 THEN t.ret ELSE 0 END),0),2) pf_b15_12,
  COUNT(*) FILTER (WHERE t.brv20d>=12) n_b20,
  ROUND(SUM(CASE WHEN t.brv20d>=12 AND t.ret>0 THEN t.ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.brv20d>=12 AND t.ret<0 THEN t.ret ELSE 0 END),0),2) pf_b20_12
FROM t, b WHERE t.open15_vs_20d>=b.lo AND t.open15_vs_20d<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- ---------------------------------------------------------------------------
-- 3. Disagreement cells: when the two rvols DISAGREE, which one is right?
--    (fix brv15>=12; split by brv20d high/low, and vice-versa)
-- ---------------------------------------------------------------------------
SELECT '=== (3a) hold brv15>=12: does ALSO requiring brv20d>=12 help? (2x2) ===' z;
SELECT
  CASE WHEN brv15>=12 THEN 'b15>=12' ELSE 'b15<12' END AS b15,
  CASE WHEN brv20d>=12 THEN 'b20>=12' ELSE 'b20<12' END AS b20,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(ret),2) avg_pct
FROM t GROUP BY 1,2 ORDER BY 1,2;

-- higher tier 2x2 (40x)
SELECT '=== (3b) same 2x2 at the 40x tier ===' z;
SELECT
  CASE WHEN brv15>=40 THEN 'b15>=40' ELSE 'b15<40' END AS b15,
  CASE WHEN brv20d>=40 THEN 'b20>=40' ELSE 'b20<40' END AS b20,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(ret),2) avg_pct
FROM t GROUP BY 1,2 ORDER BY 1,2;
