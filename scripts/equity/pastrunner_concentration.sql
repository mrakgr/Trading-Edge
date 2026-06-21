-- Is the past-runner TOP bucket (15%+ ATR / 130%+ slope, PF ~3) a genuine broad edge
-- or lottery-driven? Concentration test: each bucket's PF after removing its top-N
-- winners, + the share of total WINNING profit carried by the single biggest trade.
--
-- Answer: the top bucket is the MOST concentrated of all — one trade = 46% (ATR) /
-- 64% (slope) of winning profit; ex-top-5 the slope top bucket is a NET LOSER (0.82).
-- The PF ladder == a CONCENTRATION ladder; the ex-top-5 BASE is flat-to-declining.
-- The signal is a convexity/tail bet ("where the moonshots happen"), not a better
-- typical trade -> size as many tiny lottery tickets, never concentrated.
--
-- Population: v2-exact (rvol[6,20], no move cap, $5, ATR%<0.11, breadth-only, >=2005),
-- the population where the past-runner ladder is clean & monotone. RAW PF.
-- Regen CSV: dotnet run -c Release --project TradingEdge.MomentumV2 -- \
--   --up-threshold 0.10 --max-up-threshold 100 --rvol-min 6 --rvol-max 20 \
--   --min-price 5 --max-tightness 4.5 --max-atr-pct 0.11 --min-intraday-ret -10 \
--   --out /tmp/v2exact.csv
-- Run: duckdb -readonly data/trading.db < scripts/equity/pastrunner_concentration.sql

CREATE OR REPLACE TEMP TABLE meas AS
WITH base AS (SELECT ticker,date,ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY date) rn, adj_high,adj_low,adj_close,
    LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) pc, LAG(adj_close,14) OVER (PARTITION BY ticker ORDER BY date) c14
  FROM split_adjusted_prices WHERE ticker IN (SELECT DISTINCT symbol FROM read_csv_auto('/tmp/v2exact.csv') WHERE open=0)),
tr AS (SELECT ticker,date,rn,
    CASE WHEN adj_high>0 AND adj_low>0 AND pc>0 THEN GREATEST(ln(adj_high)-ln(adj_low),ABS(ln(adj_high)-ln(pc)),ABS(ln(adj_low)-ln(pc))) END logtr_bar,
    (adj_close/NULLIF(c14,0)-1.0) ret14 FROM base),
w AS (SELECT ticker,date,rn, AVG(logtr_bar) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) logatr14, ret14 FROM tr),
m AS (SELECT ticker,date, MAX(logatr14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_atr6,
    MAX(ret14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_slp6,
    COUNT(*) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) nbars FROM w)
SELECT ticker,date, LAG(max_atr6) OVER (PARTITION BY ticker ORDER BY date) max_atr6,
  LAG(max_slp6) OVER (PARTITION BY ticker ORDER BY date) max_slp6, LAG(nbars) OVER (PARTITION BY ticker ORDER BY date) nbars FROM m;
CREATE OR REPLACE TEMP TABLE A AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2exact.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.symbol, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, m.max_atr6, m.max_slp6
FROM raw JOIN br ON br.date=raw.entry_date JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND m.nbars>=120;

.mode box
-- concentration for an ATR bucket [lo,hi)
CREATE OR REPLACE TEMP MACRO catr(lo,hi) AS TABLE
WITH c AS (SELECT *, ROW_NUMBER() OVER (ORDER BY ret DESC) rk FROM A WHERE max_atr6>=lo AND max_atr6<hi),
gp AS (SELECT SUM(CASE WHEN ret>0 THEN ret ELSE 0 END) tw FROM c)
SELECT (SELECT COUNT(*) FROM c) n,
  ROUND((SELECT SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0) FROM c),2) pf_all,
  ROUND((SELECT SUM(CASE WHEN ret>0 AND rk>1 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>1 THEN ret ELSE 0 END),0) FROM c),2) ex1,
  ROUND((SELECT SUM(CASE WHEN ret>0 AND rk>3 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>3 THEN ret ELSE 0 END),0) FROM c),2) ex3,
  ROUND((SELECT SUM(CASE WHEN ret>0 AND rk>5 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>5 THEN ret ELSE 0 END),0) FROM c),2) ex5,
  ROUND(100.0*(SELECT MAX(ret) FROM c)/NULLIF((SELECT tw FROM gp),0),1) top1_pct,
  ROUND(100.0*(SELECT SUM(CASE WHEN rk<=3 THEN ret ELSE 0 END) FROM c)/NULLIF((SELECT tw FROM gp),0),1) top3_pct;
CREATE OR REPLACE TEMP MACRO cslp(lo,hi) AS TABLE
WITH c AS (SELECT *, ROW_NUMBER() OVER (ORDER BY ret DESC) rk FROM A WHERE max_slp6>=lo AND max_slp6<hi),
gp AS (SELECT SUM(CASE WHEN ret>0 THEN ret ELSE 0 END) tw FROM c)
SELECT (SELECT COUNT(*) FROM c) n,
  ROUND((SELECT SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0) FROM c),2) pf_all,
  ROUND((SELECT SUM(CASE WHEN ret>0 AND rk>1 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>1 THEN ret ELSE 0 END),0) FROM c),2) ex1,
  ROUND((SELECT SUM(CASE WHEN ret>0 AND rk>5 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>5 THEN ret ELSE 0 END),0) FROM c),2) ex5,
  ROUND(100.0*(SELECT MAX(ret) FROM c)/NULLIF((SELECT tw FROM gp),0),1) top1_pct,
  ROUND(100.0*(SELECT SUM(CASE WHEN rk<=3 THEN ret ELSE 0 END) FROM c)/NULLIF((SELECT tw FROM gp),0),1) top3_pct;

SELECT '=== max-log-ATR bucket concentration (raw) ===' z;
SELECT '6-8%'   g,* FROM catr(0.06,0.08);
SELECT '8-11%'  g,* FROM catr(0.08,0.11);
SELECT '11-15%' g,* FROM catr(0.11,0.15);
SELECT '15%+ TOP' g,* FROM catr(0.15,99);
SELECT '=== max-slope bucket concentration (raw) ===' z;
SELECT '30-50%'  g,* FROM cslp(0.30,0.50);
SELECT '80-130%' g,* FROM cslp(0.80,1.30);
SELECT '130%+ TOP' g,* FROM cslp(1.30,9e9);
