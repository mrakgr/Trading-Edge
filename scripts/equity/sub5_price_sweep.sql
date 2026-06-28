-- Sub-$5 price-floor analysis under the +50% winner-clip. Was the $5 floor a
-- raw-PF mirage? The $5 floor was set when a few lottery winners gave the sub-$5
-- bucket a gaudy raw PF; the clip should give an honest read.
-- Input: wide dump with the PRICE floor dropped (rest production: rvol>=5,
--   move[0.10,0.30), tight<4.5, atr%<0.10, adv>=100k):
--   dotnet run -c Release --project TradingEdge.HighFlyer -- --out /tmp/v3_wide_nopx.csv --min-price 0
-- Run: duckdb -readonly data/trading.db < scripts/equity/sub5_price_sweep.sql
-- PF on per-trade RETURN clipped at +50% (project standard). Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_wide_nopx.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.entry_price, raw.entry_date, (raw.exit_price/raw.entry_price - 1.0) AS ret
FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';

-- non-cumulative price bands (diagnostic) + cumulative floors (decisional)
.mode box
SELECT '=== PRICE BANDS (non-cumulative), clip +50% ===' z;
SELECT CASE WHEN entry_price<1 THEN '1: <$1'
            WHEN entry_price<2 THEN '2: $1-2'
            WHEN entry_price<3 THEN '3: $2-3'
            WHEN entry_price<5 THEN '4: $3-5'
            WHEN entry_price<10 THEN '5: $5-10'
            WHEN entry_price<20 THEN '6: $10-20'
            ELSE '7: $20+' END band,
  COUNT(*) n,
  ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- the specific question: the WHOLE sub-$5 region vs the >=$5 production region
SELECT '=== sub-$5 vs >=$5 (clip +50%) ===' z;
CREATE OR REPLACE TEMP MACRO region(lo, hi) AS TABLE
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE entry_price>=lo AND entry_price<hi;
SELECT 'sub-$5  [0,5)' g, * FROM region(0,5);
SELECT '>=$5    [5,inf)' g, * FROM region(5,1e9);
SELECT '$2-5   [2,5)' g, * FROM region(2,5);
SELECT '$1-5   [1,5)' g, * FROM region(1,5);

-- cumulative FLOOR sweep: does PF improve as we raise the price floor?
SELECT '=== cumulative price FLOOR (price >= f), clip +50% ===' z;
CREATE OR REPLACE TEMP MACRO pfloor(f) AS TABLE
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE entry_price>=f;
SELECT 'price>=$0' g,* FROM pfloor(0);
SELECT 'price>=$1' g,* FROM pfloor(1);
SELECT 'price>=$2' g,* FROM pfloor(2);
SELECT 'price>=$3' g,* FROM pfloor(3);
SELECT 'price>=$5' g,* FROM pfloor(5);
SELECT 'price>=$10' g,* FROM pfloor(10);
