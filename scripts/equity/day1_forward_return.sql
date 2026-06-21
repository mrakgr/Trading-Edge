-- Is the day-1 split MECHANICAL or PREDICTIVE? The full-trade-return split (day1 up
-- PF 5.7 vs down 0.46) is mostly mechanical: day-1 return is a COMPONENT of the 5-day
-- trade return. The actionable question: does day-1 weakness predict the REST of the
-- trade (day1 close -> exit) going badly? If so, an early exit on day-1 weakness helps.
-- Forward return = exit_price / day1_close - 1  (strips the mechanical day-1 component).
-- Input: /tmp/v3_prod_intraday.csv. PF clip +50%. Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_intraday.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
nx AS (SELECT ticker, date AS d0, adj_close AS c0,
         LEAD(adj_close) OVER (PARTITION BY ticker ORDER BY date) AS c1
       FROM split_adjusted_prices)
SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price, nx.c1 AS day1_close,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,          -- full trade
  nx.c1/NULLIF(raw.entry_price,0) - 1.0   AS day1_ret,     -- entry -> day1 close (mechanical component)
  raw.exit_price/NULLIF(nx.c1,0) - 1.0    AS fwd_ret       -- day1 close -> exit (the FORWARD path)
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN nx ON nx.ticker=raw.symbol AND nx.d0=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND nx.c1 IS NOT NULL;

.mode box
-- FORWARD return (day1 close -> exit), conditioned on day-1 direction.
-- This is what you'd capture if you DECIDED on day-1 close whether to keep holding.
SELECT '=== FORWARD return (day1 close -> exit) by day-1 direction — PF clip ===' z;
SELECT CASE WHEN day1_ret>0 THEN 'A: day1 UP' WHEN day1_ret<0 THEN 'B: day1 DOWN' ELSE 'C: flat' END grp,
  COUNT(*) n,
  ROUND(AVG(LEAST(fwd_ret,0.50)),4) mean_fwd_clip,
  ROUND(SUM(CASE WHEN fwd_ret>0 THEN LEAST(fwd_ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_ret<0 THEN fwd_ret ELSE 0 END),0),3) pf_fwd_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_ret>0 THEN LEAST(fwd_ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_ret<0 THEN fwd_ret ELSE 0 END),0),3) pf_fwd_post
FROM t GROUP BY 1 ORDER BY 1;

-- graded: forward PF by day-1 return band (does deeper day-1 weakness predict worse forward?)
SELECT '=== FORWARD PF by day-1 return band ===' z;
SELECT CASE WHEN day1_ret< -0.10 THEN '1: day1 < -10%'
            WHEN day1_ret< -0.05 THEN '2: -10..-5%'
            WHEN day1_ret<  0.00 THEN '3: -5..0%'
            WHEN day1_ret<  0.05 THEN '4: 0..5%'
            WHEN day1_ret<  0.10 THEN '5: 5..10%'
            ELSE                     '6: 10%+' END band,
  COUNT(*) n, ROUND(AVG(LEAST(fwd_ret,0.50)),4) mean_fwd,
  ROUND(SUM(CASE WHEN fwd_ret>0 THEN LEAST(fwd_ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_ret<0 THEN fwd_ret ELSE 0 END),0),3) pf_fwd_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_ret>0 THEN LEAST(fwd_ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_ret<0 THEN fwd_ret ELSE 0 END),0),3) pf_fwd_post
FROM t GROUP BY 1 ORDER BY 1;

-- mean forward return: is day1-DOWN forward EV actually negative (=> exit) or just lower (=> hold)?
SELECT '=== mean FORWARD return (unclipped) — is day1-down EV negative? ===' z;
SELECT CASE WHEN day1_ret>0 THEN 'day1 UP' WHEN day1_ret<0 THEN 'day1 DOWN' ELSE 'flat' END grp,
  COUNT(*) n, ROUND(AVG(fwd_ret),4) mean_fwd_raw, ROUND(MEDIAN(fwd_ret),4) median_fwd
FROM t GROUP BY 1 ORDER BY 1;
