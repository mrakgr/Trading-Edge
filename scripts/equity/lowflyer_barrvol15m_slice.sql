-- LowFlyer — slice by bar_rvol_15m (breakout-bar volume vs the [9:30,9:45) 1m baseline).
--
-- Feature: bar_rvol_15m = breakout_bar_vol / mean(1m volume over [9:30, 9:45) ET).
-- The mean baseline = vol_0945 / nbar_0945 (both already in mr_candidate: vol_0945 is the
-- RTH-only 09:30-09:45 volume SUM, nbar_0945 the bar count). The breakout bar must re-take
-- the session 1m-volume high to fire, so it's high-vol by construction; this feature asks
-- HOW extreme that spike is RELATIVE to the name's own opening-15m tempo. Thesis: extreme
-- 1m spikes (a genuine climactic bar) fade with a much higher PF — but "extreme" is only
-- knowable against the per-name 15m baseline.
--
-- Both joins 1:1 (mr_candidate unique on ticker,date). Parameterize the input CSV by editing
-- the read_csv_auto path (long: /tmp/lowflyer_long_gated.csv, short: /tmp/lowflyer_short_gated.csv).
-- For the short, ret is already sign-correct in ret_moc? NO — ret_moc = exit/entry-1 (price
-- move); net_pnl carries the short sign. Use ret = (short ? -ret_moc : ret_moc). We pass the
-- side via a SET below.
--
-- PF = +50%-winner-clipped on the SIDE-CORRECT per-trade return.
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_barrvol15m_slice.sql

-- side: 'long' or 'short' (flips ret_moc sign). Edit these two lines to switch books.
SET VARIABLE csv_path = '/tmp/lowflyer_long_gated.csv';
SET VARIABLE is_short = FALSE;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (
  SELECT symbol, trade_date, breakout_bar_vol, ret_moc,
         chg_1d, chg_3d, chg_7d, chg_20m, day_close
  FROM read_csv_auto(getvariable('csv_path'))
)
SELECT r.symbol, r.trade_date, r.chg_1d, r.chg_3d, r.chg_7d, r.chg_20m,
       (CASE WHEN getvariable('is_short') THEN -r.ret_moc ELSE r.ret_moc END) AS ret,
       mc.avgvol20 * r.day_close AS adv20,
       r.breakout_bar_vol,
       mc.vol_0945::DOUBLE / NULLIF(mc.nbar_0945,0) AS mean_bar_vol_15m,
       r.breakout_bar_vol::DOUBLE / NULLIF(mc.vol_0945::DOUBLE / NULLIF(mc.nbar_0945,0),0) AS bar_rvol_15m
FROM raw r JOIN mr_candidate mc ON mc.ticker = r.symbol AND mc.date = r.trade_date;

-- float ASOF (1:1) for the production book
ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik WHERE fs.value > 0;

CREATE OR REPLACE TEMP TABLE tf AS
WITH wf AS (
  SELECT t.*, fl.float_usd, fl.period_end AS flt_pe
  FROM t ASOF LEFT JOIN flt fl ON fl.ticker = t.symbol AND fl.known_date <= t.trade_date
)
SELECT wf.*,
  CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close>0 AND ap_en.adj_close>0
       THEN wf.float_usd*ap_en.adj_close/ap_pe.adj_close END AS float_at_entry
FROM wf
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker=wf.symbol AND ap_pe.date<=wf.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker=wf.symbol AND ap_en.date=wf.trade_date;

.mode box
-- reusable bucket + PF expression via a macro-free inline CASE.
-- (A) FULL GATED BOOK — raw discrimination (no selection filters)
SELECT '=== A) FULL gated book — PF by bar_rvol_15m ===' z;
SELECT
  CASE WHEN bar_rvol_15m IS NULL THEN '0:NA'
       WHEN bar_rvol_15m <  3  THEN '1:<3x'
       WHEN bar_rvol_15m <  5  THEN '2:3-5x'
       WHEN bar_rvol_15m <  8  THEN '3:5-8x'
       WHEN bar_rvol_15m < 12  THEN '4:8-12x'
       WHEN bar_rvol_15m < 20  THEN '5:12-20x'
       WHEN bar_rvol_15m < 40  THEN '6:20-40x'
       ELSE '7:>=40x' END AS bar_rvol_bucket,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t GROUP BY 1 ORDER BY 1;

-- (A2) FULL gated book — FLOOR sweep (keep bar_rvol_15m >= X)
SELECT '=== A2) FULL gated book — bar_rvol_15m FLOOR sweep ===' z;
WITH fl(x) AS (VALUES (0.0),(3.0),(5.0),(8.0),(12.0),(20.0),(40.0))
SELECT CASE WHEN fl.x<=0 THEN 'none' ELSE printf('>= %.0fx', fl.x) END AS floor,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM t p, fl WHERE p.bar_rvol_15m >= fl.x GROUP BY fl.x ORDER BY fl.x;

-- (B) PRODUCTION book — PF by bar_rvol_15m (does it add ON TOP of the full selection?)
-- long production selection; for the SHORT book these thresholds are the long analogs and are
-- only a first look (the true mirrored-short funnel is a later step) — but the bar_rvol slice
-- is side-agnostic, so we still see whether the spike discriminates within a selected book.
CREATE OR REPLACE TEMP TABLE prod AS
SELECT * FROM tf
WHERE chg_1d <= -0.08 AND chg_20m <= -0.03
  AND chg_3d >= -0.03 AND chg_3d <= 0.30
  AND chg_7d >= -0.05
  AND adv20 >= 500000 AND float_at_entry IS NOT NULL AND float_at_entry < 300e6;

SELECT '=== B) PRODUCTION book — PF by bar_rvol_15m ===' z;
SELECT
  CASE WHEN bar_rvol_15m IS NULL THEN '0:NA'
       WHEN bar_rvol_15m <  3  THEN '1:<3x'
       WHEN bar_rvol_15m <  5  THEN '2:3-5x'
       WHEN bar_rvol_15m <  8  THEN '3:5-8x'
       WHEN bar_rvol_15m < 12  THEN '4:8-12x'
       WHEN bar_rvol_15m < 20  THEN '5:12-20x'
       ELSE '6:>=20x' END AS bar_rvol_bucket,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM prod GROUP BY 1 ORDER BY 1;

SELECT '=== B2) PRODUCTION book — bar_rvol_15m FLOOR sweep ===' z;
WITH fl(x) AS (VALUES (0.0),(3.0),(5.0),(8.0),(12.0),(20.0))
SELECT CASE WHEN fl.x<=0 THEN 'none' ELSE printf('>= %.0fx', fl.x) END AS floor,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, fl WHERE p.bar_rvol_15m >= fl.x GROUP BY fl.x ORDER BY fl.x;
