-- TideFlyer — multi-day return breakdowns (3d / 7d / 15d) on the base book.
--
-- Base = /tmp/tide_base.csv (long-MR, 1d in [-40,-5]%, volfrac[0.5,1.5], 5d time-stop).
-- The CSV only records 1d (pct_up_at_entry); compute 3d/7d/15d returns by joining each
-- trip's (symbol, signal_date) to lagged adj_close in daily_episodes (episode-partitioned,
-- gap-severed, no-lookahead). chg_Nd = signal_close / close_N_bars_ago - 1.
--
-- Thesis (user): for 7d/15d specifically, a name that is FLAT-to-slightly-negative (a pullback
-- in an uptrend, open at the upper end) should be the good setup — NOT a multi-day collapse.
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_multiday.sql

-- lagged closes per (ticker, episode) — computed once
CREATE OR REPLACE TEMP TABLE lags AS
SELECT ticker, date, adj_close,
       LAG(adj_close, 3)  OVER w AS c3,
       LAG(adj_close, 7)  OVER w AS c7,
       LAG(adj_close, 15) OVER w AS c15
FROM daily_episodes
WINDOW w AS (PARTITION BY ticker, episode ORDER BY date);

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.signal_date,
       r.pct_up_at_entry AS d1,
       r.net_pnl AS pnl,
       r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       CASE WHEN l.c3  > 0 THEN r.entry_price/l.c3  - 1.0 END AS chg_3d,
       CASE WHEN l.c7  > 0 THEN r.entry_price/l.c7  - 1.0 END AS chg_7d,
       CASE WHEN l.c15 > 0 THEN r.entry_price/l.c15 - 1.0 END AS chg_15d
FROM read_csv_auto('/tmp/tide_base.csv') r
LEFT JOIN lags l ON l.ticker = r.symbol AND l.date = r.signal_date;

CREATE OR REPLACE TEMP MACRO pf(tbl) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM query_table(tbl);

.mode box
SELECT '=== base book (should be ~PF 1.25) + join coverage ===' z;
SELECT COUNT(*) n, COUNT(chg_3d) n3, COUNT(chg_7d) n7, COUNT(chg_15d) n15,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf
FROM t;

SELECT '=== 3d-return distribution ===' z;
SELECT ROUND(100.0*quantile_cont(chg_3d,0.10),1) p10, ROUND(100.0*MEDIAN(chg_3d),1) med,
       ROUND(100.0*quantile_cont(chg_3d,0.90),1) p90 FROM t WHERE chg_3d IS NOT NULL;

-- 3d band breakdown
SELECT '=== 3d-return band breakdown ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (-1.0,-0.40,'<-40%'),(-0.40,-0.25,'-40..-25'),(-0.25,-0.15,'-25..-15'),
  (-0.15,-0.08,'-15..-8'),(-0.08,0.0,'-8..0'),(0.0,0.10,'0..+10'),
  (0.10,0.30,'+10..+30'),(0.30,1e9,'>+30%'))
SELECT b.lbl AS chg_3d_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.chg_3d>=b.lo AND t.chg_3d<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;
