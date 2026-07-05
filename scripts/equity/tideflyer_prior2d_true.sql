-- TideFlyer — the TRUE prior-2-day return: close[t-1]/close[t-3] - 1 (a real sub-period return),
-- vs the (3d - 1d) diff-of-ratios PROXY we currently gate on. Re-pick the threshold on the principled one.
--
-- Population = the base book BEFORE the prior-2d gate but WITH 3d<=-15% (so we can re-sweep the prior-2d
-- lever cleanly): /tmp/tide_base.csv sliced to 3d<=-15%. c1=close[t-1], c3=close[t-3] via episode LAG.
-- true_p2d = c1/c3 - 1. proxy_p2d = (entry/c3 - 1) - pct_up_at_entry.
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_prior2d_true.sql

CREATE OR REPLACE TEMP TABLE lags AS
SELECT ticker, date, adj_close,
       LAG(adj_close, 1) OVER w AS c1,
       LAG(adj_close, 3) OVER w AS c3
FROM daily_episodes WINDOW w AS (PARTITION BY ticker, episode ORDER BY date);

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.signal_date, r.pct_up_at_entry AS d1, r.net_pnl AS pnl,
       r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       CASE WHEN l.c3 > 0 THEN r.entry_price/l.c3 - 1.0 END AS chg_3d,
       CASE WHEN l.c1 > 0 AND l.c3 > 0 THEN l.c1/l.c3 - 1.0 END AS true_p2d
FROM read_csv_auto('/tmp/tide_base.csv') r
LEFT JOIN lags l ON l.ticker = r.symbol AND l.date = r.signal_date;
-- proxy = chg_3d - d1 (what the engine currently gates on)
ALTER TABLE t ADD COLUMN proxy_p2d DOUBLE;
UPDATE t SET proxy_p2d = chg_3d - d1;

-- restrict to the 3d<=-15% production sub-book (where the prior-2d gate lives)
CREATE OR REPLACE TEMP TABLE g AS SELECT * FROM t WHERE chg_3d <= -0.15;

.mode box
SELECT '=== coverage + how far proxy diverges from the true prior-2d ===' z;
SELECT COUNT(*) n, COUNT(true_p2d) n_true,
  ROUND(100*MEDIAN(proxy_p2d - true_p2d),2) med_gap_pct,
  ROUND(100*quantile_cont(proxy_p2d - true_p2d,0.10),2) p10_gap,
  ROUND(100*quantile_cont(proxy_p2d - true_p2d,0.90),2) p90_gap
FROM g WHERE true_p2d IS NOT NULL;

SELECT '=== TRUE prior-2d (c1/c3-1) band breakdown, within 3d<=-15% ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (-1e9,-0.30,'<-30%'),(-0.30,-0.20,'-30..-20'),(-0.20,-0.15,'-20..-15'),
  (-0.15,-0.10,'-15..-10'),(-0.10,-0.05,'-10..-5'),(-0.05,0.0,'-5..0'),
  (0.0,0.05,'0..+5'),(0.05,1e9,'>+5%'))
SELECT b.lbl AS true_p2d_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN g.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN g.pnl>0 THEN g.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN g.pnl<0 THEN g.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(g.ret),3) avg_pct
FROM g, b WHERE g.true_p2d>=b.lo AND g.true_p2d<b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== CUMULATIVE ceilings on the TRUE prior-2d (pick the threshold) ===' z;
WITH c(ceil,lbl) AS (VALUES
  (0.05,'true<=+5% (~off)'),(0.0,'true<=0%'),(-0.05,'true<=-5%'),
  (-0.10,'true<=-10%'),(-0.15,'true<=-15%'),(-0.20,'true<=-20%'))
SELECT c.lbl AS true_ceiling, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN g.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN g.pnl>0 THEN g.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN g.pnl<0 THEN g.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(g.ret),3) avg_pct, ROUND(SUM(g.pnl)/1e6,2) net_m
FROM g, c WHERE g.true_p2d <= c.ceil
GROUP BY c.ceil,c.lbl ORDER BY c.ceil DESC;

SELECT '=== head-to-head at the same nominal -10% cut ===' z;
SELECT 'PROXY (3d-1d) <= -10%' lbl, COUNT(*) n,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct FROM g WHERE proxy_p2d <= -0.10;
SELECT 'TRUE c1/c3-1 <= -10%'  lbl, COUNT(*) n,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct FROM g WHERE true_p2d <= -0.10;
