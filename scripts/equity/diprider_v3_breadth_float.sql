-- DipRiderV3 A-book breadth + float breakdowns (post-hoc, no engine change).
-- breadth = LAG-1 pct_above_20 (prior day's market breadth). float = SEC dei:EntityPublicFloat
-- ASOF-joined (known_date <= entry) and re-anchored to entry-day price (split-safe), $ float.
-- Clipped per-trade return at +50%. Run:
--   duckdb -readonly data/trading.db < scripts/equity/diprider_v3_breadth_float.sql
ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);

CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value AS float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik = fs.cik
WHERE fs.value > 0;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (
  SELECT symbol, trade_date, entry_price, qty, ret_moc, net_pnl,
         LEAST(ret_moc, 0.50) AS ret_clip,
         qty * entry_price * LEAST(ret_moc, 0.50) AS nc
  FROM read_csv_auto('/tmp/diprider_v3_2020_A.csv')
),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1
       FROM 'data/equity/momentum_v0/breadth.parquet'),
withflt AS (
  SELECT raw.*, fl.float_usd, fl.period_end AS flt_pe
  FROM raw
  ASOF LEFT JOIN flt fl
    ON fl.ticker = raw.symbol AND fl.known_date <= raw.trade_date
)
SELECT w.*, br.b_lag1,
       ap_pe.adj_close AS px_pe, ap_en.adj_close AS px_en,
       CASE WHEN w.float_usd IS NOT NULL AND ap_pe.adj_close > 0 AND ap_en.adj_close > 0
            THEN w.float_usd * ap_en.adj_close / ap_pe.adj_close END AS float_usd_at_entry
FROM withflt w
LEFT JOIN br ON br.date = w.trade_date
ASOF LEFT JOIN split_adjusted_prices ap_pe
  ON ap_pe.ticker = w.symbol AND ap_pe.date <= w.flt_pe
LEFT JOIN split_adjusted_prices ap_en
  ON ap_en.ticker = w.symbol AND ap_en.date = w.trade_date;

.print === BREADTH (lag-1 pct_above_20) breakdown, A book, clipped ===
SELECT
  CASE WHEN b_lag1 IS NULL THEN '0: (no breadth)'
       WHEN b_lag1 < 0.20 THEN '1: <0.20'
       WHEN b_lag1 < 0.35 THEN '2: 0.20-0.35'
       WHEN b_lag1 < 0.50 THEN '3: 0.35-0.50'
       WHEN b_lag1 < 0.65 THEN '4: 0.50-0.65'
       WHEN b_lag1 < 0.80 THEN '5: 0.65-0.80'
       ELSE '6: >=0.80' END AS breadth_bucket,
  COUNT(*) n,
  ROUND(100.0*SUM(CASE WHEN net_pnl>0 THEN 1 ELSE 0 END)/COUNT(*),1) win_pct,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN nc>0 THEN nc ELSE 0 END)/NULLIF(-SUM(CASE WHEN nc<0 THEN nc ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(nc)) net_clip
FROM t GROUP BY breadth_bucket ORDER BY breadth_bucket;

.print === FLOAT ($ float at entry) breakdown, A book, clipped ===
SELECT
  CASE WHEN float_usd_at_entry IS NULL THEN '0: (no float)'
       WHEN float_usd_at_entry < 50e6 THEN '1: <$50M'
       WHEN float_usd_at_entry < 150e6 THEN '2: $50-150M'
       WHEN float_usd_at_entry < 300e6 THEN '3: $150-300M'
       WHEN float_usd_at_entry < 1e9 THEN '4: $300M-1B'
       ELSE '5: >=$1B' END AS float_bucket,
  COUNT(*) n,
  ROUND(100.0*SUM(CASE WHEN net_pnl>0 THEN 1 ELSE 0 END)/COUNT(*),1) win_pct,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_raw,
  ROUND(SUM(CASE WHEN nc>0 THEN nc ELSE 0 END)/NULLIF(-SUM(CASE WHEN nc<0 THEN nc ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(nc)) net_clip
FROM t GROUP BY float_bucket ORDER BY float_bucket;
