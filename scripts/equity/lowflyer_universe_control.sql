-- LowFlyer LONG — the §S39d LOOKAHEAD CONTROL (2026-09-05): the production spec on the LEGACY universe
-- (mr_candidate: adjusted-$1 floor + future-episode warmup) vs the CLEAN universe (mr_candidate_1s: the
-- 1s-native table LowFader runs on; no price floor, no warmup). Same engine gates, same window
-- 2016-08-08..2026-06-25. Inputs: data/lowflyer_long_prod.csv (legacy, full period, windowed here) and
-- data/lowflyer_long_clean.csv (clean). Selection is the production spec; the daily context (ADV, rvol)
-- comes from EACH book's own table. Two ADV conventions: the 1m book's avgvol20*day_close (day-D
-- quantities — a lookahead by the protocol) and the causal avgvol20_prior*prev_close. rvol on the clean
-- table = rvol_0945_honest (the 1m rvol_0945 definition does not exist there).
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_universe_control.sql
ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik WHERE fs.value > 0;
CREATE OR REPLACE TEMP TABLE br AS
SELECT date, LAG(pct_above_20) OVER (ORDER BY date) AS b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet';

CREATE OR REPLACE TEMP TABLE raw AS
SELECT 'legacy' AS uni, symbol, trade_date, entry_price, ret_moc, vol_vs_high, chg_1d, chg_3d, chg_7d, chg_20m, day_close, prev_bar_close, prev_adj_close, adj_ratio
FROM read_csv_auto('data/lowflyer_long_prod.csv') WHERE trade_date >= DATE '2016-08-08'
UNION ALL
SELECT 'clean', symbol, trade_date, entry_price, ret_moc, vol_vs_high, chg_1d, chg_3d, chg_7d, chg_20m, day_close, prev_bar_close, prev_adj_close, adj_ratio
FROM read_csv_auto('data/lowflyer_long_clean.csv');

CREATE OR REPLACE TEMP TABLE ctx AS
SELECT r.*,
       CASE WHEN r.uni='legacy' THEN m.avgvol20 * r.day_close ELSE s.avgvol20 * r.day_close END AS adv_dayd,
       CASE WHEN r.uni='legacy' THEN m.avgvol20_prior * r.prev_adj_close / NULLIF(r.adj_ratio,0) ELSE s.avgvol20_prior * r.prev_adj_close / NULLIF(r.adj_ratio,0) END AS adv_causal,
       CASE WHEN r.uni='legacy' THEN m.rvol_0945 ELSE s.rvol_0945_honest END AS rvol,
       r.entry_price/NULLIF(r.prev_bar_close,0)-1 AS flush_1m,
       r.entry_price/NULLIF(r.adj_ratio,0) AS entry_raw,
       (s.ticker IS NOT NULL) AS in_clean, (m.ticker IS NOT NULL) AS in_legacy
FROM raw r
LEFT JOIN mr_candidate    m ON m.ticker = r.symbol AND m.date = r.trade_date
LEFT JOIN mr_candidate_1s s ON s.ticker = r.symbol AND s.date = r.trade_date;

CREATE OR REPLACE TEMP TABLE t AS
SELECT c.*, YEAR(c.trade_date) AS yr, c.ret_moc AS ret, b.b_lag1 AS breadth,
       CASE WHEN c.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN c.float_usd * ap_en.adj_close / ap_pe.adj_close END AS fentry
FROM (SELECT w.*, fl.float_usd, fl.period_end AS flt_pe FROM ctx w
      ASOF LEFT JOIN flt fl ON fl.ticker = w.symbol AND fl.known_date <= w.trade_date) c
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker = c.symbol AND ap_pe.date <= c.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker = c.symbol AND ap_en.date = c.trade_date
LEFT JOIN br b ON b.date = c.trade_date;

.mode box
SELECT '=== gated books (engine gates only), same window ===' z;
SELECT uni, COUNT(*) n, COUNT(DISTINCT symbol||trade_date) tkd,
  ROUND(SUM(CASE WHEN ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),3) raw_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct, ROUND(100.0*AVG(ret),2) avg_pct,
  SUM(CASE WHEN in_clean THEN 1 ELSE 0 END) n_in_clean, SUM(CASE WHEN in_legacy THEN 1 ELSE 0 END) n_in_legacy,
  ROUND(100.0*AVG(CASE WHEN entry_raw < 1 THEN 1 ELSE 0 END),1) pct_sub1
FROM t GROUP BY uni ORDER BY uni;

-- the production selection, three ADV/rvol conventions
CREATE OR REPLACE TEMP TABLE prod AS
SELECT *, CASE WHEN breadth >= 0.65 THEN 3.0 ELSE 1.0 END AS w,
  (adv_dayd >= 500000 AND rvol >= 0.1) AS sel_1m,
  (adv_causal >= 500000) AS sel_causal,
  TRUE AS sel_noadv
FROM t
WHERE chg_1d <= -0.08 AND chg_20m <= -0.03 AND chg_3d >= -0.03 AND chg_3d <= 0.30 AND chg_7d >= -0.05
  AND flush_1m >= -0.12 AND fentry IS NOT NULL AND fentry < 300e6;

SELECT '=== PRODUCTION SPEC x universe x ADV convention (2016-08..2026-06) ===' z;
SELECT uni, conv, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) raw_pf,
  ROUND(SUM(CASE WHEN ret>0 THEN w*ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN w*ret END),0),2) sized_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct, ROUND(100.0*AVG(ret),2) avg_pct, ROUND(100.0*SUM(ret),0) net_pct,
  ROUND(100.0*MIN(ret),1) worst_pct, ROUND(100.0*AVG(CASE WHEN ret<-0.2 THEN 1 ELSE 0 END),2) tail_pct,
  ROUND(100.0*AVG(CASE WHEN entry_raw < 1 THEN 1 ELSE 0 END),1) pct_sub1
FROM (SELECT *, '1 adv_dayD+rvol (1m book)' conv FROM prod WHERE sel_1m
      UNION ALL SELECT *, '2 adv_causal' FROM prod WHERE sel_causal
      UNION ALL SELECT *, '3 no adv/rvol' FROM prod WHERE sel_noadv)
GROUP BY uni, conv ORDER BY conv, uni;

SELECT '=== by year: legacy vs clean, convention 2 (causal ADV) ===' z;
SELECT yr,
  SUM(CASE WHEN uni='legacy' THEN 1 ELSE 0 END) n_leg,
  ROUND(SUM(CASE WHEN uni='legacy' AND ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN uni='legacy' AND ret<0 THEN ret END),0),2) pf_leg,
  SUM(CASE WHEN uni='clean' THEN 1 ELSE 0 END) n_cln,
  ROUND(SUM(CASE WHEN uni='clean' AND ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN uni='clean' AND ret<0 THEN ret END),0),2) pf_cln,
  ROUND(100.0*AVG(CASE WHEN uni='clean' THEN ret END),2) avg_cln, ROUND(100.0*MIN(CASE WHEN uni='clean' THEN ret END),1) worst_cln
FROM prod WHERE sel_causal GROUP BY yr ORDER BY yr;

SELECT '=== CHURN (convention 2): legacy trips whose tkd is NOT in the clean table (the §S39d-selected ones), and clean trips not in legacy ===' z;
SELECT uni, CASE WHEN uni='legacy' THEN in_clean ELSE in_legacy END AS in_other, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) raw_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct, ROUND(100.0*AVG(ret),2) avg_pct, ROUND(100.0*SUM(ret),0) net_pct,
  ROUND(100.0*AVG(CASE WHEN entry_raw < 1 THEN 1 ELSE 0 END),1) pct_sub1, ROUND(MEDIAN(entry_raw),2) med_px
FROM prod WHERE sel_causal GROUP BY 1,2 ORDER BY 1,2;

SELECT '=== clean book by raw entry price band (convention 2) ===' z;
SELECT CASE WHEN entry_raw<1 THEN 'a <$1' WHEN entry_raw<2 THEN 'b $1-2' WHEN entry_raw<5 THEN 'c $2-5' WHEN entry_raw<10 THEN 'd $5-10' ELSE 'e >=$10' END px, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret END),0),2) raw_pf,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct, ROUND(100.0*AVG(ret),2) avg_pct
FROM prod WHERE sel_causal AND uni='clean' GROUP BY 1 ORDER BY 1;
