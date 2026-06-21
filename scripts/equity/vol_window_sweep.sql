-- Vol-window sweep: the lookback (bars) for BOTH the ATR% and tightness measures.
-- Regenerate the 16 inputs first (each is a full engine run; ~15s x 16):
--   for w in $(seq 10 25); do dotnet run -c Release --project TradingEdge.MomentumV2 -- \
--     --vol-window $w --out /tmp/v3_w${w}.csv; done
-- Then: duckdb -readonly data/trading.db < scripts/equity/vol_window_sweep.sql
-- Vol-window (ATR% + tightness lookback) sweep, windows 10..25. Each window is a
-- separate engine run (the window redefines the measures, so production caps
-- atr%<0.10 / tight<4.5 are re-applied INSIDE each run by the engine itself).
-- PF on per-trade RETURN clipped at +50% (project standard). Breadth lag1>0.5, >=2005, closed.
CREATE OR REPLACE TEMP MACRO win(w) AS TABLE
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v3_w' || w || '.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT
  w AS vw,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN (exit_price/entry_price-1)>0 THEN LEAST(exit_price/entry_price-1,0.50) ELSE 0 END)
       /NULLIF(-SUM(CASE WHEN (exit_price/entry_price-1)<0 THEN exit_price/entry_price-1 ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND (exit_price/entry_price-1)>0 THEN LEAST(exit_price/entry_price-1,0.50) ELSE 0 END)
       /NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND (exit_price/entry_price-1)<0 THEN exit_price/entry_price-1 ELSE 0 END),0),3) clip_post,
  ROUND(SUM(net_pnl),0) net_pnl
FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';

.mode box
SELECT '=== vol-window sweep (ATR%+tightness lookback), production caps on, clip +50% ===' z;
SELECT * FROM win(10) UNION ALL SELECT * FROM win(11) UNION ALL SELECT * FROM win(12)
UNION ALL SELECT * FROM win(13) UNION ALL SELECT * FROM win(14) UNION ALL SELECT * FROM win(15)
UNION ALL SELECT * FROM win(16) UNION ALL SELECT * FROM win(17) UNION ALL SELECT * FROM win(18)
UNION ALL SELECT * FROM win(19) UNION ALL SELECT * FROM win(20) UNION ALL SELECT * FROM win(21)
UNION ALL SELECT * FROM win(22) UNION ALL SELECT * FROM win(23) UNION ALL SELECT * FROM win(24)
UNION ALL SELECT * FROM win(25)
ORDER BY vw;
