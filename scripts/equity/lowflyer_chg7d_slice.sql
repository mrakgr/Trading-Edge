-- LowFlyer PRODUCTION long — slice by chg_7d (7-day return into entry).
--
-- Question: does adding a 7-day-return FLOOR on top of the locked production spec
-- improve performance? The production system is already a *pullback* fade (3d ∈
-- [−3%, +30%] keeps flat-to-up-over-3d names); chg_7d asks the same question one
-- window wider — is the fade better when the name is ALSO up (or not too down)
-- over the trailing 7 trading days?
--
-- Production spec applied here (all locked): engine gates flush ≤ −0.7% + log-ATR
-- < 0.02 (already in the input CSV), then selection 1d ≤ −8%, 20m ≤ −3%, 3d ∈
-- [−3%, +30%], ADV ≥ $500k, dollar-float < $300M. rvol_0945 ≥ 0.1 is pre-pruned in
-- mr_candidate. Then break the residual book down by chg_7d.
--
-- Float join is 1:1 (ASOF latest known float <= entry, re-anchored to entry price,
-- split-safe) — the fan-out-free pattern from float_breakdown.sql. mr_candidate join
-- is 1:1 (unique index on ticker,date), so avgvol20/rvol_0945 don't fan out either.
--
-- PF = +50%-winner-clipped: Σ min(ret,0.5) over wins / −Σ ret over losses, ret = ret_moc.
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_chg7d_slice.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);

-- float per ticker, exploded via ticker_cik (as float_breakdown.sql)
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik
WHERE fs.value > 0;

-- production population: gated trips + mr_candidate context + float, all 1:1 joins.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (
  SELECT symbol, trade_date, entry_price, ret_moc,
         chg_1d, chg_3d, chg_7d, chg_20m, day_close
  FROM read_csv_auto('/tmp/lowflyer_long_gated.csv')
),
-- 1:1 back-join to mr_candidate for avgvol20 (ADV) + rvol_0945 (unique per ticker,date).
withctx AS (
  SELECT r.*, mc.avgvol20, mc.avgvol20 * r.day_close AS adv20
  FROM raw r
  JOIN mr_candidate mc ON mc.ticker = r.symbol AND mc.date = r.trade_date
),
-- ASOF latest known float at/<= entry_date (1 row per trip → no fan-out)
withflt AS (
  SELECT w.*, fl.float_usd, fl.period_end AS flt_pe
  FROM withctx w
  ASOF LEFT JOIN flt fl
    ON fl.ticker = w.symbol AND fl.known_date <= w.trade_date
)
SELECT wf.symbol, wf.trade_date, wf.ret_moc AS ret,
       wf.chg_1d, wf.chg_3d, wf.chg_7d, wf.chg_20m, wf.adv20,
       wf.float_usd,
       ap_pe.adj_close AS px_pe, ap_en.adj_close AS px_en,
       CASE WHEN wf.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN wf.float_usd * ap_en.adj_close / ap_pe.adj_close END AS float_usd_at_entry
FROM withflt wf
ASOF LEFT JOIN split_adjusted_prices ap_pe
  ON ap_pe.ticker = wf.symbol AND ap_pe.date <= wf.flt_pe
LEFT JOIN split_adjusted_prices ap_en
  ON ap_en.ticker = wf.symbol AND ap_en.date = wf.trade_date;

-- the locked production selection (everything EXCEPT chg_7d, which we're slicing).
CREATE OR REPLACE TEMP TABLE prod AS
SELECT * FROM t
WHERE chg_1d <= -0.08
  AND chg_20m <= -0.03
  AND chg_3d >= -0.03 AND chg_3d <= 0.30
  AND adv20 >= 500000
  AND float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 300e6;

.mode box
SELECT '=== production baseline (no chg_7d filter) ===' z;
SELECT COUNT(*) n,
       ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
       ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
             / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
       ROUND(100.0*AVG(ret),3) avg_pct,
       ROUND(100.0*median(chg_7d),1) med_chg7d
FROM prod;

-- A) per-bucket breakdown by chg_7d (diagnostic — where does the 7d return help?)
SELECT '=== A) per-bucket PF by chg_7d ===' z;
SELECT
  CASE WHEN chg_7d IS NULL OR isnan(chg_7d) THEN '0:NO DATA'
       WHEN chg_7d < -0.30 THEN '1:<-30%'
       WHEN chg_7d < -0.15 THEN '2:-30..-15'
       WHEN chg_7d < -0.05 THEN '3:-15..-5'
       WHEN chg_7d <  0.00 THEN '4:-5..0'
       WHEN chg_7d <  0.15 THEN '5:0..15'
       WHEN chg_7d <  0.40 THEN '6:15..40'
       WHEN chg_7d <  1.00 THEN '7:40..100'
       ELSE '8:>=100%' END AS chg7d_bucket,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM prod GROUP BY 1 ORDER BY 1;

-- B) chg_7d FLOOR sweep (cumulative: keep trips with chg_7d >= X)
SELECT '=== B) chg_7d FLOOR sweep (>= X) ===' z;
WITH floors(x) AS (VALUES (-1.0),(-0.30),(-0.15),(-0.05),(0.0),(0.10),(0.25),(0.50),(1.0))
SELECT
  CASE WHEN f.x <= -1.0 THEN 'none' ELSE printf('>= %+.0f%%', f.x*100) END AS chg7d_floor,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, floors f
WHERE p.chg_7d IS NOT NULL AND NOT isnan(p.chg_7d) AND p.chg_7d >= f.x
GROUP BY f.x ORDER BY f.x;

-- C) chg_7d CEILING sweep (keep chg_7d <= X) — in case the tail (parabolic 7d runners) hurts
SELECT '=== C) chg_7d CEILING sweep (<= X), floor held at production ===' z;
WITH ceils(x) AS (VALUES (10.0),(2.0),(1.0),(0.50),(0.30),(0.15))
SELECT
  CASE WHEN c.x >= 10.0 THEN 'none' ELSE printf('<= %+.0f%%', c.x*100) END AS chg7d_ceil,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN p.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN p.ret>0 THEN LEAST(p.ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN p.ret<0 THEN p.ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(p.ret),3) avg_pct
FROM prod p, ceils c
WHERE p.chg_7d IS NOT NULL AND NOT isnan(p.chg_7d) AND p.chg_7d <= c.x
GROUP BY c.x ORDER BY c.x DESC;

-- ===========================================================================
-- Run 25 robustness: independence (3d x 7d) + by-year, added after the slice.
-- Reuses `t` (built above). `base` = production WITHOUT the 3d/7d filters so
-- each can be toggled. (prod above keeps the 3d filter; base drops it.)
-- ===========================================================================
CREATE OR REPLACE TEMP TABLE base AS
SELECT *, YEAR(trade_date) yr FROM t
WHERE chg_1d <= -0.08 AND chg_20m <= -0.03
  AND adv20 >= 500000 AND float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 300e6
  AND chg_7d IS NOT NULL AND NOT isnan(chg_7d) AND NOT isnan(chg_3d);

SELECT '=== (b) 2x2 independence: 3d-band x 7d>=-5% ===' z;
SELECT
  CASE WHEN chg_3d >= -0.03 AND chg_3d <= 0.30 THEN '3d in[-3,+30]' ELSE '3d OUT' END AS d3,
  CASE WHEN chg_7d >= -0.05 THEN '7d >= -5%' ELSE '7d < -5%' END AS d7,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) clip_pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM base GROUP BY 1,2 ORDER BY 1,2;

SELECT '=== (a) by-year OLD(3d only) vs NEW(3d + 7d>=-5%) ===' z;
WITH oldp AS (SELECT yr, COUNT(*) n_old,
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
          / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf_old
  FROM base WHERE chg_3d BETWEEN -0.03 AND 0.30 GROUP BY yr),
newp AS (SELECT yr, COUNT(*) n_new,
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
          / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf_new
  FROM base WHERE chg_3d BETWEEN -0.03 AND 0.30 AND chg_7d >= -0.05 GROUP BY yr)
SELECT o.yr, o.n_old, o.pf_old, n.n_new, n.pf_new
FROM oldp o JOIN newp n USING(yr) ORDER BY o.yr;
