-- TideFlyer — multi-day / longer-term return study on the NEW production book
-- (base + 3d<=-15% + (3d-1d)<=-10%, /tmp/tide_prior2d.csv, 35k trips, PF 1.635).
--
-- Thesis (user): buying INTO a pullback — a name in a longer-term UPTREND that dipped today into a
-- 7d low — should beat the alternatives (a sustained multi-week DECLINER, or a fresh deep washout).
-- Run 6 found a 15d U-shape on the OLD base book (deep-washout OR uptrend-pullback, mushy middle dead);
-- the prior-2d "already sliding" gate may have RESHAPED this — re-examine 7d / 15d / 30d / 60d here.
--
-- chg_Nd = entry_close / close_N_bars_ago - 1 (episode-partitioned LAG, no-lookahead).
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_pullback.sql

CREATE OR REPLACE TEMP TABLE lags AS
SELECT ticker, date, adj_close,
       LAG(adj_close, 7)  OVER w AS c7,
       LAG(adj_close, 15) OVER w AS c15,
       LAG(adj_close, 30) OVER w AS c30,
       LAG(adj_close, 60) OVER w AS c60
FROM daily_episodes WINDOW w AS (PARTITION BY ticker, episode ORDER BY date);

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.signal_date, r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       CASE WHEN l.c7  > 0 THEN r.entry_price/l.c7  - 1.0 END AS chg_7d,
       CASE WHEN l.c15 > 0 THEN r.entry_price/l.c15 - 1.0 END AS chg_15d,
       CASE WHEN l.c30 > 0 THEN r.entry_price/l.c30 - 1.0 END AS chg_30d,
       CASE WHEN l.c60 > 0 THEN r.entry_price/l.c60 - 1.0 END AS chg_60d
FROM read_csv_auto('/tmp/tide_prior2d.csv') r
LEFT JOIN lags l ON l.ticker = r.symbol AND l.date = r.signal_date;

.mode box
SELECT '=== book + join coverage ===' z;
SELECT COUNT(*) n, COUNT(chg_7d) n7, COUNT(chg_15d) n15, COUNT(chg_30d) n30, COUNT(chg_60d) n60,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf
FROM t;

SELECT '=== distributions (p10 / med / p90) ===' z;
SELECT '7d'  w, ROUND(100*quantile_cont(chg_7d,0.10),1) p10, ROUND(100*MEDIAN(chg_7d),1) med, ROUND(100*quantile_cont(chg_7d,0.90),1) p90 FROM t WHERE chg_7d IS NOT NULL
UNION ALL SELECT '15d', ROUND(100*quantile_cont(chg_15d,0.10),1), ROUND(100*MEDIAN(chg_15d),1), ROUND(100*quantile_cont(chg_15d,0.90),1) FROM t WHERE chg_15d IS NOT NULL
UNION ALL SELECT '30d', ROUND(100*quantile_cont(chg_30d,0.10),1), ROUND(100*MEDIAN(chg_30d),1), ROUND(100*quantile_cont(chg_30d,0.90),1) FROM t WHERE chg_30d IS NOT NULL
UNION ALL SELECT '60d', ROUND(100*quantile_cont(chg_60d,0.10),1), ROUND(100*MEDIAN(chg_60d),1), ROUND(100*quantile_cont(chg_60d,0.90),1) FROM t WHERE chg_60d IS NOT NULL;

-- (each window inlined; same 8 bands across 7d/15d/30d/60d for direct comparison.)
SELECT '=== 7d bands ===' z;
WITH b(lo,hi,lbl) AS (VALUES (-1e9,-0.40,'<-40%'),(-0.40,-0.25,'-40..-25'),(-0.25,-0.15,'-25..-15'),(-0.15,-0.08,'-15..-8'),(-0.08,0.0,'-8..0'),(0.0,0.15,'0..+15'),(0.15,0.40,'+15..+40'),(0.40,1e9,'>+40%'))
SELECT b.lbl band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.chg_7d>=b.lo AND t.chg_7d<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== 15d bands ===' z;
WITH b(lo,hi,lbl) AS (VALUES (-1e9,-0.40,'<-40%'),(-0.40,-0.25,'-40..-25'),(-0.25,-0.15,'-25..-15'),(-0.15,-0.08,'-15..-8'),(-0.08,0.0,'-8..0'),(0.0,0.15,'0..+15'),(0.15,0.40,'+15..+40'),(0.40,1e9,'>+40%'))
SELECT b.lbl band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.chg_15d>=b.lo AND t.chg_15d<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== 30d bands ===' z;
WITH b(lo,hi,lbl) AS (VALUES (-1e9,-0.40,'<-40%'),(-0.40,-0.25,'-40..-25'),(-0.25,-0.15,'-25..-15'),(-0.15,-0.08,'-15..-8'),(-0.08,0.0,'-8..0'),(0.0,0.15,'0..+15'),(0.15,0.40,'+15..+40'),(0.40,1e9,'>+40%'))
SELECT b.lbl band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.chg_30d>=b.lo AND t.chg_30d<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== 60d bands (longest-term trend) ===' z;
WITH b(lo,hi,lbl) AS (VALUES (-1e9,-0.40,'<-40%'),(-0.40,-0.25,'-40..-25'),(-0.25,-0.15,'-25..-15'),(-0.15,-0.08,'-15..-8'),(-0.08,0.0,'-8..0'),(0.0,0.15,'0..+15'),(0.15,0.40,'+15..+40'),(0.40,1e9,'>+40%'))
SELECT b.lbl band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.chg_60d>=b.lo AND t.chg_60d<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

-- the direct pullback test: is a name UP over the longer term (but down today) the better setup?
SELECT '=== PULLBACK vs DECLINER vs WASHOUT (using 30d as the trend proxy) ===' z;
SELECT 'UPTREND pullback (30d >= 0)'      lbl, COUNT(*) n,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct FROM t WHERE chg_30d >= 0.0;
SELECT 'mild DECLINER (30d in [-25,0))'   lbl, COUNT(*) n,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct FROM t WHERE chg_30d >= -0.25 AND chg_30d < 0.0;
SELECT 'deep WASHOUT (30d < -25%)'        lbl, COUNT(*) n,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct FROM t WHERE chg_30d < -0.25;
