-- 2D CUMULATIVE ceiling grid: tightness < T  AND  atr% < A  (joint ceiling).
-- Each cell = PF (clipped +50%) over all trades below BOTH ceilings.
-- Input: the wide dump with both gates opened (~9,090 trips):
--   dotnet run -c Release --project TradingEdge.MomentumV2 -- \
--     --out /tmp/v2_wide_tight_atr.csv --max-tightness 1000 --max-atr-pct 1000
-- Run: duckdb -readonly data/trading.db < scripts/equity/tightness_atr_2d_sweep.sql
--
-- PF on per-trade RETURN clipped at +50% (project standard). Decide on cumulative.
-- Population: move[0.10,0.30) / rvol>=5 / 52w>=0.95 / price>=5 / adv>=100k / 5d-stop,
--   breadth lag1>0.5, era>=2005, closed trips only. Production corner = (T<4.5, A<0.10).
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_wide_tight_atr.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price - 1.0) AS ret
FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';

-- one cumulative cell: tight < T and atr% < A
CREATE OR REPLACE TEMP MACRO cell_pf(T, A) AS (
  SELECT ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
             /NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3)
  FROM t WHERE tightness_14_at_entry<T AND atr_pct_14_at_entry<A);
CREATE OR REPLACE TEMP MACRO cell_n(T, A) AS (
  SELECT COUNT(*) FROM t WHERE tightness_14_at_entry<T AND atr_pct_14_at_entry<A);
CREATE OR REPLACE TEMP MACRO cell_post(T, A) AS (
  SELECT ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
             /NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3)
  FROM t WHERE tightness_14_at_entry<T AND atr_pct_14_at_entry<A);

.mode box
SELECT '=== 2D CUMULATIVE: PF (clip+50%)  rows=tight<T, cols=atr%<A ===' z;
SELECT 'tight<'||T AS tight,
  cell_pf(T,0.05) "a<.05", cell_pf(T,0.06) "a<.06", cell_pf(T,0.07) "a<.07",
  cell_pf(T,0.08) "a<.08", cell_pf(T,0.09) "a<.09", cell_pf(T,0.10) "a<.10",
  cell_pf(T,0.11) "a<.11"
FROM (VALUES (3.0),(3.5),(4.0),(4.5),(5.0),(5.5),(7.0)) v(T) ORDER BY T;

SELECT '=== 2D CUMULATIVE: n (trips) ===' z;
SELECT 'tight<'||T AS tight,
  cell_n(T,0.05) "a<.05", cell_n(T,0.06) "a<.06", cell_n(T,0.07) "a<.07",
  cell_n(T,0.08) "a<.08", cell_n(T,0.09) "a<.09", cell_n(T,0.10) "a<.10",
  cell_n(T,0.11) "a<.11"
FROM (VALUES (3.0),(3.5),(4.0),(4.5),(5.0),(5.5),(7.0)) v(T) ORDER BY T;

SELECT '=== 2D CUMULATIVE: PF post-2015 (clip+50%) ===' z;
SELECT 'tight<'||T AS tight,
  cell_post(T,0.05) "a<.05", cell_post(T,0.06) "a<.06", cell_post(T,0.07) "a<.07",
  cell_post(T,0.08) "a<.08", cell_post(T,0.09) "a<.09", cell_post(T,0.10) "a<.10",
  cell_post(T,0.11) "a<.11"
FROM (VALUES (3.0),(3.5),(4.0),(4.5),(5.0),(5.5),(7.0)) v(T) ORDER BY T;
