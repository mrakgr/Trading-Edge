-- Within the DEAD ZONE (d52 >= 3% above the 52w max CLOSE), is there an rvol level
-- above which the trades turn positive? The O'Neil "buy the new high" crowd lives
-- mostly at rvol 1-3 here; sweep the full rvol axis (CSV regenerated at rvol>=1).
--
-- Population: [5,10]% move, rvol>=1, full production (ATR%<0.10, tight<4.5, price>=1,
--   52w>=0.95, -0.07 intraday gate, 5d stop) + breadth lag1>0.5 + heat h10<0.25.
-- Input: /tmp/v3_510_rvol1.csv  (regen: dotnet run -c Release --project
--   TradingEdge.MomentumV2 -- --up-threshold 0.05 --rvol-min 1 --out /tmp/v3_510_rvol1.csv)
-- d52 = pct_52w_at_entry. PF clip +50%. >=2005, closed.
-- Run: duckdb -readonly data/trading.db < scripts/equity/deadzone_rvol_sweep.sql

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
  AND raw.pct_up_at_entry>=0.05 AND raw.pct_up_at_entry<0.10;   -- the [5,10]% band; rvol unrestricted

.mode box
SELECT '=== dead zone d52>=3%: baseline (all rvol>=1) ===' z;
SELECT COUNT(*) n, ROUND(AVG(rvol),2) mean_rvol,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE d52>=0.03;

-- 1) rvol BANDS within the dead zone (diagnostic)
SELECT '=== dead zone d52>=3%: rvol BANDS ===' z;
SELECT CASE WHEN rvol<1.5 THEN '1: 1.0-1.5' WHEN rvol<2 THEN '2: 1.5-2' WHEN rvol<3 THEN '3: 2-3'
            WHEN rvol<5 THEN '4: 3-5' WHEN rvol<8 THEN '5: 5-8' WHEN rvol<15 THEN '6: 8-15'
            ELSE '7: 15+' END rvol_band,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE d52>=0.03 GROUP BY 1 ORDER BY 1;

-- 2) CUMULATIVE FLOOR: keep rvol >= N (decision view — where does it turn?)
SELECT '=== dead zone d52>=3%: cumulative rvol FLOOR (keep rvol >= N) ===' z;
CREATE OR REPLACE TEMP MACRO fl(n) AS TABLE
SELECT COUNT(*) nn,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE d52>=0.03 AND rvol >= n;
SELECT 'rvol>=1 (all)' g,* FROM fl(1.0);
SELECT 'rvol>=2' g,* FROM fl(2.0);
SELECT 'rvol>=3' g,* FROM fl(3.0);
SELECT 'rvol>=5' g,* FROM fl(5.0);
SELECT 'rvol>=8' g,* FROM fl(8.0);
SELECT 'rvol>=10' g,* FROM fl(10.0);
SELECT 'rvol>=15' g,* FROM fl(15.0);

-- 3) contrast: the SAME rvol floor sweep in the BREAKOUT zone (d52<1%) — to show the
--    dead zone needs MORE rvol than the fresh-high zone to reach the same PF.
SELECT '=== contrast: breakout zone d52<1% — cumulative rvol FLOOR ===' z;
CREATE OR REPLACE TEMP MACRO flb(n) AS TABLE
SELECT COUNT(*) nn,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t WHERE d52<0.01 AND rvol >= n;
SELECT 'rvol>=1 (all)' g,* FROM flb(1.0);
SELECT 'rvol>=2' g,* FROM flb(2.0);
SELECT 'rvol>=3' g,* FROM flb(3.0);
SELECT 'rvol>=5' g,* FROM flb(5.0);
SELECT 'rvol>=8' g,* FROM flb(8.0);
