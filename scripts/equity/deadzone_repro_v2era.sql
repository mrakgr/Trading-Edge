-- REPRODUCE the original v2 dead-zone reclaim>gap-over result, then run the
-- intraday-return controlling test on the SAME population.
--
-- The original (docs/highflyer_results.md (§ v2), 2026-06-18): in the 0-10%-above-
-- 52w-intraday-high DEAD ZONE, RECLAIM (open below the prior high, close above)
-- beat GAP-OVER (open >= high): PF 1.43 vs 1.09, ~2244 trips, reclaim carried
-- nearly all the P&L. This ONLY reproduces on the "PURE GAINERS" population:
--   --up-threshold 0  (NO move floor — the up>=10 default hid the small movers)
-- on the v2-era defaults + breadth.
--
-- Regenerate the CSV first:
--   dotnet run --project TradingEdge.HighFlyer -c Release -- \
--     --up-threshold 0 --max-up-threshold 100 --rvol-min 6 --rvol-max 20 \
--     --min-price 5 --max-tightness 4.0 --max-atr-pct 0.11 --min-intraday-ret -10 \
--     --out /tmp/v2era_up0.csv
-- (v3 exit = 5d time-stop, so PF LEVELS differ slightly from the original 20d-era
--  numbers, but the reclaim>gap DIRECTION and the ~2244-trip count reproduce.)
--
-- prior 52w intraday high = entry_price/(1+pct_52w_high_at_entry); reclaim = the
-- entry-bar OPEN (adj_open) sits below it. RAW PF (no clip) to match the original.
-- Breadth lag1>0.5, >=2005. Run: duckdb -readonly data/trading.db < this.sql

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2era_up0.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
px AS (SELECT ticker, date AS d0, adj_open FROM split_adjusted_prices)
SELECT raw.symbol, raw.entry_date, raw.entry_price, raw.exit_price, raw.net_pnl,
  px.adj_open AS o, raw.entry_price/(1.0+raw.pct_52w_high_at_entry) AS prior_high,
  (raw.exit_price/raw.entry_price - 1.0) AS ret,
  raw.entry_price/NULLIF(px.adj_open,0) - 1.0 AS intraday_ret
FROM raw JOIN br ON br.date=raw.entry_date
JOIN px ON px.ticker=raw.symbol AND px.d0=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.pct_52w_high_at_entry>=0 AND raw.pct_52w_high_at_entry<0.10   -- DEAD ZONE
  AND px.adj_open>0;

.mode box
-- 1) the original split reproduced: reclaim > gap-over
SELECT '=== REPRO: dead-zone reclaim vs gap-over (raw PF, up=0 pure gainers) ===' z;
SELECT CASE WHEN o<prior_high THEN 'reclaim (open below high)' ELSE 'gap-over (open>=high)' END grp,
  COUNT(*) n, ROUND(100.0*AVG((net_pnl>0)::INT),1) win_pct,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(net_pnl)/1000,1) net_k,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_pre15,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND ret<0 THEN ret ELSE 0 END),0),3) pf_post15
FROM t GROUP BY 1 ORDER BY 1;

-- 2) cross-tab: reclaims are 100% intraday-up by construction
SELECT '=== reclaim/gap x intraday-sign cross-tab ===' z;
SELECT CASE WHEN o<prior_high THEN 'reclaim' ELSE 'gap-over' END grp,
  COUNT(*) n, SUM((intraday_ret>0)::INT) n_up, SUM((intraday_ret<=0)::INT) n_dn, ROUND(AVG(intraday_ret),4) mean_intra
FROM t GROUP BY 1 ORDER BY 1;

-- 3) CONTROLLING TEST: hold intraday sign fixed, compare reclaim vs gap-over.
--    Does the reclaim edge SURVIVE controlling for intraday return, or vanish?
SELECT '=== CONTROLLING TEST: reclaim vs gap-over WITHIN intraday sign (raw PF) ===' z;
SELECT CASE WHEN intraday_ret>0 THEN 'intraday UP' ELSE 'intraday DOWN/flat' END sgn,
  CASE WHEN o<prior_high THEN 'reclaim' ELSE 'gap-over' END grp,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(net_pnl)/1000,1) net_k
FROM t GROUP BY 1,2 ORDER BY 1,2;

-- 4) intraday-return bands on this dead zone — is push-size the underlying axis?
SELECT '=== intraday-return bands (push-size) ===' z;
SELECT CASE WHEN intraday_ret<-0.05 THEN '1:<-5%' WHEN intraday_ret<-0.02 THEN '2:-5..-2%'
            WHEN intraday_ret<0 THEN '3:-2..0%' WHEN intraday_ret<0.05 THEN '4:0..5%'
            WHEN intraday_ret<0.10 THEN '5:5..10%' ELSE '6:10%+' END band,
  COUNT(*) n, ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw
FROM t GROUP BY 1 ORDER BY 1;
