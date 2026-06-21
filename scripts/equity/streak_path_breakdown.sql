-- Path dependency, STREAK version: 2 and 3 consecutive down (or up) days from entry.
-- Entry = close of day 0; days 1-5 held; exit at the open of day 6 (MaxHoldBars=5).
-- A "down day" = that day's close < the prior day's close. Streaks measured from entry:
--   d1 = day1 vs entry ; d2 = day2 vs day1 ; d3 = day3 vs day2.
-- For EACH streak we report BOTH:
--   (a) full-trade PF  — mostly MECHANICAL (the streak return is part of the trade), and
--   (b) FORWARD PF from the end of the streak -> exit — the only ACTIONABLE read (you can
--       only act after observing the streak); strips the mechanical component.
-- PF clip +50%. Input: /tmp/v3_prod_intraday.csv. Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_intraday.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
nx AS (
  SELECT ticker, date AS d0, adj_close AS c0,
    LEAD(adj_close,1) OVER (PARTITION BY ticker ORDER BY date) AS c1,
    LEAD(adj_close,2) OVER (PARTITION BY ticker ORDER BY date) AS c2,
    LEAD(adj_close,3) OVER (PARTITION BY ticker ORDER BY date) AS c3,
    LEAD(adj_close,4) OVER (PARTITION BY ticker ORDER BY date) AS c4   -- day-4 close (for the 4-day streak)
  FROM split_adjusted_prices)
SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price,
  nx.c1, nx.c2, nx.c3, nx.c4,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,
  -- per-day direction
  (nx.c1 < raw.entry_price) AS d1_down, (nx.c1 > raw.entry_price) AS d1_up,
  (nx.c2 < nx.c1)           AS d2_down, (nx.c2 > nx.c1)           AS d2_up,
  (nx.c3 < nx.c2)           AS d3_down, (nx.c3 > nx.c2)           AS d3_up,
  (nx.c4 < nx.c3)           AS d4_down, (nx.c4 > nx.c3)           AS d4_up,
  -- forward returns from the end of each streak length -> exit
  raw.exit_price/NULLIF(nx.c2,0) - 1.0 AS fwd_from_d2,
  raw.exit_price/NULLIF(nx.c3,0) - 1.0 AS fwd_from_d3,
  raw.exit_price/NULLIF(nx.c4,0) - 1.0 AS fwd_from_d4
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN nx ON nx.ticker=raw.symbol AND nx.d0=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND nx.c2 IS NOT NULL;

-- helper: PF clip on a given return column, over rows matching a predicate
.mode box

-- ============ 2-DAY STREAKS ============
SELECT '=== 2-DAY streaks: full-trade PF (mechanical) vs FORWARD PF (from day2 close) ===' z;
SELECT grp, n, full_pf, full_post, fwd_pf, fwd_post, fwd_mean_pct, fwd_median_pct FROM (
  SELECT 'DOWN-DOWN' grp, COUNT(*) n,
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) full_pf,
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) full_post,
    ROUND(SUM(CASE WHEN fwd_from_d2>0 THEN LEAST(fwd_from_d2,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_from_d2<0 THEN fwd_from_d2 ELSE 0 END),0),3) fwd_pf,
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d2>0 THEN LEAST(fwd_from_d2,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d2<0 THEN fwd_from_d2 ELSE 0 END),0),3) fwd_post,
    ROUND(AVG(fwd_from_d2)*100,3) fwd_mean_pct, ROUND(MEDIAN(fwd_from_d2)*100,3) fwd_median_pct
  FROM t WHERE d1_down AND d2_down
  UNION ALL
  SELECT 'UP-UP', COUNT(*),
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN fwd_from_d2>0 THEN LEAST(fwd_from_d2,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_from_d2<0 THEN fwd_from_d2 ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d2>0 THEN LEAST(fwd_from_d2,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d2<0 THEN fwd_from_d2 ELSE 0 END),0),3),
    ROUND(AVG(fwd_from_d2)*100,3), ROUND(MEDIAN(fwd_from_d2)*100,3)
  FROM t WHERE d1_up AND d2_up
  UNION ALL
  SELECT 'all (2d defined)', COUNT(*),
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN fwd_from_d2>0 THEN LEAST(fwd_from_d2,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_from_d2<0 THEN fwd_from_d2 ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d2>0 THEN LEAST(fwd_from_d2,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d2<0 THEN fwd_from_d2 ELSE 0 END),0),3),
    ROUND(AVG(fwd_from_d2)*100,3), ROUND(MEDIAN(fwd_from_d2)*100,3)
  FROM t
) ORDER BY grp;

-- ============ 3-DAY STREAKS ============
SELECT '=== 3-DAY streaks: full-trade PF (mechanical) vs FORWARD PF (from day3 close) ===' z;
SELECT grp, n, full_pf, full_post, fwd_pf, fwd_post, fwd_mean_pct, fwd_median_pct FROM (
  SELECT 'DOWN-DOWN-DOWN' grp, COUNT(*) n,
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) full_pf,
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) full_post,
    ROUND(SUM(CASE WHEN fwd_from_d3>0 THEN LEAST(fwd_from_d3,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_from_d3<0 THEN fwd_from_d3 ELSE 0 END),0),3) fwd_pf,
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d3>0 THEN LEAST(fwd_from_d3,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d3<0 THEN fwd_from_d3 ELSE 0 END),0),3) fwd_post,
    ROUND(AVG(fwd_from_d3)*100,3) fwd_mean_pct, ROUND(MEDIAN(fwd_from_d3)*100,3) fwd_median_pct
  FROM t WHERE d1_down AND d2_down AND d3_down AND c3 IS NOT NULL
  UNION ALL
  SELECT 'UP-UP-UP', COUNT(*),
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN fwd_from_d3>0 THEN LEAST(fwd_from_d3,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_from_d3<0 THEN fwd_from_d3 ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d3>0 THEN LEAST(fwd_from_d3,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d3<0 THEN fwd_from_d3 ELSE 0 END),0),3),
    ROUND(AVG(fwd_from_d3)*100,3), ROUND(MEDIAN(fwd_from_d3)*100,3)
  FROM t WHERE d1_up AND d2_up AND d3_up AND c3 IS NOT NULL
) ORDER BY grp;

-- ============ 4-DAY STREAKS (forward window is only ~1 day; small samples) ============
SELECT '=== 4-DAY streaks: full-trade PF (mechanical) vs FORWARD PF (from day4 close) ===' z;
SELECT grp, n, full_pf, fwd_pf, fwd_post, fwd_mean_pct, fwd_median_pct FROM (
  SELECT 'DOWN x4' grp, COUNT(*) n,
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) full_pf,
    ROUND(SUM(CASE WHEN fwd_from_d4>0 THEN LEAST(fwd_from_d4,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_from_d4<0 THEN fwd_from_d4 ELSE 0 END),0),3) fwd_pf,
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d4>0 THEN LEAST(fwd_from_d4,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d4<0 THEN fwd_from_d4 ELSE 0 END),0),3) fwd_post,
    ROUND(AVG(fwd_from_d4)*100,3) fwd_mean_pct, ROUND(MEDIAN(fwd_from_d4)*100,3) fwd_median_pct
  FROM t WHERE d1_down AND d2_down AND d3_down AND d4_down AND c4 IS NOT NULL
  UNION ALL
  SELECT 'UP x4', COUNT(*),
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN fwd_from_d4>0 THEN LEAST(fwd_from_d4,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN fwd_from_d4<0 THEN fwd_from_d4 ELSE 0 END),0),3),
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d4>0 THEN LEAST(fwd_from_d4,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND fwd_from_d4<0 THEN fwd_from_d4 ELSE 0 END),0),3),
    ROUND(AVG(fwd_from_d4)*100,3), ROUND(MEDIAN(fwd_from_d4)*100,3)
  FROM t WHERE d1_up AND d2_up AND d3_up AND d4_up AND c4 IS NOT NULL
) ORDER BY grp;

-- counts sanity: how common are the streaks
SELECT '=== streak frequency ===' z;
SELECT
  SUM(CASE WHEN d1_down AND d2_down THEN 1 ELSE 0 END) dd,
  SUM(CASE WHEN d1_up AND d2_up THEN 1 ELSE 0 END) uu,
  SUM(CASE WHEN d1_down AND d2_down AND d3_down THEN 1 ELSE 0 END) ddd,
  SUM(CASE WHEN d1_up AND d2_up AND d3_up THEN 1 ELSE 0 END) uuu,
  COUNT(*) total
FROM t;
