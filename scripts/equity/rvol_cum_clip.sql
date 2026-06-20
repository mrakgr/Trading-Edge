CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_rvol_sub1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.*, (raw.exit_price/raw.entry_price - 1.0) AS ret FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.pct_up_at_entry>=0.10 AND raw.rvol_at_entry<15;

-- PF computed on CLIPPED per-trade return (cap the upside at `cap`); loss side untouched
CREATE OR REPLACE TEMP MACRO floor(f, cap) AS TABLE
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,cap) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' THEN (CASE WHEN ret>0 THEN LEAST(ret,cap) ELSE 0 END) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_clip_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' THEN (CASE WHEN ret>0 THEN LEAST(ret,cap) ELSE 0 END) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_clip_post
FROM t WHERE rvol_at_entry >= f;

.mode box
SELECT '=== cumulative rvol floor, return clipped at +50% ===' z;
SELECT 'rvol>='||f g, floor.* FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(12)) v(f), LATERAL floor(v.f, 0.50);
SELECT 'rvol>=1'  g, * FROM floor(1, 0.50);
SELECT 'rvol>=2'  g, * FROM floor(2, 0.50);
SELECT 'rvol>=3'  g, * FROM floor(3, 0.50);
SELECT 'rvol>=4'  g, * FROM floor(4, 0.50);
SELECT 'rvol>=5'  g, * FROM floor(5, 0.50);
SELECT 'rvol>=6'  g, * FROM floor(6, 0.50);
SELECT 'rvol>=7'  g, * FROM floor(7, 0.50);
SELECT 'rvol>=8'  g, * FROM floor(8, 0.50);
SELECT 'rvol>=9'  g, * FROM floor(9, 0.50);
SELECT 'rvol>=10' g, * FROM floor(10, 0.50);
SELECT 'rvol>=12' g, * FROM floor(12, 0.50);
