-- Do trailing-6-month MAX ATR% and 6-month SLOPE discriminate the dead zone, where
-- reclaim / intraday-return / pre-entry-streak all failed?
--
-- max_atr6mo = max over the trailing 126 trading days of the 14-bar log-ATR
--   (mean of ln(high/low) over 14 bars). A high value = the name had a violent,
--   high-volatility episode in the last 6 months (a prior pump/crash).
-- slope6mo  = OLS slope of ln(adj_close) vs row-index over the trailing 126 bars
--   (per-trading-day log drift). High = strong 6mo uptrend; <=0 = flat/down base.
--
-- Both are measured AS OF the entry bar (inclusive), so no lookahead.
-- Quintiles (NTILE 5) are cut WITHIN the trip population so they're balanced.
--
-- Population: [5,10]% move, rvol>3, FULL production + breadth lag1>0.5 + heat h10<0.25.
-- Input: /tmp/v3_510_rvol3.csv. d52 = pct_52w_at_entry. PF clip +50%. >=2005, closed.
-- Run: duckdb -readonly data/trading.db < scripts/equity/sixmo_atr_slope_deadzone.sql

-- 1) entry (ticker,date) set, so we only compute rolling measures where needed
CREATE OR REPLACE TEMP TABLE entries AS
SELECT DISTINCT symbol AS ticker, entry_date FROM read_csv_auto('/tmp/v3_510_rvol3.csv') WHERE open=0;

-- 2) rolling 6mo measures over the price history of just those tickers
CREATE OR REPLACE TEMP TABLE meas AS
WITH base AS (
  SELECT p.ticker, p.date,
    ROW_NUMBER() OVER (PARTITION BY p.ticker ORDER BY p.date) rn,
    ln(GREATEST(p.adj_high,1e-9)/GREATEST(p.adj_low,1e-9)) AS log_tr,
    ln(GREATEST(p.adj_close,1e-9)) AS lc
  FROM split_adjusted_prices p
  WHERE p.ticker IN (SELECT DISTINCT ticker FROM entries)
),
atr AS (
  SELECT *, AVG(log_tr) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS atr14
  FROM base
)
SELECT ticker, date,
  MAX(atr14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS max_atr6mo,
  REGR_SLOPE(lc, rn) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS slope6mo,
  COUNT(*) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS nbars
FROM atr;

-- 3) join to trips + gates, keep only entries with a full ~6mo window
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol3.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT raw.symbol, raw.entry_date, raw.pct_52w_at_entry AS d52,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,
  m.max_atr6mo, m.slope6mo
FROM raw
JOIN br ON br.date=raw.entry_date
LEFT JOIN hn ON hn.date=raw.entry_date
JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND (hn.h10 IS NULL OR hn.h10 < 0.25)
  AND raw.pct_up_at_entry>=0.05 AND raw.pct_up_at_entry<0.10 AND raw.rvol_at_entry>=3
  AND m.nbars>=120;          -- require a near-full 6mo window

-- assign quintiles within the population
CREATE OR REPLACE TEMP TABLE tq AS
SELECT *, NTILE(5) OVER (ORDER BY max_atr6mo) AS atr_q,
          NTILE(5) OVER (ORDER BY slope6mo)  AS slp_q
FROM t;

.mode box
SELECT '=== baseline (with full-6mo-window requirement) ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM tq;

-- A) PF by max-ATR% 6mo quintile (overall, then dead-zone only)
SELECT '=== max-ATR% 6mo quintile — OVERALL ===' z;
SELECT atr_q, COUNT(*) n, ROUND(MIN(max_atr6mo),4) lo, ROUND(MAX(max_atr6mo),4) hi,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM tq GROUP BY 1 ORDER BY 1;

SELECT '=== max-ATR% 6mo quintile — DEAD ZONE (d52>=0.03) only ===' z;
SELECT atr_q, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM tq WHERE d52>=0.03 GROUP BY 1 ORDER BY 1;

-- B) PF by slope 6mo quintile (overall, then dead-zone only)
SELECT '=== slope 6mo quintile — OVERALL ===' z;
SELECT slp_q, COUNT(*) n, ROUND(MIN(slope6mo),5) lo, ROUND(MAX(slope6mo),5) hi,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM tq GROUP BY 1 ORDER BY 1;

SELECT '=== slope 6mo quintile — DEAD ZONE (d52>=0.03) only ===' z;
SELECT slp_q, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM tq WHERE d52>=0.03 GROUP BY 1 ORDER BY 1;
