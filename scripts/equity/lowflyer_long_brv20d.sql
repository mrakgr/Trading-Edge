-- LowFlyer PRODUCTION LONG — breakdown by bar_rvol_20d (the short's new main lever).
--
-- On the SHORT, brv20d>=100 is the new default gate (PF 6.65, 5x the capacity of brv15>=40).
-- On the long, brv15 was a MIRROR: inverted-U, moderate 3-8x best, extreme >=40x COLLAPSES
-- (falling knives, PF ~1.15). Question: does brv20d behave the same way on the long, or does
-- the STABLE 20d baseline change the picture?
--
-- brv20d = breakout_bar_vol / (avgvol20*adj_ratio/390). Population = the LOCKED production long
-- (flush<=-0.7% & log-ATR<0.02 engine gates in the CSV; then 1d<=-8%, 20m<=-3%, 3d in [-3,30]%,
-- 7d>=-5%, 1m-flush>=-12%, ADV>=$500k, float<$300M). METRIC = +50%-winner-CLIPPED PF (long
-- convention -- winners unbounded). ret = ret_moc (long, no sign flip).
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_long_brv20d.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);

CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik
WHERE fs.value > 0;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (
  SELECT symbol, trade_date, entry_price, ret_moc,
         chg_1d, chg_3d, chg_7d, chg_20m, day_close,
         breakout_bar_vol, prev_bar_close   -- 1m-flush = entry_price/prev_bar_close - 1
  FROM read_csv_auto('/tmp/lowflyer_long_gated.csv')
),
withctx AS (
  SELECT r.*, mc.avgvol20, mc.adj_ratio, mc.avgvol20 * r.day_close AS adv20,
         r.breakout_bar_vol / NULLIF(mc.avgvol20 * mc.adj_ratio / 390.0, 0) AS brv20d,
         r.entry_price / NULLIF(r.prev_bar_close,0) - 1.0 AS flush_1m
  FROM raw r
  JOIN mr_candidate mc ON mc.ticker = r.symbol AND mc.date = r.trade_date
),
withflt AS (
  SELECT w.*, fl.float_usd, fl.period_end AS flt_pe
  FROM withctx w
  ASOF LEFT JOIN flt fl ON fl.ticker = w.symbol AND fl.known_date <= w.trade_date
)
SELECT wf.symbol, wf.trade_date, YEAR(wf.trade_date) yr, wf.ret_moc AS ret,
       wf.chg_1d, wf.chg_3d, wf.chg_7d, wf.chg_20m, wf.adv20, wf.brv20d, wf.flush_1m,
       CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN wf.float_usd * ap_en.adj_close / ap_pe.adj_close END AS float_usd_at_entry
FROM withflt wf
ASOF LEFT JOIN split_adjusted_prices ap_pe
  ON ap_pe.ticker = wf.symbol AND ap_pe.date <= wf.flt_pe
LEFT JOIN split_adjusted_prices ap_en
  ON ap_en.ticker = wf.symbol AND ap_en.date = wf.trade_date;

-- locked production selection (incl. 7d>=-5% and 1m-flush>=-12%, per the final spec)
CREATE OR REPLACE TEMP TABLE prod AS
SELECT * FROM t
WHERE chg_1d <= -0.08
  AND chg_20m <= -0.03
  AND chg_3d >= -0.03 AND chg_3d <= 0.30
  AND chg_7d >= -0.05
  AND flush_1m >= -0.12
  AND adv20 >= 500000
  AND float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 300e6;

.mode box
SELECT '=== production long baseline (should be ~PF 3.45 / 870) ===' z;
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct,
  ROUND(MEDIAN(brv20d),1) med_brv20d, ROUND(quantile_cont(brv20d,0.90),1) p90_brv20d
FROM prod;

-- A) per-band breakdown by brv20d (is it an inverted-U like brv15, or monotone?)
SELECT '=== A) brv20d per-band (non-cumulative) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,10.0,'0-10'),(10.0,25.0,'10-25'),(25.0,50.0,'25-50'),(50.0,100.0,'50-100'),
  (100.0,200.0,'100-200'),(200.0,1e9,'>=200'))
SELECT b.lbl AS brv20d_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, b WHERE p.brv20d>=b.lo AND p.brv20d<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- B) brv20d FLOOR sweep
SELECT '=== B) brv20d FLOOR sweep (>= X) ===' z;
WITH f(x) AS (VALUES (0.0),(10.0),(25.0),(50.0),(100.0),(200.0))
SELECT printf('brv20d >= %g', f.x) g, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, f WHERE p.brv20d>=f.x GROUP BY f.x ORDER BY f.x;

-- C) brv20d CEILING sweep (does capping it help, like brv15's falling-knife ceiling?)
SELECT '=== C) brv20d CEILING sweep (<= X) ===' z;
WITH f(x) AS (VALUES (10.0),(25.0),(50.0),(100.0),(1e9))
SELECT CASE WHEN f.x>1e8 THEN 'none' ELSE printf('brv20d <= %g', f.x) END g, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, f WHERE p.brv20d<=f.x GROUP BY f.x ORDER BY f.x;
