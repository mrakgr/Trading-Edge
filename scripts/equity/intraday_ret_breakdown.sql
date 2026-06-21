-- Full breakdown of the entry-day INTRADAY return (close/open - 1) vs trade PF.
-- The deep-fade floor (>= -0.07) is now a production gate; this maps the WHOLE range
-- to see what other patterns the intraday push carries. PF clip +50%.
-- Input: /tmp/v3_prod_px1.csv (the pre-intraday-gate dump, so the full range is visible).
-- Run: duckdb -readonly data/trading.db < scripts/equity/intraday_ret_breakdown.sql
-- Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_px1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.entry_date, raw.pct_up_at_entry, raw.rvol_at_entry,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,
  s.adj_close / NULLIF(s.adj_open,0) - 1.0 AS intraday_ret,
  s.adj_open / NULLIF(LAG(s.adj_close) OVER (PARTITION BY s.ticker ORDER BY s.date),0) - 1.0 AS gap_pct
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN split_adjusted_prices s ON s.ticker=raw.symbol AND s.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND s.adj_open > 0;

CREATE OR REPLACE TEMP MACRO m() AS TABLE
SELECT COUNT(*) n,
  ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t;

.mode box
-- 1) FULL non-cumulative bands across the whole intraday-return range
SELECT '=== intraday_ret bands (full range), clip +50% ===' z;
SELECT CASE WHEN intraday_ret< -0.07 THEN '0: < -7% (CUT by gate)'
            WHEN intraday_ret< 0.0   THEN '1: -7..0% (mild red, kept)'
            WHEN intraday_ret< 0.02  THEN '2: 0-2%'
            WHEN intraday_ret< 0.05  THEN '3: 2-5%'
            WHEN intraday_ret< 0.10  THEN '4: 5-10%'
            WHEN intraday_ret< 0.15  THEN '5: 10-15%'
            WHEN intraday_ret< 0.25  THEN '6: 15-25%'
            ELSE                         '7: 25%+' END band,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- 2) QUINTILES of intraday_ret (data-driven bands)
SELECT '=== intraday_ret QUINTILES (data-driven) ===' z;
WITH q AS (SELECT *, NTILE(5) OVER (ORDER BY intraday_ret) AS quintile FROM t)
SELECT quintile,
  ROUND(MIN(intraday_ret),3) lo, ROUND(MAX(intraday_ret),3) hi, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM q GROUP BY quintile ORDER BY quintile;

-- 3) Does intraday_ret just proxy the day MOVE (pct_up)? Compare the two.
SELECT '=== intraday_ret vs day-move (pct_up) correlation + how independent ===' z;
SELECT ROUND(CORR(intraday_ret, pct_up_at_entry),3) corr_intraday_move,
       ROUND(CORR(intraday_ret, gap_pct),3)         corr_intraday_gap
FROM t;

-- 4) intraday_ret x gap: is a green intraday push better when it FOLLOWS a gap or not?
SELECT '=== intraday push (>=0) split by overnight GAP size ===' z;
SELECT CASE WHEN gap_pct<0.02 THEN '1: gap <2% (no gap)'
            WHEN gap_pct<0.05 THEN '2: gap 2-5%'
            WHEN gap_pct<0.10 THEN '3: gap 5-10%'
            ELSE                   '4: gap 10%+' END gap_band,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE intraday_ret>=0 GROUP BY 1 ORDER BY 1;

SELECT '=== baseline (all, pre-gate) ===' z;
SELECT * FROM m();
