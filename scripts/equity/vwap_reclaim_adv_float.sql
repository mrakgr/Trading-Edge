-- VwapReclaim ADV + FLOAT breakdown on the PRODUCTION CELL, over the WIDE (all-ADV) universe.
--
-- Question (user, 2026-07-06): now that the exit is fixed (close-stop d*2/3 + NO target), are the
-- LOWER ADV tiers and LOWER float names — which the $100M floor + charts had us cut — actually
-- profitable? The $100M floor was set under the OLD exit (target + d/3 stop) from illiquid-junk charts.
--
-- Population = /tmp/vwr_wide_trips.csv (engine over vwap_reclaim_candidate_wide = mr_candidate WHERE
-- rvol_0945>1, NO ADV floor; --no-target; 2020-07..2025-06). We re-impose the production-cell SLICES
-- in SQL: morning 10:00-13:30 ET, run_below_vwap in [11,30], intraday_tightness >= 4.5.
-- PF convention = the intraday MOC book's ret_moc (NO +50% clip — this is not a HighFlyer multi-day book).
--
-- ADV = avgvol20 * day_close (20d avg DOLLAR volume), joined from the wide candidate table by (ticker,date).
-- Float (canonical, from tideflyer_float.sql): SEC dei:EntityPublicFloat re-anchored to entry-day price,
--   ASOF known_date <= entry_date (no-lookahead).
-- Run: duckdb -readonly data/trading.db < scripts/equity/vwap_reclaim_adv_float.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);

CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik
WHERE fs.value > 0;

-- production-cell trips + ADV (from the wide candidate table) + float-at-entry
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (
  SELECT * FROM read_csv_auto('/tmp/vwr_wide_trips.csv')
  WHERE date_part('hour',entry_time)*60 + date_part('minute',entry_time) BETWEEN 600 AND 810  -- 10:00-13:30 ET
    AND run_below_vwap BETWEEN 11 AND 30
    AND intraday_tightness_at_entry >= 4.5
),
withadv AS (
  SELECT raw.*, c.avgvol20 * c.day_close AS adv_usd
  FROM raw JOIN vwap_reclaim_candidate_wide c
    ON c.ticker = raw.symbol AND c.date = raw.trade_date
),
withflt AS (
  SELECT w.*, fl.float_usd, fl.period_end AS flt_pe
  FROM withadv w
  ASOF LEFT JOIN flt fl ON fl.ticker = w.symbol AND fl.known_date <= w.trade_date
)
SELECT w.symbol, w.trade_date, w.ret_moc AS ret, w.net_pnl AS pnl, w.adv_usd, w.float_usd,
       CASE WHEN w.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN w.float_usd * ap_en.adj_close / ap_pe.adj_close END AS float_usd_at_entry
FROM withflt w
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker = w.symbol AND ap_pe.date <= w.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker = w.symbol AND ap_en.date = w.trade_date;

CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct,
  ROUND(SUM(pnl)/1000.0,1) net_k
FROM t WHERE cond;

.mode box
SELECT '=== population (production cell, WIDE universe) ===' z;
SELECT COUNT(*) trips,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf
FROM t;

SELECT '=== A) ADV per-bucket ===' z;
SELECT
  CASE WHEN adv_usd <  10e6 THEN '1:<10M'
       WHEN adv_usd <  30e6 THEN '2:10-30M'
       WHEN adv_usd < 100e6 THEN '3:30-100M'
       WHEN adv_usd < 300e6 THEN '4:100-300M'
       WHEN adv_usd <   1e9 THEN '5:300M-1B'
       WHEN adv_usd <   5e9 THEN '6:1-5B'
       ELSE '7:>5B' END AS adv_bucket,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct, ROUND(SUM(pnl)/1000.0,1) net_k
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== B) ADV cumulative FLOOR: keep ADV >= N ===' z;
SELECT '>=10M'  g,* FROM pf(adv_usd >= 10e6);
SELECT '>=30M'  g,* FROM pf(adv_usd >= 30e6);
SELECT '>=100M' g,* FROM pf(adv_usd >= 100e6);
SELECT '>=300M' g,* FROM pf(adv_usd >= 300e6);
SELECT '>=1B'   g,* FROM pf(adv_usd >= 1e9);

SELECT '=== C) ADV cumulative CEILING: keep ADV <= N ===' z;
SELECT '<30M'   g,* FROM pf(adv_usd < 30e6);
SELECT '<100M'  g,* FROM pf(adv_usd < 100e6);
SELECT '<300M'  g,* FROM pf(adv_usd < 300e6);

SELECT '=== D) FLOAT per-bucket ===' z;
SELECT
  CASE WHEN float_usd_at_entry IS NULL THEN '0:NO DATA'
       WHEN float_usd_at_entry < 150e6 THEN '1:<150M'
       WHEN float_usd_at_entry < 300e6 THEN '2:150-300M'
       WHEN float_usd_at_entry < 750e6 THEN '3:300-750M'
       WHEN float_usd_at_entry < 2e9   THEN '4:750M-2B'
       WHEN float_usd_at_entry < 10e9  THEN '5:2-10B'
       ELSE '6:>10B' END AS float_bucket,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct, ROUND(SUM(pnl)/1000.0,1) net_k
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== E) FLOAT cumulative FLOOR: keep float >= N (covered only) ===' z;
SELECT '>=150M' g,* FROM pf(float_usd_at_entry >= 150e6);
SELECT '>=300M' g,* FROM pf(float_usd_at_entry >= 300e6);
SELECT '>=750M' g,* FROM pf(float_usd_at_entry >= 750e6);
SELECT '>=2B'   g,* FROM pf(float_usd_at_entry >= 2e9);

SELECT '=== F) FLOAT cumulative CEILING: keep float <= N (covered only) ===' z;
SELECT '<150M'  g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 150e6);
SELECT '<300M'  g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 300e6);
SELECT '<750M'  g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 750e6);
