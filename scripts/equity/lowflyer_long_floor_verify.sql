-- Verify the engine-wired flush-depth floor (--min-bar-flush-floor -0.12) reproduces the
-- SQL-applied Run 26 floor. Read the FLOORED engine CSV, apply the production selection
-- WITHOUT the flush_1m>=-0.12 clause (the engine now enforces it), and compare to the
-- known production numbers (870 trips, clip PF 3.447).
ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik=fs.cik WHERE fs.value>0;

CREATE OR REPLACE TEMP MACRO prod_from(path) AS TABLE
WITH raw AS (SELECT symbol,trade_date,entry_price,ret_moc,chg_1d,chg_3d,chg_7d,chg_20m,day_close,prev_bar_close
             FROM read_csv_auto(path)),
withctx AS (SELECT r.*, mc.avgvol20*r.day_close AS adv20,
  r.entry_price/NULLIF(r.prev_bar_close,0)-1.0 AS flush_1m
  FROM raw r JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date),
withflt AS (SELECT w.*, fl.float_usd, fl.period_end AS flt_pe FROM withctx w
  ASOF LEFT JOIN flt fl ON fl.ticker=w.symbol AND fl.known_date<=w.trade_date)
SELECT wf.ret_moc AS ret, wf.flush_1m, wf.chg_1d, wf.chg_20m, wf.chg_3d, wf.chg_7d, wf.adv20,
  CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close>0 AND ap_en.adj_close>0
       THEN wf.float_usd*ap_en.adj_close/ap_pe.adj_close END AS fentry
FROM withflt wf
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker=wf.symbol AND ap_pe.date<=wf.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker=wf.symbol AND ap_en.date=wf.trade_date
WHERE wf.chg_1d<=-0.08 AND wf.chg_20m<=-0.03 AND wf.chg_3d>=-0.03 AND wf.chg_3d<=0.30
  AND wf.chg_7d>=-0.05 AND wf.adv20>=500000 AND fentry IS NOT NULL AND fentry<300e6;

.mode box
-- (1) FLOORED engine CSV, selection WITHOUT the SQL flush floor -> engine enforces it
SELECT '=== floored engine CSV (engine enforces -12% floor; SQL floor OFF) ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(100.0*MIN(flush_1m),2) deepest_flush_pct  -- should be >= -12
FROM prod_from('/tmp/lowflyer_long_gated_floored.csv');

-- (2) OLD un-floored engine CSV + SQL floor -> the reference production number
SELECT '=== un-floored engine CSV + SQL flush_1m>=-12% (reference) ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf
FROM prod_from('/tmp/lowflyer_long_gated.csv') WHERE flush_1m >= -0.12;

-- (3) OLD un-floored engine CSV, NO flush floor -> the pre-Run-26 number (PF ~3.25)
SELECT '=== un-floored engine CSV, NO floor (pre-Run-26 baseline) ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf
FROM prod_from('/tmp/lowflyer_long_gated.csv');
