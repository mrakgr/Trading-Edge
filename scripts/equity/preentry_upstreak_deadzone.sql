-- Are dead-zone trades just names that broke out DAYS EARLIER and have been
-- grinding up since? Measure the PRE-ENTRY up-streak: how many consecutive days
-- (ending at the entry bar, day 0) closed higher than the prior day.
--   up0 = entry close > prior close (always true-ish: it's a gainer-day entry)
--   up_streak = length of the run of consecutive up-days ending at day 0.
-- Hypothesis: dead-zone trades (extended above the 52w max CLOSE, d52 high) carry
-- LONGER pre-entry up-streaks — the real breakout was earlier, and the entry-day
-- 5-10% move is a late continuation. If so, the 5% move filter is too coarse and a
-- pre-entry-streak / extension cap is the better discriminator.
--
-- Population: [5,10]% move, rvol>3, FULL production (ATR%<0.10, tight<4.5, price>=1,
--   52w>=0.95, -0.07 intraday gate, 5d stop) + breadth lag1>0.5 + heat h10<0.25.
-- Input: /tmp/v3_510_rvol3.csv. d52 = pct_52w_at_entry (close vs 52w max close).
-- PF clip +50%. Run: duckdb -readonly data/trading.db < this.sql

CREATE OR REPLACE TEMP TABLE t AS
WITH px AS (  -- per-ticker prior-day closes (LAG), to read the run of up-days ending at d0
  SELECT ticker, date AS d0, adj_close AS c0,
    LAG(adj_close,1) OVER (PARTITION BY ticker ORDER BY date) AS p1,
    LAG(adj_close,2) OVER (PARTITION BY ticker ORDER BY date) AS p2,
    LAG(adj_close,3) OVER (PARTITION BY ticker ORDER BY date) AS p3,
    LAG(adj_close,4) OVER (PARTITION BY ticker ORDER BY date) AS p4,
    LAG(adj_close,5) OVER (PARTITION BY ticker ORDER BY date) AS p5
  FROM split_adjusted_prices),
raw AS (SELECT * FROM read_csv_auto('/tmp/v3_510_rvol3.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price,
  raw.pct_52w_at_entry AS d52, (raw.exit_price/raw.entry_price - 1.0) AS ret,
  -- up_streak ending at day0: day0 up (c0>p1)? then p1>p2? then p2>p3? ...
  -- count consecutive trues from the entry bar backward.
  CASE
    WHEN NOT (px.c0 > px.p1) THEN 0                       -- entry day itself not an up-close vs prior
    WHEN NOT (px.p1 > px.p2) THEN 1                       -- only the entry day
    WHEN NOT (px.p2 > px.p3) THEN 2
    WHEN NOT (px.p3 > px.p4) THEN 3
    WHEN NOT (px.p4 > px.p5) THEN 4
    ELSE 5                                                -- 5+ consecutive up-days ending at entry
  END AS up_streak
FROM raw
JOIN br ON br.date=raw.entry_date
LEFT JOIN hn ON hn.date=raw.entry_date
JOIN px ON px.ticker=raw.symbol AND px.d0=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND (hn.h10 IS NULL OR hn.h10 < 0.25)
  AND raw.pct_up_at_entry>=0.05 AND raw.pct_up_at_entry<0.10 AND raw.rvol_at_entry>=3
  AND px.p5 IS NOT NULL;

.mode box
-- 1) does the dead zone carry longer pre-entry up-streaks? (mean streak by d52 band)
SELECT '=== mean pre-entry up-streak by distance-from-max-close band ===' z;
SELECT CASE WHEN d52<0 THEN '1: <0 (below close-high)' WHEN d52<0.01 THEN '2: 0..1% (fresh high)'
            WHEN d52<0.03 THEN '3: 1..3%' WHEN d52<0.05 THEN '4: 3..5%' ELSE '5: 5%+ (extended)' END d52_band,
  COUNT(*) n, ROUND(AVG(up_streak),2) mean_upstreak, ROUND(MEDIAN(up_streak),1) med_upstreak,
  ROUND(100.0*AVG((up_streak>=3)::INT),1) pct_streak3plus
FROM t GROUP BY 1 ORDER BY 1;

-- 2) PF by pre-entry up-streak length (does a longer prior run = worse trade?)
SELECT '=== PF by pre-entry up-streak length (all dead-zone+breakout pooled) ===' z;
SELECT up_streak, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- 3) THE KEY 2D: is the dead zone bad BECAUSE of long streaks, or independently?
--    PF by d52 band x up-streak bucket. If the dead zone is dead at EVERY streak
--    length, extension is the real axis; if it's only dead at long streaks, the
--    streak is the real axis and d52 was a proxy.
SELECT '=== 2D: PF by d52 band x pre-entry up-streak bucket ===' z;
SELECT CASE WHEN d52<0.01 THEN 'A: <1% (at high)' WHEN d52<0.03 THEN 'B: 1..3%'
            WHEN d52<0.05 THEN 'C: 3..5%' ELSE 'D: 5%+ extended' END d52_band,
  CASE WHEN up_streak<=1 THEN '0-1 (fresh)' WHEN up_streak<=2 THEN '2' WHEN up_streak<=3 THEN '3' ELSE '4+' END streak_bkt,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t GROUP BY 1,2 ORDER BY 1,2;

-- 4) within the EXTENDED zone only (d52>=0.03): does streak length still matter?
SELECT '=== extended zone d52>=0.03: PF by up-streak ===' z;
SELECT up_streak, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t WHERE d52>=0.03 GROUP BY 1 ORDER BY 1;
