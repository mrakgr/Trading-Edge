-- Past-runner personality breakdown (2026-06-20).
-- Tests whether a stock's trailing-6mo volatility/momentum HISTORY predicts the next
-- breakout's outcome. Three measures, each = trailing-126d max of a 14d window stat,
-- as-of signal date, lagged 1 bar (no lookahead): max ADR, max ATR%, max 14d return.
-- Run on the default-system trips CSV (5d time-stop, tight<4.5, ATR%<0.11).
--   duckdb -readonly data/trading.db < scripts/equity/past_runner_breakdown.sql
-- Expects /tmp/v2_default_45.csv (regen: dotnet run --project TradingEdge.MomentumV2 -c Release -- -o /tmp/v2_default_45.csv)
-- Breadth: data/equity/momentum_v0/breadth.parquet (lag-1 pct_above_20 > 0.5).

-- Build the three trailing-6mo "runner personality" measures, as-of signal (lag-1, no lookahead).
CREATE OR REPLACE TEMP TABLE feat AS
WITH base AS (
  SELECT ticker, date, adj_high h, adj_low l, adj_close c,
         ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY date) rn,
         LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) pc,
         LAG(adj_close,14) OVER (PARTITION BY ticker ORDER BY date) c14
  FROM split_adjusted_prices WHERE adj_close > 0 AND adj_low > 0
),
win AS (  -- 14-day window stats at each date
  SELECT *,
    AVG(h/l - 1.0) OVER w14 AS adr14,                                   -- range-based ADR%
    AVG( GREATEST(h, COALESCE(pc,h)) - LEAST(l, COALESCE(pc,l)) )
       OVER w14 / NULLIF(c,0) AS atr14,                                 -- true-range ATR% (gap-aware)
    CASE WHEN c14 > 0 THEN c/c14 - 1.0 END AS ret14                     -- 14d return (slope/burst)
  FROM base
  WINDOW w14 AS (PARTITION BY ticker ORDER BY date ROWS BETWEEN 13 PRECEDING AND CURRENT ROW)
),
mx AS (  -- trailing 126-bar (~6mo) max of each, ENDING AT THE PRIOR BAR (lag 1 = no lookahead)
  SELECT ticker, date,
    MAX(adr14) OVER w126 AS max_adr6m,
    MAX(atr14) OVER w126 AS max_atr6m,
    MAX(ret14) OVER w126 AS max_ret6m
  FROM win
  WINDOW w126 AS (PARTITION BY ticker ORDER BY date ROWS BETWEEN 126 PRECEDING AND 1 PRECEDING)
)
SELECT * FROM mx;

CREATE OR REPLACE TEMP TABLE tr AS
SELECT t.*, f.max_adr6m, f.max_atr6m, f.max_ret6m
FROM read_csv_auto('/tmp/v2_default_45.csv') t
JOIN feat f ON f.ticker = t.symbol AND f.date = t.signal_date
WHERE t.open = 0;

.mode box
SELECT 'coverage' z, COUNT(*) trips,
  COUNT(max_adr6m) has_adr, COUNT(max_atr6m) has_atr, COUNT(max_ret6m) has_ret FROM tr;
SELECT 'distributions' z,
  ROUND(quantile_cont(max_adr6m,0.1),3) adr_p10, ROUND(MEDIAN(max_adr6m),3) adr_med, ROUND(quantile_cont(max_adr6m,0.9),3) adr_p90,
  ROUND(quantile_cont(max_atr6m,0.1),3) atr_p10, ROUND(MEDIAN(max_atr6m),3) atr_med, ROUND(quantile_cont(max_atr6m,0.9),3) atr_p90,
  ROUND(quantile_cont(max_ret6m,0.2),3) ret_p20, ROUND(MEDIAN(max_ret6m),3) ret_med, ROUND(quantile_cont(max_ret6m,0.9),3) ret_p90
FROM tr;

-- apply production filters: breadth lag-1 > 0.5, entry >= 2005
CREATE OR REPLACE TEMP TABLE trf AS
WITH br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1
            FROM read_parquet('/home/mrakgr/Trading-Edge/data/equity/momentum_v0/breadth.parquet'))
SELECT tr.* FROM tr JOIN br ON br.date = tr.entry_date
WHERE br.b_lag1 > 0.5 AND tr.entry_date >= DATE '2005-01-01';

CREATE OR REPLACE TEMP MACRO bd(expr) AS TABLE
SELECT expr AS bucket, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN net_pnl>0 THEN 1 ELSE 0 END),1) winr,
  ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(net_pnl),0) tot
FROM trf GROUP BY 1 ORDER BY 1;

SELECT '===== max 14d ADR over 6mo =====' z;
FROM bd(CASE WHEN max_adr6m<0.04 THEN '1:<4%' WHEN max_adr6m<0.06 THEN '2:4-6%' WHEN max_adr6m<0.08 THEN '3:6-8%'
             WHEN max_adr6m<0.11 THEN '4:8-11%' WHEN max_adr6m<0.15 THEN '5:11-15%' ELSE '6:15%+' END);
SELECT '===== max 14d ATR% over 6mo =====' z;
FROM bd(CASE WHEN max_atr6m<0.04 THEN '1:<4%' WHEN max_atr6m<0.06 THEN '2:4-6%' WHEN max_atr6m<0.08 THEN '3:6-8%'
             WHEN max_atr6m<0.11 THEN '4:8-11%' WHEN max_atr6m<0.15 THEN '5:11-15%' ELSE '6:15%+' END);
SELECT '===== max 14d return over 6mo (slope/burst) =====' z;
FROM bd(CASE WHEN max_ret6m<0.15 THEN '1:<15%' WHEN max_ret6m<0.30 THEN '2:15-30%' WHEN max_ret6m<0.50 THEN '3:30-50%'
             WHEN max_ret6m<0.80 THEN '4:50-80%' WHEN max_ret6m<1.30 THEN '5:80-130%' ELSE '6:130%+' END);

CREATE OR REPLACE MACRO bde(expr) AS TABLE
SELECT expr AS bucket,
  SUM(CASE WHEN entry_date < DATE '2015-01-01' THEN 1 ELSE 0 END) n_pre,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  SUM(CASE WHEN entry_date >= DATE '2015-01-01' THEN 1 ELSE 0 END) n_post,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM trf GROUP BY 1 ORDER BY 1;

SELECT '== max-ret 6mo by ERA ==' z;
FROM bde(CASE WHEN max_ret6m<0.15 THEN '1:<15%' WHEN max_ret6m<0.30 THEN '2:15-30%' WHEN max_ret6m<0.50 THEN '3:30-50%'
              WHEN max_ret6m<0.80 THEN '4:50-80%' WHEN max_ret6m<1.30 THEN '5:80-130%' ELSE '6:130%+' END);
SELECT '== max-ADR 6mo by ERA ==' z;
FROM bde(CASE WHEN max_adr6m<0.04 THEN '1:<4%' WHEN max_adr6m<0.06 THEN '2:4-6%' WHEN max_adr6m<0.08 THEN '3:6-8%'
              WHEN max_adr6m<0.11 THEN '4:8-11%' WHEN max_adr6m<0.15 THEN '5:11-15%' ELSE '6:15%+' END);

-- independence from entry ATR%: hold entry-ATR% bucket, does max-ret still sort?
SELECT '== max-ret sorts WITHIN entry-ATR%<0.06 (the quiet entries) ==' z;
FROM bd(CASE WHEN max_ret6m<0.30 THEN '1:lo<30%' WHEN max_ret6m<0.80 THEN '2:mid' ELSE '3:hi>80%' END)
WHERE atr_pct_14_at_entry < 0.06;
