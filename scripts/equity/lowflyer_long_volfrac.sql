-- LowFlyer LONG — relax the volume-confirm gate to a FRACTION of the vol high.
--
-- Run with --vol-high-frac 0.8 (enter if breakout-bar vol >= 80% of the running
-- session 1m-vol high). `vol_vs_high` = breakout_bar_vol / runVolHi (continuous).
-- Question (user): does entering NEAR the vol high (0.8-1.0x) instead of strictly
-- OVER it (>=1.0x) recover trips without losing the edge? Where's the knee?
--
-- Production ENGINE gates in the CSV (flush<=-0.7%, floor>=-12%, ATR<0.02) + vol-frac 0.8.
-- Apply full production SELECTION, then bucket by vol_vs_high. Clipped PF, ret=ret_moc.
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_long_volfrac.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik=fs.cik WHERE fs.value>0;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT symbol,trade_date,entry_price,ret_moc,new_vol_high,vol_vs_high,
                    chg_1d,chg_3d,chg_7d,chg_20m,day_close,prev_bar_close
             FROM read_csv_auto('/tmp/lowflyer_long_frac80.csv')),
withctx AS (SELECT r.*, mc.avgvol20*r.day_close AS adv20,
                   r.entry_price/NULLIF(r.prev_bar_close,0)-1.0 AS flush_1m
            FROM raw r JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date),
withflt AS (SELECT w.*, fl.float_usd, fl.period_end AS flt_pe FROM withctx w
            ASOF LEFT JOIN flt fl ON fl.ticker=w.symbol AND fl.known_date<=w.trade_date)
SELECT wf.symbol, wf.trade_date, YEAR(wf.trade_date) yr, wf.ret_moc AS ret,
       wf.new_vol_high, wf.vol_vs_high, wf.flush_1m,
       wf.chg_1d, wf.chg_3d, wf.chg_7d, wf.chg_20m, wf.adv20,
       CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close>0 AND ap_en.adj_close>0
            THEN wf.float_usd*ap_en.adj_close/ap_pe.adj_close END AS fentry
FROM withflt wf
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker=wf.symbol AND ap_pe.date<=wf.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker=wf.symbol AND ap_en.date=wf.trade_date;

CREATE OR REPLACE TEMP TABLE prod AS
SELECT * FROM t
WHERE chg_1d<=-0.08 AND chg_20m<=-0.03 AND chg_3d>=-0.03 AND chg_3d<=0.30 AND chg_7d>=-0.05
  AND flush_1m>=-0.12 AND adv20>=500000 AND fentry IS NOT NULL AND fentry<300e6;

.mode box
SELECT '=== production long @ vol-high-frac 0.8 (whole book) ===' z;
SELECT COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct FROM prod;

-- bucket by vol_vs_high (the knee finder). 0.8-1.0 = the newly-admitted "near but not over"
-- band; >=1.0 = a true new vol high (the original gate).
SELECT '=== bucketed by vol_vs_high (breakout vol / session vol high) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.80,0.90,'0.80-0.90'),(0.90,0.95,'0.90-0.95'),(0.95,1.00,'0.95-1.00'),
  (1.00,1.25,'1.00-1.25 (new high)'),(1.25,2.0,'1.25-2.0'),(2.0,1e9,'>=2.0'))
SELECT b.lbl AS vol_vs_high, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, b WHERE p.vol_vs_high>=b.lo AND p.vol_vs_high<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- FLOOR sweep: require vol_vs_high >= X (find the trip/PF tradeoff vs the strict 1.0 gate)
SELECT '=== vol_vs_high FLOOR sweep (require >= X) ===' z;
WITH f(x) AS (VALUES (0.80),(0.85),(0.90),(0.95),(1.00),(1.10),(1.25))
SELECT printf('vol_vs_high >= %.2f', f.x) g, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, f WHERE p.vol_vs_high>=f.x GROUP BY f.x ORDER BY f.x;
