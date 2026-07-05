-- LowFlyer LONG — experiment: DROP the volume-confirmation gate (--no-vol-high).
--
-- Hypothesis (user): the 1m-flush gate is load-bearing on the long; the vol-high
-- requirement may just be cutting trade count. Input CSV = production ENGINE gates
-- (flush<=-0.7%, flush-floor>=-12%, ATR<0.02) but with the vol-high gate DROPPED, so
-- entries fire on the FIRST new-session-low bar. `new_vol_high` flags which entries
-- WOULD have passed the old gate. Apply the full production SELECTION, then split by it.
--
-- PF = +50%-winner-CLIPPED (long convention). ret = ret_moc. Float ASOF 1:1, re-anchored.
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_long_novolhigh.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik=fs.cik WHERE fs.value>0;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (
  SELECT symbol, trade_date, entry_price, ret_moc, new_vol_high,
         chg_1d, chg_3d, chg_7d, chg_20m, day_close, prev_bar_close
  FROM read_csv_auto('/tmp/lowflyer_long_novolhigh.csv')
),
withctx AS (
  SELECT r.*, mc.avgvol20*r.day_close AS adv20,
         r.entry_price/NULLIF(r.prev_bar_close,0)-1.0 AS flush_1m
  FROM raw r JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
),
withflt AS (
  SELECT w.*, fl.float_usd, fl.period_end AS flt_pe FROM withctx w
  ASOF LEFT JOIN flt fl ON fl.ticker=w.symbol AND fl.known_date<=w.trade_date
)
SELECT wf.symbol, wf.trade_date, YEAR(wf.trade_date) yr, wf.ret_moc AS ret, wf.new_vol_high,
       wf.chg_1d, wf.chg_3d, wf.chg_7d, wf.chg_20m, wf.adv20, wf.flush_1m,
       CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close>0 AND ap_en.adj_close>0
            THEN wf.float_usd*ap_en.adj_close/ap_pe.adj_close END AS fentry
FROM withflt wf
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker=wf.symbol AND ap_pe.date<=wf.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker=wf.symbol AND ap_en.date=wf.trade_date;

-- full production SELECTION (everything except the vol gate, which the engine dropped)
CREATE OR REPLACE TEMP TABLE prod AS
SELECT * FROM t
WHERE chg_1d<=-0.08 AND chg_20m<=-0.03 AND chg_3d>=-0.03 AND chg_3d<=0.30 AND chg_7d>=-0.05
  AND flush_1m>=-0.12 AND adv20>=500000 AND fentry IS NOT NULL AND fentry<300e6;

CREATE OR REPLACE TEMP MACRO pf(tbl) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM query_table(tbl);

.mode box
SELECT '=== production long, VOL GATE DROPPED (whole book) ===' z;
SELECT * FROM pf('prod');

-- the split: which entries made a new vol high vs not
SELECT '=== split by new_vol_high (1 = would have passed the old gate) ===' z;
SELECT new_vol_high, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM prod GROUP BY new_vol_high ORDER BY new_vol_high DESC;

-- reference: the vol-high=1 subset SHOULD ~= the current production (PF 3.45 / 870)
-- (not exact: earlier vs later same-bar entries can differ, but close)
SELECT '=== reference: production WITH vol gate (from gated CSV) ===' z;
WITH g AS (
  SELECT r.symbol, r.trade_date, r.ret_moc AS ret, r.chg_1d, r.chg_3d, r.chg_7d, r.chg_20m,
         mc.avgvol20*r.day_close AS adv20, r.entry_price/NULLIF(r.prev_bar_close,0)-1.0 AS flush_1m,
         r.day_close, fl.float_usd, fl.period_end flt_pe
  FROM read_csv_auto('/tmp/lowflyer_long_gated_floored.csv') r
  JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
  ASOF LEFT JOIN flt fl ON fl.ticker=r.symbol AND fl.known_date<=r.trade_date
), g2 AS (
  SELECT g.*, CASE WHEN g.float_usd IS NOT NULL AND ap_pe.adj_close>0 AND ap_en.adj_close>0
                   THEN g.float_usd*ap_en.adj_close/ap_pe.adj_close END AS fentry
  FROM g
  ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker=g.symbol AND ap_pe.date<=g.flt_pe
  LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker=g.symbol AND ap_en.date=g.trade_date
)
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM g2 WHERE chg_1d<=-0.08 AND chg_20m<=-0.03 AND chg_3d>=-0.03 AND chg_3d<=0.30 AND chg_7d>=-0.05
  AND flush_1m>=-0.12 AND adv20>=500000 AND fentry IS NOT NULL AND fentry<300e6;
