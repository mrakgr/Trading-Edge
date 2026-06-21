-- Does the entry-bar INTRADAY return (close/open-1) explain the dead-zone
-- reclaim-vs-gap-over effect? And does negative intraday return go neutral on
-- the lower-tier [5,10]% / rvol>=3 breakout band?
--
-- ⚠️ REPRODUCTION NOTE: the ORIGINAL v2 reclaim result (reclaim PF 1.43 vs
-- gap-over 1.09, ~2244 dead-zone trips) was on a DIFFERENT population than the
-- v3 production tier: it was "PURE GAINERS" (--up-threshold 0, NO move floor) on
-- the v2-era defaults (rvol[6,20], tight<4.0, atr%<0.11, price>=5) + breadth,
-- v2-era exit. The original reclaim edge ONLY appears on the up=0 population.
-- The CORRECT reproduction lives in: scripts/equity/deadzone_repro_v2era.sql
-- (regen CSV with `--up-threshold 0 --rvol-min 6 --rvol-max 20 --min-price 5
--  --max-tightness 4.0 --max-atr-pct 0.11 --min-intraday-ret -10`).
-- THIS script instead studies the v3 tiers, where the reclaim edge is weak/absent.
--
-- Population: LOOSE CSV (move>=5%, rvol>=2, intraday gate OFF), then RESTRICT to
--   move in [5,10]% AND rvol>=3 in SQL. Breadth lag1>0.5, >=2005, closed trips.
-- All quality filters (ATR%<0.10, tight<4.5, price>=1, 52w>=0.95, adv) already
-- baked into the engine run.
--
-- KEY JOIN: entry-bar OPEN comes from split_adjusted_prices (adj_open); the CSV's
-- `open` col is the closed-trip marker (=0), NOT the entry bar open. adj_close ==
-- entry_price (both split-adjusted). Intraday return = adj_open -> adj_close.
--
-- Dead zone = 0..10% above the prior 52w INTRADAY high (pct_52w_high_at_entry in
-- [0,0.10)). Reclaim = entry bar OPENED BELOW the prior high and CLOSED above it
-- (live intraday breakout). Gap-over = opened AT/ABOVE the prior high (whole move
-- pre-open). Prior high level = entry_price/(1+pct_52w_high_at_entry).
--
-- PF on per-trade RETURN clipped at +50% (project standard); loss side raw.
-- Run: duckdb -readonly data/trading.db < scripts/equity/deadzone_intraday_explains.sql

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_loose_intraday.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
px AS (SELECT ticker, date AS d0, adj_open, adj_close FROM split_adjusted_prices)
SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price,
  raw.pct_up_at_entry, raw.rvol_at_entry,
  raw.pct_52w_high_at_entry            AS p52h,          -- close vs 52w intraday high
  px.adj_open                          AS o,             -- entry-bar open
  raw.entry_price                      AS c,             -- entry-bar close (== adj_close)
  (raw.exit_price/raw.entry_price - 1.0) AS ret,         -- full trade return
  px.adj_open/NULLIF(raw.entry_price,0)               AS open_over_close,
  raw.entry_price/NULLIF(px.adj_open,0) - 1.0          AS intraday_ret,  -- close/open - 1
  raw.entry_price/(1.0 + raw.pct_52w_high_at_entry)    AS prior_high     -- reconstructed prior 52w high
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN px ON px.ticker=raw.symbol AND px.d0=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.pct_up_at_entry>=0.05 AND raw.pct_up_at_entry<0.10   -- RESTRICT move [5,10]%
  AND raw.rvol_at_entry>=3                                     -- RESTRICT rvol>=3
  AND px.adj_open>0;

.mode box

-- ============================================================================
-- PART 1: does NEGATIVE intraday return go neutral on the [5,10]% / rvol>=2 band?
-- ============================================================================
SELECT '=== [5,10]% rvol>=3 baseline ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t;

SELECT '=== intraday-return SIGN (close/open) — neutral on the fade?  ===' z;
SELECT CASE WHEN intraday_ret>0 THEN 'A: intraday UP (close>open)'
            WHEN intraday_ret<0 THEN 'B: intraday DOWN (fade)'
            ELSE 'C: flat' END grp,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== intraday-return BANDS ===' z;
SELECT CASE WHEN intraday_ret< -0.05 THEN '1: < -5%'
            WHEN intraday_ret< -0.02 THEN '2: -5..-2%'
            WHEN intraday_ret<  0.00 THEN '3: -2..0%'
            WHEN intraday_ret<  0.02 THEN '4: 0..2%'
            WHEN intraday_ret<  0.05 THEN '5: 2..5%'
            ELSE                       '6: 5%+' END band,
  COUNT(*) n, ROUND(AVG(LEAST(ret,0.50)),4) mean_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- ============================================================================
-- PART 2: dead zone (0..10% above 52w intraday high) — reclaim vs gap-over
-- ============================================================================
SELECT '=== DEAD ZONE (0..10% above 52w high): reclaim vs gap-over ===' z;
SELECT CASE WHEN o < prior_high THEN 'reclaim (open<high -> close>high)'
            ELSE 'gap-over (open>=high)' END grp,
  COUNT(*) n, ROUND(100.0*AVG((ret>0)::INT),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE p52h>=0 AND p52h<0.10
GROUP BY 1 ORDER BY 1;

-- ============================================================================
-- PART 3: does intraday return EXPLAIN the reclaim/gap effect?
-- ============================================================================
-- 3a. cross-tab: is reclaim just a proxy for positive intraday return?
SELECT '=== 3a. reclaim/gap x intraday-sign cross-tab (counts + mean intraday) ===' z;
SELECT CASE WHEN o < prior_high THEN 'reclaim' ELSE 'gap-over' END grp,
  COUNT(*) n,
  SUM((intraday_ret>0)::INT) n_intraday_up,
  SUM((intraday_ret<=0)::INT) n_intraday_dn,
  ROUND(AVG(intraday_ret),4) mean_intraday_ret
FROM t WHERE p52h>=0 AND p52h<0.10
GROUP BY 1 ORDER BY 1;

-- 3b. THE TEST: within the SAME intraday-return sign, does reclaim still beat gap-over?
--     If the reclaim edge collapses once you condition on intraday-up, reclaim was a proxy.
SELECT '=== 3b. reclaim vs gap-over WITHIN intraday-return sign (the controlling test) ===' z;
SELECT CASE WHEN intraday_ret>0 THEN 'intraday UP' ELSE 'intraday DOWN/flat' END intraday_sign,
  CASE WHEN o < prior_high THEN 'reclaim' ELSE 'gap-over' END grp,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE p52h>=0 AND p52h<0.10
GROUP BY 1,2 ORDER BY 1,2;

-- 3c. converse: within reclaim-only and gap-only, does intraday sign still matter?
--     If intraday sign keeps a strong edge inside BOTH, intraday is the deeper signal.
SELECT '=== 3c. intraday sign WITHIN reclaim-only and gap-only ===' z;
SELECT CASE WHEN o < prior_high THEN 'reclaim' ELSE 'gap-over' END grp,
  CASE WHEN intraday_ret>0 THEN 'intraday UP' ELSE 'intraday DOWN/flat' END intraday_sign,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t WHERE p52h>=0 AND p52h<0.10
GROUP BY 1,2 ORDER BY 1,2;
