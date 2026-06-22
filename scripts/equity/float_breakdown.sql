-- HighFlyer (v3 production) FLOAT breakdown.
--
-- Float as a feature. SEC dei:EntityPublicFloat is a USD value anchored to the close on the
-- issuer's 2nd-fiscal-quarter end (our period_end). The DOLLAR figure conflates company size
-- with float-tightness and is anchored to a stale price, so:
--   1. shares_float = float_usd / price[period_end]   (undo the SEC price anchor -> share count)
--   2. float_usd_at_entry = shares_float * price[entry]  (re-anchor to the price the day we traded)
-- A 1M-share float means $1M behind a $1 stock and $100M behind a $100 stock -- bucket on (2).
-- This is split-SAFE in adjusted space: float_usd * adj_close[entry]/adj_close[period_end]; the
-- split adjustment factor cancels in the ratio (verified on SMCI across its 2024 10:1 split).
--
-- No-lookahead: ASOF join the latest float row with known_date (= period_end + 90d, the 10-K
-- filing deadline) <= entry_date.
--
-- Population: v3 production trips (/tmp/v3_prod_float.csv = engine defaults, PF 1.922) + breadth
-- (lag-1 pct_above_20 > 0.5) + heat (h10 < 0.25) + >=2005. PF on +50%-clipped per-trade return.
-- Run: duckdb -readonly data/trading.db < scripts/equity/float_breakdown.sql

ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);

-- Float per ticker, exploded to (ticker, known_date, period_end, float_usd) via ticker_cik.
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik
WHERE fs.value > 0;

-- Production population with breadth+heat, float ASOF-joined and re-anchored to entry price.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (
  SELECT * FROM read_csv_auto('/tmp/v3_prod_float.csv')
),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1
       FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet'),
-- ASOF: latest known float at/<= entry_date
withflt AS (
  SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price,
         (raw.exit_price/raw.entry_price - 1.0) AS ret,
         fl.float_usd, fl.period_end AS flt_pe
  FROM raw
  ASOF LEFT JOIN flt fl
    ON fl.ticker = raw.symbol AND fl.known_date <= raw.entry_date
)
SELECT w.symbol, w.entry_date, w.ret, w.float_usd, w.flt_pe,
       -- split-safe re-anchor to entry-day price using adjusted closes
       ap_pe.adj_close AS px_pe, ap_en.adj_close AS px_en,
       CASE WHEN w.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN w.float_usd * ap_en.adj_close / ap_pe.adj_close END AS float_usd_at_entry
FROM withflt w
JOIN br ON br.date = w.entry_date
LEFT JOIN hn ON hn.date = w.entry_date
-- price at period_end (ASOF <=) and at entry (exact)
ASOF LEFT JOIN split_adjusted_prices ap_pe
  ON ap_pe.ticker = w.symbol AND ap_pe.date <= w.flt_pe
LEFT JOIN split_adjusted_prices ap_en
  ON ap_en.ticker = w.symbol AND ap_en.date = w.entry_date
WHERE br.b_lag1 > 0.5
  AND w.entry_date >= DATE '2005-01-01'
  AND (hn.h10 IS NULL OR hn.h10 < 0.25);

.mode box
SELECT '=== float coverage on the production population ===' z;
SELECT COUNT(*) trips,
       COUNT(*) FILTER (WHERE float_usd_at_entry IS NOT NULL) with_float,
       ROUND(100.0*COUNT(*) FILTER (WHERE float_usd_at_entry IS NOT NULL)/COUNT(*),1) pct_cov
FROM t;

-- Bucket table (per-bucket, NON-cumulative -- diagnostic) on float_usd_at_entry ($M).
SELECT '=== A) per-bucket PF by float$ at entry (diagnostic) ===' z;
SELECT
  CASE WHEN float_usd_at_entry IS NULL THEN '0:NO DATA'
       WHEN float_usd_at_entry < 50e6   THEN '1:<\$50M'
       WHEN float_usd_at_entry < 150e6  THEN '2:\$50-150M'
       WHEN float_usd_at_entry < 300e6  THEN '3:\$150-300M'
       WHEN float_usd_at_entry < 750e6  THEN '4:\$300-750M'
       WHEN float_usd_at_entry < 2e9    THEN '5:\$750M-2B'
       ELSE '6:>\$2B' END AS float_bucket,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
        / NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_post2015
FROM t GROUP BY 1 ORDER BY 1;

-- Cumulative CEILING: keep float <= N (the decision lens -- is small-float the edge or the trap?)
SELECT '=== B) CUMULATIVE: keep float$ at entry <= N ===' z;
CREATE OR REPLACE TEMP MACRO keep_below(n) AS TABLE
  SELECT COUNT(*) trips, ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t WHERE float_usd_at_entry IS NOT NULL),1) pct_of_covered,
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
          / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
          / NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post2015
  FROM t WHERE float_usd_at_entry IS NOT NULL AND float_usd_at_entry < n;
SELECT '<\$50M'  g,* FROM keep_below(50e6);
SELECT '<\$150M' g,* FROM keep_below(150e6);
SELECT '<\$300M' g,* FROM keep_below(300e6);
SELECT '<\$750M' g,* FROM keep_below(750e6);
SELECT '<\$2B'   g,* FROM keep_below(2e9);

-- Cumulative FLOOR: keep float >= N (is big-float a drag we should exclude?)
SELECT '=== C) CUMULATIVE: keep float$ at entry >= N ===' z;
CREATE OR REPLACE TEMP MACRO keep_above(n) AS TABLE
  SELECT COUNT(*) trips, ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t WHERE float_usd_at_entry IS NOT NULL),1) pct_of_covered,
    ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
          / NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
    ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)
          / NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post2015
  FROM t WHERE float_usd_at_entry IS NOT NULL AND float_usd_at_entry >= n;
SELECT '>=\$50M'  g,* FROM keep_above(50e6);
SELECT '>=\$150M' g,* FROM keep_above(150e6);
SELECT '>=\$300M' g,* FROM keep_above(300e6);
SELECT '>=\$750M' g,* FROM keep_above(750e6);

-- Baseline for reference (whole covered population + the no-data bucket).
SELECT '=== D) baselines ===' z;
SELECT 'all covered' g, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t WHERE float_usd_at_entry IS NOT NULL
UNION ALL
SELECT 'no-data', COUNT(*),
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3)
FROM t WHERE float_usd_at_entry IS NULL
UNION ALL
SELECT 'ENTIRE pop', COUNT(*),
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3)
FROM t;
