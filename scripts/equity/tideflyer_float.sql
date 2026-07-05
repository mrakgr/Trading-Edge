-- TideFlyer FLOAT breakdown — does low dollar-float lift the washout book (as it did HighFlyer/LowFlyer)?
--
-- Float method (canonical, from float_breakdown.sql): SEC dei:EntityPublicFloat is a USD value anchored
-- to the close on the issuer's 2nd-fiscal-quarter period_end. Undo the stale price anchor and re-anchor
-- to the entry-day price, split-safe in adjusted space:
--   float_usd_at_entry = float_usd * adj_close[entry] / adj_close[period_end]
-- No-lookahead: ASOF the latest float row with known_date (= period_end + 90d filing deadline) <= entry_date.
--
-- Population = the CURRENT production TideFlyer book /tmp/tide_true.csv (base + 3d<=-15 + true-prior2d<=-10
-- + 60d<=-40, 19,587 trips / PF 1.924). RAW PF (TideFlyer's convention — no +50% clip, unlike HighFlyer).
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_float.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);

CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik
WHERE fs.value > 0;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/tide_true.csv')),
withflt AS (
  SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price, raw.net_pnl AS pnl,
         (raw.exit_price/NULLIF(raw.entry_price,0) - 1.0) AS ret,
         fl.float_usd, fl.period_end AS flt_pe
  FROM raw
  ASOF LEFT JOIN flt fl ON fl.ticker = raw.symbol AND fl.known_date <= raw.entry_date
)
SELECT w.symbol, w.entry_date, w.ret, w.pnl, w.float_usd,
       CASE WHEN w.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN w.float_usd * ap_en.adj_close / ap_pe.adj_close END AS float_usd_at_entry
FROM withflt w
ASOF LEFT JOIN split_adjusted_prices ap_pe ON ap_pe.ticker = w.symbol AND ap_pe.date <= w.flt_pe
LEFT JOIN split_adjusted_prices ap_en ON ap_en.ticker = w.symbol AND ap_en.date = w.entry_date;

-- raw-PF helper on any predicate over t
CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t WHERE float_usd_at_entry IS NOT NULL),1) pct_cov,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t WHERE cond;

.mode box
SELECT '=== float coverage ===' z;
SELECT COUNT(*) trips, COUNT(*) FILTER (WHERE float_usd_at_entry IS NOT NULL) with_float,
  ROUND(100.0*COUNT(*) FILTER (WHERE float_usd_at_entry IS NOT NULL)/COUNT(*),1) pct_cov FROM t;

SELECT '=== A) per-bucket (diagnostic) ===' z;
SELECT
  CASE WHEN float_usd_at_entry IS NULL THEN '0:NO DATA'
       WHEN float_usd_at_entry < 50e6  THEN '1:<50M'
       WHEN float_usd_at_entry < 150e6 THEN '2:50-150M'
       WHEN float_usd_at_entry < 300e6 THEN '3:150-300M'
       WHEN float_usd_at_entry < 750e6 THEN '4:300-750M'
       WHEN float_usd_at_entry < 2e9   THEN '5:750M-2B'
       ELSE '6:>2B' END AS float_bucket,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== B) CUMULATIVE ceiling: keep float <= N ===' z;
SELECT '<50M'   g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 50e6);
SELECT '<150M'  g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 150e6);
SELECT '<300M'  g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 300e6);
SELECT '<750M'  g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 750e6);
SELECT '<2B'    g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry < 2e9);

SELECT '=== C) CUMULATIVE floor: keep float >= N ===' z;
SELECT '>=300M' g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry >= 300e6);
SELECT '>=750M' g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry >= 750e6);
SELECT '>=2B'   g,* FROM pf(float_usd_at_entry IS NOT NULL AND float_usd_at_entry >= 2e9);

SELECT '=== D) baselines ===' z;
SELECT 'all covered' g,* FROM pf(float_usd_at_entry IS NOT NULL);
SELECT 'no-data'     g,* FROM pf(float_usd_at_entry IS NULL);
SELECT 'ENTIRE pop'  g,* FROM pf(true);
