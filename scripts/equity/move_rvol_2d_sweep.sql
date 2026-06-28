-- 2D CUMULATIVE grid: entry-day move% FLOOR  ×  rvol FLOOR  (move capped at 30%).
-- Each cell = PF (clipped +50%) over all trades with move >= M AND move < 0.30 AND rvol >= R.
--   move% range [0, 30] (the 30% blow-off cap is always on); rvol range [1, inf).
-- Input: wide dump with the move + rvol gates opened (rest production: tight<4.5, atr%<0.10):
--   dotnet run -c Release --project TradingEdge.HighFlyer -- --out /tmp/v3_wide_move_rvol.csv \
--     --up-threshold 0.0 --max-up-threshold 1000 --rvol-min 0 --rvol-max 100000
-- Run: duckdb -readonly data/trading.db < scripts/equity/move_rvol_2d_sweep.sql
--
-- PF on per-trade RETURN clipped at +50% (project standard). Decide on cumulative.
-- Population: 52w>=0.95 / price>=5 / adv>=100k / tight<4.5 / atr%<0.10 / 5d-stop,
--   breadth lag1>0.5, era>=2005, closed trips only. Production corner = (move>=0.10, rvol>=5).
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_wide_move_rvol.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.pct_up_at_entry, raw.rvol_at_entry, raw.entry_date,
       (raw.exit_price/raw.entry_price - 1.0) AS ret
FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.pct_up_at_entry < 0.30;        -- 30% blow-off cap always on

CREATE OR REPLACE TEMP MACRO cell_pf(M, R) AS (
  SELECT ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
             /NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3)
  FROM t WHERE pct_up_at_entry>=M AND rvol_at_entry>=R);
CREATE OR REPLACE TEMP MACRO cell_n(M, R) AS (
  SELECT COUNT(*) FROM t WHERE pct_up_at_entry>=M AND rvol_at_entry>=R);
CREATE OR REPLACE TEMP MACRO cell_post(M, R) AS (
  SELECT ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
             /NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3)
  FROM t WHERE pct_up_at_entry>=M AND rvol_at_entry>=R);

.mode box
SELECT '=== 2D CUMULATIVE PF (clip+50%): rows=move>=M (move<30%), cols=rvol>=R ===' z;
SELECT 'move>='||CAST(ROUND(M*100) AS INT)||'%' AS move,
  cell_pf(M,1) "rv>=1", cell_pf(M,3) "rv>=3", cell_pf(M,5) "rv>=5",
  cell_pf(M,7) "rv>=7", cell_pf(M,10) "rv>=10", cell_pf(M,15) "rv>=15"
FROM (VALUES (0.0),(0.05),(0.10),(0.15),(0.20),(0.25)) v(M) ORDER BY M;

SELECT '=== 2D CUMULATIVE n (trips) ===' z;
SELECT 'move>='||CAST(ROUND(M*100) AS INT)||'%' AS move,
  cell_n(M,1) "rv>=1", cell_n(M,3) "rv>=3", cell_n(M,5) "rv>=5",
  cell_n(M,7) "rv>=7", cell_n(M,10) "rv>=10", cell_n(M,15) "rv>=15"
FROM (VALUES (0.0),(0.05),(0.10),(0.15),(0.20),(0.25)) v(M) ORDER BY M;

SELECT '=== 2D CUMULATIVE PF post-2015 (clip+50%) ===' z;
SELECT 'move>='||CAST(ROUND(M*100) AS INT)||'%' AS move,
  cell_post(M,1) "rv>=1", cell_post(M,3) "rv>=3", cell_post(M,5) "rv>=5",
  cell_post(M,7) "rv>=7", cell_post(M,10) "rv>=10", cell_post(M,15) "rv>=15"
FROM (VALUES (0.0),(0.05),(0.10),(0.15),(0.20),(0.25)) v(M) ORDER BY M;
