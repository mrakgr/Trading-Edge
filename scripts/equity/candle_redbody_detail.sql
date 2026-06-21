-- Detail on the RED-CLOSE band (body_frac < 0): trades that close UP >=10% vs the
-- prior close, yet the candle is RED (close < open) — i.e. gapped up big at the open
-- then sold off into the close. The most extreme gap-over shape. Worst body band
-- (clip 1.345 / post 1.077). Drill: how red, how big the gap, era split, vs baseline.
-- Input: /tmp/v3_prod_px1.csv. Clip +50%, breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_prod_px1.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
prev AS (SELECT ticker, date, adj_close,
           LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) AS prev_close
         FROM split_adjusted_prices)
SELECT raw.symbol, raw.entry_date, raw.pct_up_at_entry,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,
  s.adj_open o, s.adj_high h, s.adj_low l, s.adj_close c, p.prev_close pc,
  (s.adj_close - s.adj_open)/NULLIF(s.adj_high-s.adj_low,0) AS body_frac,
  (s.adj_open  - s.adj_low )/NULLIF(s.adj_high-s.adj_low,0) AS open_pos,
  (s.adj_close - s.adj_low )/NULLIF(s.adj_high-s.adj_low,0) AS close_pos,
  s.adj_open / NULLIF(p.prev_close,0) - 1.0                 AS gap_pct,       -- overnight gap to the open
  s.adj_close / NULLIF(s.adj_open,0) - 1.0                  AS intraday_ret   -- open->close (negative for red)
FROM raw
JOIN br ON br.date=raw.entry_date
JOIN split_adjusted_prices s ON s.ticker=raw.symbol AND s.date=raw.entry_date
JOIN prev p ON p.ticker=raw.symbol AND p.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND s.adj_high > s.adj_low;

.mode box
-- characterize the red band vs the rest
SELECT '=== RED-CLOSE band (body_frac<0) vs GREEN (body_frac>=0): profile ===' z;
SELECT CASE WHEN body_frac<0 THEN 'RED (gap-up then fade)' ELSE 'GREEN' END grp,
  COUNT(*) n,
  ROUND(AVG(gap_pct),3)        avg_gap,        -- how big the overnight gap to the open
  ROUND(AVG(intraday_ret),3)   avg_open2close, -- avg open->close (red band is negative)
  ROUND(AVG(pct_up_at_entry),3) avg_day_move,  -- close vs prevclose (all >=0.10 by construction)
  ROUND(AVG(open_pos),3)       avg_open_pos,   -- where it opened in range
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t GROUP BY 1 ORDER BY 1;

-- within the red band, how RED? (deeper fade = worse?)
SELECT '=== within RED band: depth of the open->close fade ===' z;
SELECT CASE WHEN intraday_ret<-0.15 THEN '1: fade < -15%'
            WHEN intraday_ret<-0.10 THEN '2: -15..-10%'
            WHEN intraday_ret<-0.05 THEN '3: -10..-5%'
            ELSE                        '4: -5..0% (mild red)' END fade_band,
  COUNT(*) n, ROUND(AVG(gap_pct),3) avg_gap,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t WHERE body_frac<0 GROUP BY 1 ORDER BY 1;

-- is the red band just a big-gap artifact? cross red x gap size
SELECT '=== RED band x overnight gap size ===' z;
SELECT CASE WHEN gap_pct<0.05 THEN '1: gap <5%' WHEN gap_pct<0.10 THEN '2: gap 5-10%'
            WHEN gap_pct<0.20 THEN '3: gap 10-20%' ELSE '4: gap 20%+' END gap_band,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t WHERE body_frac<0 GROUP BY 1 ORDER BY 1;

-- sample the most extreme red bars (big gap, deep fade) to eyeball them
SELECT '=== 12 most extreme RED bars (biggest gap-up then fade) ===' z;
SELECT symbol, entry_date, ROUND(pct_up_at_entry,3) day_move, ROUND(gap_pct,3) gap,
  ROUND(intraday_ret,3) open2close, ROUND(body_frac,2) body, ROUND(ret,3) trade_ret
FROM t WHERE body_frac<0 ORDER BY gap_pct DESC LIMIT 12;

-- baseline
SELECT '=== baseline (all) for reference ===' z;
SELECT COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) clip_post
FROM t;
