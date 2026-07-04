-- LowFlyer SHORT (fade the new-session-HIGH pop) — BREADTH breakdown.
--
-- Question: does high market breadth improve the short like it does the long MR system?
-- Thesis (user): high breadth = bullish market = more risk-seeking participants = more
-- and higher-quality parabolic blow-offs to fade. Expect high-breadth to help.
--
-- Breadth = pct_above_20 (fraction of the CS/ADRC liquid universe above its 20d MA).
-- NO-LOOKAHEAD: an entry on day t sees breadth through t-1, so we LAG(pct_above_20) OVER
-- (ORDER BY date) — identical convention to the long production gate (breadth>=0.65) and
-- the momentum book.
--
-- Run on the canonical tiers: rvol>=40 (S, PF 4.37 standalone) and rvol>=12 (A+, higher
-- capacity). ATR%>=0.03 base, NO 1d floor (per the standalone S spec / user's request).
-- ret = -ret_moc (short sign flip). METRIC = RAW PF (shorts +100%-bounded).
-- Run: duckdb -readonly data/trading.db < scripts/equity/lowflyer_short_breadth.sql

CREATE OR REPLACE TEMP TABLE br AS
SELECT date, LAG(pct_above_20) OVER (ORDER BY date) AS b_lag1
FROM 'data/equity/momentum_v0/breadth.parquet';

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.trade_date, YEAR(r.trade_date) yr,
       -r.ret_moc AS ret,
       r.rvol, r.bar_rvol_15m AS brv, r.intraday_atr_pct_at_entry AS iatr,
       r.chg_1d, r.chg_20m,
       b.b_lag1 AS breadth
FROM read_csv_auto('/tmp/lowflyer_short_ungated.csv') r
LEFT JOIN br b ON b.date = r.trade_date
WHERE r.intraday_atr_pct_at_entry >= 0.03;   -- ATR floor (S-spec base)

.mode box

SELECT '=== breadth coverage (should be ~100% within data range) ===' z;
SELECT COUNT(*) n, COUNT(breadth) n_breadth,
  ROUND(100.0*COUNT(breadth)/COUNT(*),1) pct_cov
FROM t WHERE rvol >= 12;

-- =========================================================================
-- (A) rvol>=40 (S bucket) — breadth breakdown
-- =========================================================================
SELECT '=== (A) rvol>=40 S bucket: breadth breakdown ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.35,'0.00-0.35 (bearish)'),
  (0.35,0.50,'0.35-0.50'),
  (0.50,0.65,'0.50-0.65'),
  (0.65,0.80,'0.65-0.80'),
  (0.80,1.01,'0.80-1.00 (bullish)'))
SELECT b.lbl AS breadth_bucket,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.rvol>=40 AND t.breadth>=b.lo AND t.breadth<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- floor sweep (rvol>=40)
SELECT '=== (A2) rvol>=40: breadth FLOOR sweep ===' z;
WITH f(x) AS (VALUES (-1.0),(0.35),(0.50),(0.65),(0.80))
SELECT CASE WHEN f.x<0 THEN 'any' ELSE printf('breadth >= %.2f', f.x) END AS floor,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, f WHERE t.rvol>=40 AND (f.x<0 OR t.breadth>=f.x)
GROUP BY f.x ORDER BY f.x;

-- =========================================================================
-- (B) rvol>=12 (A+ bucket) — breadth breakdown (higher capacity)
-- =========================================================================
SELECT '=== (B) rvol>=12 A+ bucket: breadth breakdown ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.35,'0.00-0.35 (bearish)'),
  (0.35,0.50,'0.35-0.50'),
  (0.50,0.65,'0.50-0.65'),
  (0.65,0.80,'0.65-0.80'),
  (0.80,1.01,'0.80-1.00 (bullish)'))
SELECT b.lbl AS breadth_bucket,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.rvol>=12 AND t.breadth>=b.lo AND t.breadth<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== (B2) rvol>=12: breadth FLOOR sweep ===' z;
WITH f(x) AS (VALUES (-1.0),(0.35),(0.50),(0.65),(0.80))
SELECT CASE WHEN f.x<0 THEN 'any' ELSE printf('breadth >= %.2f', f.x) END AS floor,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, f WHERE t.rvol>=12 AND (f.x<0 OR t.breadth>=f.x)
GROUP BY f.x ORDER BY f.x;

-- =========================================================================
-- (C) whole rvol>=4 book — breadth breakdown (does it help the weak tier?)
-- =========================================================================
SELECT '=== (C) rvol>=4 whole book: breadth breakdown ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.35,'0.00-0.35 (bearish)'),
  (0.35,0.50,'0.35-0.50'),
  (0.50,0.65,'0.50-0.65'),
  (0.65,0.80,'0.65-0.80'),
  (0.80,1.01,'0.80-1.00 (bullish)'))
SELECT b.lbl AS breadth_bucket,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.ret>0 THEN t.ret ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN t.ret<0 THEN t.ret ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.rvol>=4 AND t.breadth>=b.lo AND t.breadth<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;
