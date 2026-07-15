-- SANITY CHECK: faithfully reproduce the v2 06-20 "Past-runner personality" section
-- (docs/highflyer_results.md (§ v2)) with the EXACT original measures, buckets, population,
-- and RAW PF — then add the +50% CLIP column to show the top-bucket collapse.
--
-- THREE MEASURES (trailing 126 trading days = 6mo, LAGGED 1 bar = signal bar excluded,
-- no lookahead):
--   max ADR 6mo   = max over 126d of mean14( adj_high/adj_low - 1 )         (range-based, literal Sykes ADR)
--   max ATR% 6mo  = max over 126d of mean14( true_range / adj_close )       (gap-aware; TR DIVIDED BY CLOSE, not log)
--   max ret/slope = max over 126d of ( adj_close_t / adj_close_{t-14} - 1 ) (14d directional burst; NOT an OLS slope)
--
-- Population: v2-era production — move>=10%, rvol[6,20], ATR%<0.11, tight<4.5, price>=5,
-- 5d stop, breadth lag1>0.5, >=2005. Target ~2,580 trips (reproduces at 2,619).
-- Regen the CSV:
--   dotnet run -c Release --project TradingEdge.HighFlyer -- \
--     --up-threshold 0.10 --max-up-threshold 100 --rvol-min 6 --rvol-max 20 \
--     --min-price 5 --max-tightness 4.5 --max-atr-pct 0.11 --min-intraday-ret -10 \
--     --out /tmp/pastrunner_repro.csv
-- Run: duckdb -readonly data/trading.db < scripts/equity/pastrunner_repro.sql

CREATE OR REPLACE TEMP TABLE meas AS
WITH base AS (
  SELECT ticker, date, ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY date) rn, adj_high, adj_low, adj_close,
    LAG(adj_close,14) OVER (PARTITION BY ticker ORDER BY date) AS c14,
    (adj_high/NULLIF(adj_low,0)-1.0) AS adr_bar,                                                          -- ADR: high/low-1
    GREATEST(adj_high-adj_low, ABS(adj_high-LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date)),
             ABS(adj_low-LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date)))/NULLIF(adj_close,0) AS atrpct_bar, -- TR/close
    -- engine log-ATR = log TRUE RANGE (max of 3 log legs incl. gap to prevClose), NOT ln(high/low) [=log-ADR]
    CASE WHEN adj_high>0 AND adj_low>0 AND LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date)>0 THEN
      GREATEST( ln(adj_high)-ln(adj_low),
                ABS(ln(adj_high)-ln(LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date))),
                ABS(ln(adj_low) -ln(LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date))) ) END AS logatr_bar
  FROM split_adjusted_prices
  WHERE ticker IN (SELECT DISTINCT symbol FROM read_csv_auto('/tmp/pastrunner_repro.csv') WHERE open=0)
),
w AS (SELECT ticker, date, rn,
    AVG(adr_bar)    OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) adr14,
    AVG(atrpct_bar) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) atr14,
    AVG(logatr_bar) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) logatr14,
    (adj_close/NULLIF(c14,0)-1.0) ret14 FROM base),
m AS (SELECT ticker, date,
    MAX(adr14)    OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_adr6,
    MAX(atr14)    OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_atr6,
    MAX(logatr14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_logatr6,
    MAX(ret14)    OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_ret6,
    COUNT(*)      OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) nbars FROM w)
SELECT ticker, date,                                                          -- LAG 1 bar (exclude the signal bar)
  LAG(max_adr6)    OVER (PARTITION BY ticker ORDER BY date) max_adr6,
  LAG(max_atr6)    OVER (PARTITION BY ticker ORDER BY date) max_atr6,
  LAG(max_logatr6) OVER (PARTITION BY ticker ORDER BY date) max_logatr6,
  LAG(max_ret6)    OVER (PARTITION BY ticker ORDER BY date) max_ret6,
  LAG(nbars)       OVER (PARTITION BY ticker ORDER BY date) nbars FROM m;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/pastrunner_repro.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT (raw.exit_price/raw.entry_price-1.0) ret, m.max_adr6, m.max_atr6, m.max_logatr6, m.max_ret6
FROM raw JOIN br ON br.date=raw.entry_date JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND m.nbars>=120;

.mode box
SELECT '=== trip count (target ~2580) ===' z; SELECT COUNT(*) n FROM t;

-- helper: raw + clip PF for a given measure's buckets
SELECT '=== max ADR 6mo: raw|clip (orig top 3.126) ===' z;
SELECT CASE WHEN max_adr6<0.04 THEN '1:<4%' WHEN max_adr6<0.06 THEN '2:4-6%' WHEN max_adr6<0.08 THEN '3:6-8%'
            WHEN max_adr6<0.11 THEN '4:8-11%' WHEN max_adr6<0.15 THEN '5:11-15%' ELSE '6:15%+' END b, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== max ret 6mo / slope: raw|clip (orig top 3.599) ===' z;
SELECT CASE WHEN max_ret6<0.15 THEN '1:<15%' WHEN max_ret6<0.30 THEN '2:15-30%' WHEN max_ret6<0.50 THEN '3:30-50%'
            WHEN max_ret6<0.80 THEN '4:50-80%' WHEN max_ret6<1.30 THEN '5:80-130%' ELSE '6:130%+' END b, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== max ATR% 6mo (TR/close): raw|clip (orig top 2.91) ===' z;
SELECT CASE WHEN max_atr6<0.04 THEN '1:<4%' WHEN max_atr6<0.06 THEN '2:4-6%' WHEN max_atr6<0.08 THEN '3:6-8%'
            WHEN max_atr6<0.11 THEN '4:8-11%' WHEN max_atr6<0.15 THEN '5:11-15%' ELSE '6:15%+' END b, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t GROUP BY 1 ORDER BY 1;

-- FORMULA INVARIANCE: TR/close ATR% vs engine log-ATR (log true range). corr ~0.959, same shape.
SELECT '=== formula invariance: corr(TR/close, engine log-ATR true-range) ===' z;
SELECT ROUND(CORR(max_atr6, max_logatr6),4) corr_atr_logatr FROM t;
SELECT '=== TR/close ATR% decile (raw|clip) vs LOG-ATR decile (raw|clip) ===' z;
WITH d AS (SELECT *, NTILE(10) OVER (ORDER BY max_atr6) q FROM t)
SELECT 'TR/close D'||q b,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) raw,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) clip
FROM d GROUP BY q ORDER BY q;
WITH d AS (SELECT *, NTILE(10) OVER (ORDER BY max_logatr6) q FROM t)
SELECT 'log-ATR D'||q b,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) raw,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) clip
FROM d GROUP BY q ORDER BY q;
