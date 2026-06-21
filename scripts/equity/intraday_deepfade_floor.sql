-- Deep-fade-only floor: keep close/open-1 >= N for SHALLOW negative N (cut ONLY the
-- deep intraday fades, keep the mild-red band). The earlier floor sweep stepped up
-- from the bottom and showed the OPTIMUM was N=0; this zooms into the shallow-negative
-- region to check whether cutting ONLY deep fades (-0.20/-0.15/-0.10) is a better
-- capacity/PF trade than the full no-red rule.
-- Input: /tmp/v3_prod_px1.csv. Clip +50%, breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_px1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.entry_date, (raw.exit_price/raw.entry_price - 1.0) AS ret,
  s.adj_close / NULLIF(s.adj_open,0) - 1.0 AS intraday_ret
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN split_adjusted_prices s ON s.ticker=raw.symbol AND s.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND s.adj_open > 0;

-- KEEP intraday_ret >= N  (so N=-0.20 keeps everything except fades worse than -20%)
CREATE OR REPLACE TEMP MACRO keep(n) AS TABLE
SELECT COUNT(*) n_trips,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE intraday_ret >= n;

-- the tail being CUT at each floor (intraday_ret < N): is it actually bad?
CREATE OR REPLACE TEMP MACRO cut(n) AS TABLE
SELECT COUNT(*) n_cut,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip_cut,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post_cut
FROM t WHERE intraday_ret < n;

.mode box
SELECT '=== KEEP close/open-1 >= N (deep-fade-only cuts), clip +50% ===' z;
SELECT 'all (-inf)' g,* FROM keep(-1e9);
SELECT 'N=-0.25'  g,* FROM keep(-0.25);
SELECT 'N=-0.20'  g,* FROM keep(-0.20);
SELECT 'N=-0.15'  g,* FROM keep(-0.15);
SELECT 'N=-0.12'  g,* FROM keep(-0.12);
SELECT 'N=-0.10'  g,* FROM keep(-0.10);
SELECT 'N=-0.07'  g,* FROM keep(-0.07);
SELECT 'N=-0.05'  g,* FROM keep(-0.05);
SELECT 'N= 0.00'  g,* FROM keep(0.0);

SELECT '=== the tail CUT at each floor (intraday_ret < N) — is it bad enough to drop? ===' z;
SELECT 'cut <-0.20' g,* FROM cut(-0.20);
SELECT 'cut <-0.15' g,* FROM cut(-0.15);
SELECT 'cut <-0.10' g,* FROM cut(-0.10);
SELECT 'cut <-0.05' g,* FROM cut(-0.05);
SELECT 'cut < 0.00' g,* FROM cut(0.0);

-- non-cumulative slices to see the gradient inside the negative region
SELECT '=== non-cumulative intraday_ret bands (negative region) ===' z;
SELECT CASE WHEN intraday_ret < -0.20 THEN '1: < -20%'
            WHEN intraday_ret < -0.15 THEN '2: -20..-15%'
            WHEN intraday_ret < -0.10 THEN '3: -15..-10%'
            WHEN intraday_ret < -0.05 THEN '4: -10..-5%'
            WHEN intraday_ret < 0.0   THEN '5: -5..0%'
            ELSE                          '6: >= 0 (green)' END band,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;
