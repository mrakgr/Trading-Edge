-- WHY does the v2 "past-runner personality" monotonicity break under v3 production?
-- One-step ablation from v2-exact -> v3-production, watching the max-log-ATR buckets.
--
-- Measures (RAW PF, the v2 convention): engine log-ATR = mean14 of the LOG TRUE RANGE
--   max(ln(hi/lo), |ln(hi)-ln(prevC)|, |ln(lo)-ln(prevC)|); slope = max-126d of the
--   14-day % return. Trailing-126 max, lagged 1 bar. Buckets = v2 fixed edges.
--
-- Input: a LOOSE superset CSV (move>=10, rvol>=1, ATR%<0.11, tight<4.5, price>=1,
--   intraday gate OFF, no move cap) so every production filter can be applied post-hoc:
--   dotnet run -c Release --project TradingEdge.HighFlyer -- \
--     --up-threshold 0.10 --max-up-threshold 100 --rvol-min 1 --rvol-max 100000 \
--     --min-price 1 --max-tightness 4.5 --max-atr-pct 0.11 --min-intraday-ret -10 \
--     --out /tmp/ablation.csv
-- Breadth lag1>0.5, >=2005. Run: duckdb -readonly data/trading.db < this.sql

CREATE OR REPLACE TEMP TABLE meas AS
WITH base AS (
  SELECT ticker, date, ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY date) rn, adj_high, adj_low, adj_close, adj_open,
    LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) pc, LAG(adj_close,14) OVER (PARTITION BY ticker ORDER BY date) c14
  FROM split_adjusted_prices WHERE ticker IN (SELECT DISTINCT symbol FROM read_csv_auto('/tmp/ablation.csv') WHERE open=0)
),
tr AS (SELECT ticker,date,rn,adj_open,
    CASE WHEN adj_high>0 AND adj_low>0 AND pc>0 THEN GREATEST(ln(adj_high)-ln(adj_low),ABS(ln(adj_high)-ln(pc)),ABS(ln(adj_low)-ln(pc))) END logtr_bar,
    (adj_close/NULLIF(c14,0)-1.0) ret14 FROM base),
w AS (SELECT ticker,date,rn,adj_open, AVG(logtr_bar) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 13 PRECEDING AND CURRENT ROW) logatr14, ret14 FROM tr),
m AS (SELECT ticker,date,adj_open, MAX(logatr14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_atr6,
    MAX(ret14) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) max_slp6,
    COUNT(*) OVER (PARTITION BY ticker ORDER BY rn ROWS BETWEEN 125 PRECEDING AND CURRENT ROW) nbars FROM w)
SELECT ticker,date,adj_open, LAG(max_atr6) OVER (PARTITION BY ticker ORDER BY date) max_atr6,
  LAG(max_slp6) OVER (PARTITION BY ticker ORDER BY date) max_slp6, LAG(nbars) OVER (PARTITION BY ticker ORDER BY date) nbars FROM m;

CREATE OR REPLACE TEMP TABLE A AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/ablation.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet')
SELECT raw.symbol, raw.entry_date, raw.net_pnl, (raw.exit_price/raw.entry_price-1.0) ret,
  raw.pct_up_at_entry mv, raw.rvol_at_entry rvol, raw.atr_pct_14_at_entry entry_atr, raw.entry_price price,
  (raw.entry_price/NULLIF(m.adj_open,0)-1.0) intraday, hn.h10, m.max_atr6, m.max_slp6
FROM raw JOIN br ON br.date=raw.entry_date LEFT JOIN hn ON hn.date=raw.entry_date
JOIN meas m ON m.ticker=raw.symbol AND m.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01' AND m.nbars>=120;

CREATE OR REPLACE TEMP MACRO bk(tbl) AS TABLE
SELECT CASE WHEN max_atr6<0.04 THEN '1:<4' WHEN max_atr6<0.06 THEN '2:4-6' WHEN max_atr6<0.08 THEN '3:6-8'
            WHEN max_atr6<0.11 THEN '4:8-11' WHEN max_atr6<0.15 THEN '5:11-15' ELSE '6:15+' END b, COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf
FROM query_table(tbl) GROUP BY 1 ORDER BY 1;

.mode box
-- ===== ABLATION: v2-exact -> production, one step at a time =====
CREATE OR REPLACE TEMP TABLE s0 AS SELECT * FROM A WHERE rvol>=6 AND rvol<=20 AND price>=5;                       -- v2-exact
CREATE OR REPLACE TEMP TABLE s1 AS SELECT * FROM s0 WHERE mv<0.30;                                                 -- + 30% move cap
CREATE OR REPLACE TEMP TABLE s2 AS SELECT * FROM A WHERE mv<0.30 AND rvol>=5 AND price>=5;                         -- + rvol [6,20]->>=5  (BREAKS)
CREATE OR REPLACE TEMP TABLE s6 AS SELECT * FROM A WHERE mv<0.30 AND rvol>=5 AND entry_atr<0.10 AND price>=1 AND intraday>=-0.07 AND (h10 IS NULL OR h10<0.25); -- full prod
SELECT '=== S0 v2-exact (monotone) n='||(SELECT COUNT(*) FROM s0)||' ===' z; SELECT * FROM bk('s0');
SELECT '=== S1 +movecap30 (still monotone) n='||(SELECT COUNT(*) FROM s1)||' ===' z; SELECT * FROM bk('s1');
SELECT '=== S2 +rvol>=5 (BREAKS) n='||(SELECT COUNT(*) FROM s2)||' ===' z; SELECT * FROM bk('s2');
SELECT '=== S6 full production n='||(SELECT COUNT(*) FROM s6)||' ===' z; SELECT * FROM bk('s6');

-- isolate the rvol change: floor 6->5 vs cap removal
CREATE OR REPLACE TEMP TABLE tmp_floor AS SELECT * FROM A WHERE mv<0.30 AND price>=5 AND rvol>=5 AND rvol<=20;
CREATE OR REPLACE TEMP TABLE tmp_cap   AS SELECT * FROM A WHERE mv<0.30 AND price>=5 AND rvol>=6;
SELECT '=== floor 6->5 only (BREAKS) ===' z; SELECT * FROM bk('tmp_floor');
SELECT '=== remove cap only, keep floor 6 (stays monotone) ===' z; SELECT * FROM bk('tmp_cap');

-- ===== CULPRIT #1: the 6-8% spike is ONE trade (SNS 2009) in the rvol 5-6 cohort =====
SELECT '=== 6-8% ATR bucket split by rvol cohort (raw PF) ===' z;
SELECT CASE WHEN rvol>=6 THEN 'rvol>=6 (v2 had)' ELSE 'rvol 5-6 (NEW)' END grp, COUNT(*) n,
  ROUND(100.0*AVG((ret>0)::INT),1) win, ROUND(MAX(ret),2) max_ret,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf, ROUND(SUM(net_pnl)/1000,1) net_k
FROM A WHERE mv<0.30 AND price>=5 AND max_atr6>=0.06 AND max_atr6<0.08 AND rvol>=5 GROUP BY 1 ORDER BY 1;
SELECT '=== rvol5-6 x 6-8%ATR: PF with vs without the top winner(s) ===' z;
WITH c AS (SELECT *, ROW_NUMBER() OVER (ORDER BY ret DESC) rk FROM A WHERE mv<0.30 AND price>=5 AND max_atr6>=0.06 AND max_atr6<0.08 AND rvol>=5 AND rvol<6)
SELECT 'all' g, COUNT(*) n, ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),2) pf FROM c
UNION ALL SELECT 'ex top-1', COUNT(*)-1, ROUND(SUM(CASE WHEN ret>0 AND rk>1 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>1 THEN ret ELSE 0 END),0),2) FROM c
UNION ALL SELECT 'ex top-3', COUNT(*)-3, ROUND(SUM(CASE WHEN ret>0 AND rk>3 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 AND rk>3 THEN ret ELSE 0 END),0),2) FROM c;

-- ===== CULPRIT #2: heat removes the WINNERS from the 11-15% band =====
SELECT '=== heat removal rate + PF-of-cut by ATR bucket (v2-exact pop) ===' z;
SELECT CASE WHEN max_atr6<0.04 THEN '1:<4' WHEN max_atr6<0.06 THEN '2:4-6' WHEN max_atr6<0.08 THEN '3:6-8'
            WHEN max_atr6<0.11 THEN '4:8-11' WHEN max_atr6<0.15 THEN '5:11-15' ELSE '6:15+' END b,
  COUNT(*) n_all, ROUND(100.0*SUM(CASE WHEN h10>=0.25 THEN 1 ELSE 0 END)/COUNT(*),1) pct_cut,
  ROUND(SUM(CASE WHEN h10>=0.25 AND ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN h10>=0.25 AND ret<0 THEN ret ELSE 0 END),0),2) pf_of_cut
FROM A WHERE mv<0.30 AND rvol>=6 AND rvol<=20 AND price>=5 GROUP BY 1 ORDER BY 1;
