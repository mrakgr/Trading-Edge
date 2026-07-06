-- VwapReclaim sweep analysis: for each below-frac x time-stop book, apply the in-play gates
--   ADV = avgvol20 * day_close >= $1M   AND   rvol_0945 > 1   (both from mr_candidate)
-- and report gated PF / trips / win. The engine already applied below-frac + time-stop per file.
-- Usage: duckdb -readonly data/trading.db -c ".read scripts/equity/vwap_reclaim_sweep.sql"  (edit :file)
-- This file is parameterized by a glob in the driver; here we define the reusable gated-PF macro.

-- candidate liquidity/in-play context, one row per (ticker, date)
CREATE OR REPLACE TEMP TABLE cand AS
SELECT ticker, date,
       avgvol20 * day_close AS adv_usd,
       rvol_0945
FROM mr_candidate;

-- gated PF for one trips CSV (pass the path via the ${f} placeholder in the driver)
CREATE OR REPLACE TEMP MACRO gated_pf(path) AS TABLE
WITH t AS (
  SELECT r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
         c.adv_usd, c.rvol_0945
  FROM read_csv_auto(path) r
  LEFT JOIN cand c ON c.ticker = r.symbol AND c.date = r.trade_date::DATE
)
SELECT
  COUNT(*) FILTER (WHERE adv_usd>=1e6 AND rvol_0945>1) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END) FILTER (WHERE adv_usd>=1e6 AND rvol_0945>1),1) win,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END) FILTER (WHERE adv_usd>=1e6 AND rvol_0945>1)
      / NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END) FILTER (WHERE adv_usd>=1e6 AND rvol_0945>1),0),3) pf,
  ROUND(100.0*AVG(ret) FILTER (WHERE adv_usd>=1e6 AND rvol_0945>1),3) avg_pct,
  ROUND(SUM(pnl) FILTER (WHERE adv_usd>=1e6 AND rvol_0945>1)/1e3,0) net_k
FROM t;
