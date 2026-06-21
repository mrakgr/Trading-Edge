-- Do 6mo-max-ATR% and 6mo-slope COMBINE, or are they redundant? So far used in
-- isolation; test the joint 2D grid + correlation.
--   max_atr6mo = rolling-126d max of the 14-bar log-ATR (historical vol episode).
--   slope6mo   = OLS slope of ln(close) over 126 bars (6mo log-drift).
-- Both as-of entry (no lookahead). Quintiled within the population.
--
-- Population: PRODUCTION system, rvol lowered to 2 — move in [10,30]%, rvol>=2,
--   full production (ATR%<0.10, tight<4.5, price>=1, 52w>=0.95, -0.07 intraday gate,
--   5d stop) + breadth lag1>0.5 + heat h10<0.25.
-- Input: /tmp/v3_510_rvol1.csv (rvol>=1; contains [10,30]). PF clip +50%. >=2005, closed.
-- Run: duckdb -readonly data/trading.db < scripts/equity/atr_slope_combine.sql

CREATE OR REPLACE TEMP TABLE meas AS
WITH base AS (
  SELECT p.ticker, p.date, ROW_NUMBER() OVER (PARTITION BY p.ticker ORDER BY p.date) rn,
    ln(GREATEST(p.adj_high,1e-9)/GREATEST(p.adj_low,1e-9)) AS log_tr,
    ln(GREATEST(p.adj_close,1e-9)) AS lc
  FROM split_adjusted_prices p
  WHERE p.ticker IN (SELECT DISTINCT symbol FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0)
),
atr AS (SELECT *, AVG(log_tr) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) AS atr14 FROM base)
SELECT ticker, date,
  MAX(atr14)        OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS max_atr6mo,
  REGR_SLOPE(lc,rn) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS slope6mo,
  COUNT(*)          OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) AS nbars
FROM atr;

CREATE OR REPLACE TEMP TABLE tq AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet'),
j AS (
  SELECT (raw.exit_price/raw.entry_price-1.0) AS ret, raw.entry_date, m.max_atr6mo, m.slope6mo
  FROM raw JOIN br ON br.date=raw.entry_date LEFT JOIN hn ON hn.date=raw.entry_date
  JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
  WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND (hn.h10 IS NULL OR hn.h10<0.25)
    AND raw.pct_up_at_entry>=0.10 AND raw.pct_up_at_entry<0.30 AND raw.rvol_at_entry>=2 AND m.nbars>=120
)
SELECT *, NTILE(5) OVER (ORDER BY max_atr6mo) aq, NTILE(5) OVER (ORDER BY slope6mo) sq FROM j;

.mode box
SELECT '=== baseline (production, rvol>=2) ===' z;
SELECT COUNT(*) n, ROUND(CORR(max_atr6mo, slope6mo),3) corr_atr_slope,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM tq;

-- isolation: each quintile alone
SELECT '=== max-ATR% 6mo quintile (isolation) ===' z;
SELECT aq, COUNT(*) n, ROUND(MIN(max_atr6mo),4) lo, ROUND(MAX(max_atr6mo),4) hi,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM tq GROUP BY 1 ORDER BY 1;

SELECT '=== slope 6mo quintile (isolation) ===' z;
SELECT sq, COUNT(*) n, ROUND(MIN(slope6mo),5) lo, ROUND(MAX(slope6mo),5) hi,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM tq GROUP BY 1 ORDER BY 1;

-- THE 2D GRID: PF by ATR% quintile (rows) x slope quintile (cols). Does slope add
-- anything WITHIN an ATR% stratum? (If rows are flat across cols, slope is redundant.)
SELECT '=== 2D PF grid: ATR% quintile (aq) x slope quintile (sq) ===' z;
SELECT aq,
  ROUND(SUM(CASE WHEN sq=1 AND ret>0 THEN LEAST(ret,0.50) WHEN sq=1 THEN 0 END)/NULLIF(-SUM(CASE WHEN sq=1 AND ret<0 THEN ret END),0),2) s1,
  ROUND(SUM(CASE WHEN sq=2 AND ret>0 THEN LEAST(ret,0.50) WHEN sq=2 THEN 0 END)/NULLIF(-SUM(CASE WHEN sq=2 AND ret<0 THEN ret END),0),2) s2,
  ROUND(SUM(CASE WHEN sq=3 AND ret>0 THEN LEAST(ret,0.50) WHEN sq=3 THEN 0 END)/NULLIF(-SUM(CASE WHEN sq=3 AND ret<0 THEN ret END),0),2) s3,
  ROUND(SUM(CASE WHEN sq=4 AND ret>0 THEN LEAST(ret,0.50) WHEN sq=4 THEN 0 END)/NULLIF(-SUM(CASE WHEN sq=4 AND ret<0 THEN ret END),0),2) s4,
  ROUND(SUM(CASE WHEN sq=5 AND ret>0 THEN LEAST(ret,0.50) WHEN sq=5 THEN 0 END)/NULLIF(-SUM(CASE WHEN sq=5 AND ret<0 THEN ret END),0),2) s5
FROM tq GROUP BY aq ORDER BY aq;

SELECT '=== 2D N grid (cell counts) ===' z;
SELECT aq, COUNT(*) FILTER (WHERE sq=1) s1, COUNT(*) FILTER (WHERE sq=2) s2,
  COUNT(*) FILTER (WHERE sq=3) s3, COUNT(*) FILTER (WHERE sq=4) s4, COUNT(*) FILTER (WHERE sq=5) s5
FROM tq GROUP BY aq ORDER BY aq;

-- combined gate idea: keep BOTH >= their 2nd quintile (drop calm base AND drop the
-- worst slope), vs each alone, vs baseline.
SELECT '=== combined gate: drop bottom quintile of one/both ===' z;
SELECT 'baseline (all)'              g, COUNT(*) n, ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) post FROM tq
UNION ALL SELECT 'drop ATR q1',       COUNT(*), ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3),
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) FROM tq WHERE aq>=2
UNION ALL SELECT 'drop slope q1',     COUNT(*), ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3),
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) FROM tq WHERE sq>=2
UNION ALL SELECT 'drop BOTH q1',      COUNT(*), ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3),
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) FROM tq WHERE aq>=2 AND sq>=2;

-- ============================================================================
-- Why does this contradict the v2 06-20 "past-runner floor, top-decile PF ~2.7"?
-- It's the CLIP: 06-20 was RAW PF (pre clip-everywhere). Decile raw-vs-clip on the
-- [10,30] rvol>=5 tier shows the top ATR% decile is raw 2.65 (~the remembered 2.7)
-- -> clip 1.71, and slope's mid deciles are raw 6.3/3.6 -> clip 2.2/1.7 (lottery
-- winners the clip removes). Slope was a lottery-winner concentrator, not an edge.
-- ============================================================================
CREATE OR REPLACE TEMP TABLE b5 AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT (raw.exit_price/raw.entry_price-1.0) ret, m.max_atr6mo, m.slope6mo
FROM raw JOIN br ON br.date=raw.entry_date LEFT JOIN hn ON hn.date=raw.entry_date
JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND (hn.h10 IS NULL OR hn.h10<0.25)
  AND raw.pct_up_at_entry>=0.10 AND raw.pct_up_at_entry<0.30 AND raw.rvol_at_entry>=5 AND m.nbars>=120;
SELECT '=== max-6mo-ATR% DECILE: raw vs clip ([10,30] rvol>=5) ===' z;
WITH d AS (SELECT *, NTILE(10) OVER (ORDER BY max_atr6mo) dq FROM b5)
SELECT dq, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf_raw,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf_clip
FROM d GROUP BY 1 ORDER BY 1;
SELECT '=== max-6mo-SLOPE DECILE: raw vs clip ([10,30] rvol>=5) ===' z;
WITH d AS (SELECT *, NTILE(10) OVER (ORDER BY slope6mo) dq FROM b5)
SELECT dq, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf_raw,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf_clip
FROM d GROUP BY 1 ORDER BY 1;

-- ============================================================================
-- Two follow-ups to the "why did it look clear-cut yesterday" question:
-- (A) NO breadth/heat: does the regime filter explain it? NO — same character,
--     ATR% even flatter clipped (Q1 1.13 -> Q5 1.37), slope still dead (raw Q3
--     spike 1.72 -> clip 1.27). Rules out regime interaction.
-- (B) Cumulative FLOOR raw vs clip: yesterday's "monotone, top ~2.7" was the
--     cumulative-floor + raw combo. Raw rises to 2.70 @ d5; clip is flat/
--     non-monotone (1.66 -> 1.86 peak -> 1.71). It's a bottom-only floor.
-- ============================================================================
-- (A) no breadth/heat
CREATE OR REPLACE TEMP TABLE bnh AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol1.csv') WHERE open=0)
SELECT (raw.exit_price/raw.entry_price-1.0) ret, m.max_atr6mo, m.slope6mo
FROM raw JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE raw.entry_date>=DATE '2005-01-01'
  AND raw.pct_up_at_entry>=0.10 AND raw.pct_up_at_entry<0.30 AND raw.rvol_at_entry>=2 AND m.nbars>=120;
SELECT '=== (A) ATR% quintile NO breadth/heat: clip vs raw ===' z;
WITH d AS (SELECT *, NTILE(5) OVER (ORDER BY max_atr6mo) q FROM bnh)
SELECT q, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw
FROM d GROUP BY 1 ORDER BY 1;
SELECT '=== (A) SLOPE quintile NO breadth/heat: clip vs raw ===' z;
WITH d AS (SELECT *, NTILE(5) OVER (ORDER BY slope6mo) q FROM bnh)
SELECT q, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw
FROM d GROUP BY 1 ORDER BY 1;

-- (B) cumulative floor raw vs clip (reuses b5 = [10,30] rvol>=5)
SELECT '=== (B) max-ATR% cumulative FLOOR (keep >= decile boundary): raw vs clip ===' z;
WITH q AS (SELECT quantile_cont(max_atr6mo, [0.0,0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9]) qs FROM b5)
SELECT 'floor d'||i g,
  (SELECT COUNT(*) FROM b5 WHERE max_atr6mo >= (SELECT qs[i+1] FROM q)) n,
  ROUND((SELECT SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0) FROM b5 WHERE max_atr6mo >= (SELECT qs[i+1] FROM q)),2) pf_raw,
  ROUND((SELECT SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0) FROM b5 WHERE max_atr6mo >= (SELECT qs[i+1] FROM q)),2) pf_clip
FROM range(0,10) t(i) ORDER BY i;
