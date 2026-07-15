-- Re-confirm the HEAT gate after switching the heat universe to the CS/ADRC inner
-- join (was: whole liquid tape + hardcoded test-ticker blocklist). The two heat
-- series are 0.81-correlated, mean diff 0.034 (CS/ADRC runs slightly hotter), so
-- the gate must be re-validated. New 80th pctile of h10 = 0.251 (~= old 0.25).
-- Input: production-defaults trips (ATR% 0.10): /tmp/v2_prod_atr10.csv
--   (regen: dotnet run -c Release --project TradingEdge.HighFlyer -- --out /tmp/v2_prod_atr10.csv)
-- Run: duckdb -readonly data/trading.db < scripts/equity/heat_csadrc_gate_sweep.sql
-- PF on per-trade RETURN clipped at +50% (project standard). Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_prod_atr10.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 AS h10_new FROM 'data/equity/momentum_v0/heat.parquet')   -- now the CS/ADRC build
SELECT raw.*, (raw.exit_price/raw.entry_price - 1.0) AS ret, hn.h10_new
FROM raw
JOIN br ON br.date=raw.entry_date
LEFT JOIN hn ON hn.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';
-- NOTE: the old whole-tape-heat-vs-new comparison was a one-time validation (results
-- in docs/highflyer_results.md (§ v3) § heat universe). heat.parquet is now the CS/ADRC
-- build, so only the NEW gate is reproducible here.

CREATE OR REPLACE TEMP MACRO gate_new(thr) AS TABLE
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE h10_new IS NULL OR h10_new < thr;

.mode box
SELECT '=== baseline: NO heat gate (clip+50%) ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t;

SELECT '=== heat (CS/ADRC) gate: keep h10_new < thr ===' z;
SELECT 'h10<0.20' g,* FROM gate_new(0.20);
SELECT 'h10<0.225' g,* FROM gate_new(0.225);
SELECT 'h10<0.25' g,* FROM gate_new(0.25);
SELECT 'h10<0.275' g,* FROM gate_new(0.275);
SELECT 'h10<0.30' g,* FROM gate_new(0.30);
