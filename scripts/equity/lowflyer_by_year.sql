-- LowFlyer LONG — PRODUCTION book BY YEAR (robustness check, 2026-09-05).
-- Input: data/lowflyer_long_prod.csv from scripts/equity/lowflyer_run_prod.sh (engine gates: flush<=-0.7%,
-- flush-floor>=-12%, log-ATR<0.02, vol-high-frac 0.90). Post-hoc SELECTION = the production spec
-- (docs/lowflyer_results.md FINAL): chg_1d<=-8%, chg_20m<=-3%, chg_3d in [-3,+30]%, chg_7d>=-5%,
-- float<$300M (ASOF known_date<=trade_date, re-anchored to entry via adj_close ratio — a RATIO of two
-- adjusted prices, adj_ratio cancels), ADV=avgvol20*day_close>=$500k (⚠ the 1m book's own convention,
-- avgvol20 not _prior), rvol_0945>=0.1. Sizing x3 on LAGGED breadth pct_above_20>=0.65.
-- Reports raw PF, clip PF (+50% clip, the doc's convention), by year; plus era totals.
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_by_year.sql
ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik WHERE fs.value > 0;
CREATE OR REPLACE TEMP TABLE br AS
SELECT date, LAG(pct_above_20) OVER (ORDER BY date) AS b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet';
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT symbol, trade_date, entry_price, ret_moc, vol_vs_high, chg_1d, chg_3d, chg_7d, chg_20m, day_close, prev_bar_close
             FROM read_csv_auto('data/lowflyer_long_prod.csv')),
withctx AS (SELECT r.*, mc.avgvol20 * r.day_close AS adv20, mc.rvol_0945, r.entry_price/NULLIF(r.prev_bar_close,0)-1 AS flush_1m
            FROM raw r JOIN mr_candidate mc ON mc.ticker = r.symbol AND mc.date = r.trade_date),
withflt AS (SELECT w.*, fl.float_usd, fl.period_end AS flt_pe FROM withctx w
            ASOF LEFT JOIN flt fl ON fl.ticker = w.symbol AND fl.known_date <= w.trade_date)
SELECT wf.*, YEAR(wf.trade_date) AS yr, wf.ret_moc AS ret, b.b_lag1 AS breadth,
       CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN wf.float_usd * ap_en.adj_close / ap_pe.adj_close END AS fentry
FROM withflt wf
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker = wf.symbol AND ap_pe.date <= wf.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker = wf.symbol AND ap_en.date = wf.trade_date
LEFT JOIN br b ON b.date = wf.trade_date;
CREATE OR REPLACE TEMP TABLE prod AS
SELECT *, CASE WHEN breadth >= 0.65 THEN 3.0 ELSE 1.0 END AS w FROM t
WHERE chg_1d <= -0.08 AND chg_20m <= -0.03 AND chg_3d >= -0.03 AND chg_3d <= 0.30 AND chg_7d >= -0.05
  AND flush_1m >= -0.12 AND adv20 >= 500000 AND rvol_0945 >= 0.1 AND fentry IS NOT NULL AND fentry < 300e6;
.mode box
SELECT '=== gated CSV ===' z; SELECT COUNT(*) n_gated, MIN(trade_date) d0, MAX(trade_date) d1 FROM t;

SELECT '=== PRODUCTION book, whole period ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) raw_pf,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.5) END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) clip_pf,
  ROUND(SUM(CASE WHEN ret>0 THEN w*ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN w*ret END),0),2) sized_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct, ROUND(100.0*AVG(ret),2) avg_pct,
  ROUND(100.0*SUM(ret),0) net_pct, ROUND(100.0*MIN(ret),1) worst_pct, ROUND(100.0*AVG(CASE WHEN ret<-0.2 THEN 1 ELSE 0 END),2) tail_pct
FROM prod;
SELECT '=== by year ===' z;
SELECT yr, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) raw_pf,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.5) END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) clip_pf,
  ROUND(SUM(CASE WHEN ret>0 THEN w*ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN w*ret END),0),2) sized_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct, ROUND(100.0*AVG(ret),2) avg_pct,
  ROUND(100.0*SUM(ret),0) net_pct, ROUND(100.0*MIN(ret),1) worst_pct, ROUND(100.0*AVG(CASE WHEN ret<-0.2 THEN 1 ELSE 0 END),1) tail_pct,
  SUM(CASE WHEN w=3 THEN 1 ELSE 0 END) n_up
FROM prod GROUP BY yr ORDER BY yr;
SELECT '=== eras ===' z;
SELECT CASE WHEN yr<2017 THEN 'a 2011-16' WHEN yr<2020 THEN 'b 2017-19' WHEN yr<2022 THEN 'c 2020-21' ELSE 'd 2022-26' END era, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) raw_pf,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.5) END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) clip_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct, ROUND(100.0*AVG(ret),2) avg_pct, ROUND(100.0*SUM(ret),0) net_pct
FROM prod GROUP BY 1 ORDER BY 1;
