-- Does the dead-zone extension penalty hold for the STRONG [10,30]% breakouts, or
-- is it a weak-breakout phenomenon? Mirror of dist_52w_close_510_rvol3.sql on the
-- [10,30)% move band. d52 = pct_52w_at_entry (close vs 52w max close).
--
-- Population: [10,30]% move, full production (ATR%<0.10, tight<4.5, price>=1,
--   52w>=0.95, -0.07 intraday gate, 5d stop) + breadth lag1>0.5 + heat h10<0.25.
-- Shown at rvol>=1 (full axis) AND rvol>=5 (the production strong-tier floor).
-- Input: /tmp/v3_510_rvol1.csv (rvol>=1; contains the [10,30] band). PF clip +50%.
-- Run: duckdb -readonly data/trading.db < scripts/equity/dist_52w_close_1030.sql

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT raw.symbol, raw.entry_date, raw.pct_52w_at_entry AS d52, raw.rvol_at_entry AS rvol,
  (raw.exit_price/raw.entry_price - 1.0) AS ret
FROM raw
JOIN br ON br.date=raw.entry_date
LEFT JOIN hn ON hn.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND (hn.h10 IS NULL OR hn.h10 < 0.25)
  AND raw.pct_up_at_entry>=0.10 AND raw.pct_up_at_entry<0.30;   -- STRONG band

.mode box
-- distance bands at rvol>=5 (production strong-tier floor)
SELECT '=== [10,30]% rvol>=5: distance-from-max-close BANDS ===' z;
SELECT CASE WHEN d52< -0.03 THEN '1: < -3%' WHEN d52< -0.01 THEN '2: -3..-1%'
            WHEN d52<  0.00 THEN '3: -1..0%' WHEN d52<  0.01 THEN '4: 0..1% (fresh high)'
            WHEN d52<  0.03 THEN '5: 1..3%' WHEN d52<  0.05 THEN '6: 3..5%'
            ELSE                 '7: 5%+ (extended)' END band,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE rvol>=5 GROUP BY 1 ORDER BY 1;

-- cumulative ceiling at rvol>=5
SELECT '=== [10,30]% rvol>=5: cumulative CEILING (keep d52 < N) ===' z;
CREATE OR REPLACE TEMP MACRO cl5(n) AS TABLE
SELECT COUNT(*) nn,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE rvol>=5 AND d52 < n;
SELECT 'd52<0.01' g,* FROM cl5(0.01);
SELECT 'd52<0.03' g,* FROM cl5(0.03);
SELECT 'd52<0.05' g,* FROM cl5(0.05);
SELECT 'd52<0.10 (all)' g,* FROM cl5(0.10);

-- the dead zone (d52>=3%) compared head-to-head: strong vs weak band, at rvol>=5
SELECT '=== dead zone d52>=3%: STRONG [10,30] vs production rvol floors ===' z;
SELECT 'rvol>=5' g, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE d52>=0.03 AND rvol>=5;

-- rvol sweep within the STRONG-band dead zone (does it ALSO need rvol>=8-10, or turn sooner?)
SELECT '=== STRONG [10,30] dead zone d52>=3%: cumulative rvol FLOOR ===' z;
CREATE OR REPLACE TEMP MACRO fls(n) AS TABLE
SELECT COUNT(*) nn,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE d52>=0.03 AND rvol >= n;
SELECT 'rvol>=1 (all)' g,* FROM fls(1.0);
SELECT 'rvol>=3' g,* FROM fls(3.0);
SELECT 'rvol>=5' g,* FROM fls(5.0);
SELECT 'rvol>=8' g,* FROM fls(8.0);
SELECT 'rvol>=10' g,* FROM fls(10.0);

-- fresh-high contrast for the strong band (does the spike-at-fresh-high pattern hold?)
SELECT '=== STRONG [10,30] rvol>=5: fresh high d52<1% vs dead zone d52>=3% ===' z;
SELECT CASE WHEN d52<0.01 THEN 'fresh high d52<1%' WHEN d52>=0.03 THEN 'dead zone d52>=3%' ELSE 'mid 1-3%' END zone,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE rvol>=5 GROUP BY 1 ORDER BY 1;

-- ============================================================================
-- 6mo-max-ATR% breakdown on the STRONG band — is the calm-base penalty UNIVERSAL
-- (holds where the dead zone is alive), unlike extension which was weak-band-only?
-- max_atr6mo = rolling-126d max of the 14-bar log-ATR, measured as-of entry.
-- Answer: YES — calmest-6mo quintile is a net LOSER on the strong band too
-- (PF ~0.99 / post-2015 0.80), and it survives the extension control.
-- ============================================================================
CREATE OR REPLACE TEMP TABLE meas AS
WITH base AS (
  SELECT p.ticker, p.date, ROW_NUMBER() OVER (PARTITION BY p.ticker ORDER BY p.date) rn,
    ln(GREATEST(p.adj_high,1e-9)/GREATEST(p.adj_low,1e-9)) AS log_tr
  FROM split_adjusted_prices p
  WHERE p.ticker IN (SELECT DISTINCT symbol FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0)
),
atr AS (SELECT *, AVG(log_tr) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS atr14 FROM base)
SELECT ticker, date, MAX(atr14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS max_atr6mo,
  COUNT(*) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS nbars FROM atr;

CREATE OR REPLACE TEMP TABLE tatr AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT raw.pct_52w_at_entry AS d52, raw.rvol_at_entry AS rvol,
  (raw.exit_price/raw.entry_price-1.0) AS ret, raw.entry_date, m.max_atr6mo
FROM raw JOIN br ON br.date=raw.entry_date LEFT JOIN hn ON hn.date=raw.entry_date
JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND (hn.h10 IS NULL OR hn.h10<0.25)
  AND raw.pct_up_at_entry>=0.10 AND raw.pct_up_at_entry<0.30 AND m.nbars>=120;

SELECT '=== STRONG [10,30] 6mo-max-ATR% quintile: OVERALL (rvol>=5) then DEAD ZONE ===' z;
WITH d AS (SELECT *, NTILE(5) OVER (ORDER BY max_atr6mo) aq FROM tatr WHERE rvol>=5)
SELECT 'overall' scp, aq, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM d GROUP BY 1,2 ORDER BY 2;
WITH d AS (SELECT *, NTILE(5) OVER (ORDER BY max_atr6mo) aq FROM tatr WHERE rvol>=5 AND d52>=0.03)
SELECT 'dead zone' scp, aq, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM d GROUP BY 1,2 ORDER BY 2;
