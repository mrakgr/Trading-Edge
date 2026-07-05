-- LowFlyer LONG — 1m-flush ramp split by vol_vs_high, FULL [0, inf) range.
--
-- Earlier split used the frac-0.8 book so "<0.9" was only [0.8,0.9). This uses the
-- --no-vol-high book (admits every new-session-low bar, vol_vs_high spans 0->inf) so
-- the full [0,0.9) range is visible. Question: does flush depth stay FLAT across the
-- whole below-0.9 range, or does the deep-below-0.9 (near-zero volume) behave differently?
--
-- Input = /tmp/lowflyer_long_novolhigh2.csv (production ENGINE gates + --no-vol-high, has
-- the vol_vs_high column). Clipped PF, ret=ret_moc.
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_long_flush_by_volfrac.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik=fs.cik WHERE fs.value>0;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT symbol,trade_date,entry_price,ret_moc,vol_vs_high,
                    chg_1d,chg_3d,chg_7d,chg_20m,day_close,prev_bar_close
             FROM read_csv_auto('/tmp/lowflyer_long_novolhigh2.csv')),
withctx AS (SELECT r.*, mc.avgvol20*r.day_close AS adv20,
                   r.entry_price/NULLIF(r.prev_bar_close,0)-1.0 AS flush_1m
            FROM raw r JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date),
withflt AS (SELECT w.*, fl.float_usd, fl.period_end AS flt_pe FROM withctx w
            ASOF LEFT JOIN flt fl ON fl.ticker=w.symbol AND fl.known_date<=w.trade_date)
SELECT wf.ret_moc AS ret, wf.vol_vs_high, wf.flush_1m,
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
-- how the below-0.9 population is distributed across vol_vs_high
SELECT '=== vol_vs_high distribution in the <0.9 region ===' z;
WITH b(lo,hi,lbl) AS (VALUES (0.0,0.25,'0-0.25'),(0.25,0.50,'0.25-0.50'),(0.50,0.70,'0.50-0.70'),(0.70,0.90,'0.70-0.90'))
SELECT b.lbl AS vband, COUNT(*) n,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),2) avg
FROM prod p, b WHERE p.vol_vs_high>=b.lo AND p.vol_vs_high<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- 1m-flush ramp within the FULL <0.9 group vs the >=0.9 group
SELECT '=== 1m-flush ceiling sweep, FULL <0.9 group (vs >=0.9) ===' z;
WITH f(x) AS (VALUES (0.0),(-0.02),(-0.03),(-0.04),(-0.05),(-0.07))
SELECT printf('flush<=%.0f%%',f.x*100) g,
  COUNT(*) FILTER (WHERE p.vol_vs_high<0.9 AND p.flush_1m<=f.x) n_lo,
  ROUND(SUM(CASE WHEN p.vol_vs_high<0.9 AND p.flush_1m<=f.x AND p.ret>0 THEN LEAST(p.ret,0.5) ELSE 0 END)
        /NULLIF(-SUM(CASE WHEN p.vol_vs_high<0.9 AND p.flush_1m<=f.x AND p.ret<0 THEN p.ret ELSE 0 END),0),3) pf_lo,
  COUNT(*) FILTER (WHERE p.vol_vs_high>=0.9 AND p.flush_1m<=f.x) n_hi,
  ROUND(SUM(CASE WHEN p.vol_vs_high>=0.9 AND p.flush_1m<=f.x AND p.ret>0 THEN LEAST(p.ret,0.5) ELSE 0 END)
        /NULLIF(-SUM(CASE WHEN p.vol_vs_high>=0.9 AND p.flush_1m<=f.x AND p.ret<0 THEN p.ret ELSE 0 END),0),3) pf_hi
FROM prod p, f GROUP BY f.x ORDER BY f.x DESC;

-- per-flush-band x vol group (non-cumulative)
SELECT '=== 1m-flush band x vol group (full <0.9) ===' z;
WITH b(lo,hi,lbl) AS (VALUES (-0.02,0.0,'0..-2%'),(-0.04,-0.02,'-2..-4%'),(-0.07,-0.04,'-4..-7%'),(-0.12,-0.07,'-7..-12%'))
SELECT b.lbl AS flush_band,
  CASE WHEN p.vol_vs_high>=0.9 THEN '>=0.9' ELSE '<0.9' END AS vgrp,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.5) ELSE 0 END)/NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),2) avg
FROM prod p, b WHERE p.flush_1m>=b.lo AND p.flush_1m<b.hi
GROUP BY b.lo,b.hi,b.lbl, vgrp ORDER BY b.lo DESC, vgrp DESC;
