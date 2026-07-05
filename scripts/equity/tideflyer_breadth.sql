-- TideFlyer BREADTH breakdown. breadth = pct_above_20 (fraction of the universe above its 20d MA),
-- LAG-1 (yesterday's value — no-lookahead, the market state going INTO the entry day; same convention
-- HighFlyer/float used). User hypothesis: strong breadth should LIFT PF (a dip in a healthy tape
-- reverts; a dip in a collapsing tape keeps falling). BUT test DIRECTION empirically — it could invert
-- (deep washouts may revert BEST when the whole market is puking & everything's oversold — cf. float,
-- ATR%, which both inverted vs HighFlyer here).
-- Population = production book /tmp/tide_final.csv (13,122 / PF 2.295, next-open target exit). RAW PF.
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_breadth.sql

CREATE OR REPLACE TEMP TABLE br AS
SELECT date, LAG(pct_above_20) OVER (ORDER BY date) AS b_lag1
FROM 'data/equity/momentum_v0/breadth.parquet';

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.entry_date, r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       b.b_lag1 AS breadth
FROM read_csv_auto('/tmp/tide_final.csv') r
LEFT JOIN br b ON b.date = r.entry_date::DATE;

CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n, ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t WHERE breadth IS NOT NULL),1) pct_cov,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t WHERE cond;

.mode box
SELECT '=== coverage + breadth distribution ===' z;
SELECT COUNT(*) n, COUNT(breadth) with_breadth,
  ROUND(quantile_cont(breadth,0.10),3) p10, ROUND(MEDIAN(breadth),3) med, ROUND(quantile_cont(breadth,0.90),3) p90 FROM t;

SELECT '=== A) breadth bands (is strong tape better, or does it invert?) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (0.0,0.2,'<0.2 (puking)'),(0.2,0.35,'0.2-0.35'),(0.35,0.5,'0.35-0.5'),
  (0.5,0.65,'0.5-0.65'),(0.65,0.8,'0.65-0.8'),(0.8,1.01,'>0.8 (euphoric)'))
SELECT b.lbl AS breadth_band, COUNT(*) n, ROUND(100.0*AVG(CASE WHEN t.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN t.pnl>0 THEN t.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN t.pnl<0 THEN t.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(t.ret),3) avg_pct
FROM t, b WHERE t.breadth>=b.lo AND t.breadth<b.hi GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== B) CUMULATIVE floor: keep breadth >= N (strong-tape hypothesis) ===' z;
SELECT 'breadth>=0.35' g,* FROM pf(breadth>=0.35);
SELECT 'breadth>=0.5'  g,* FROM pf(breadth>=0.5);
SELECT 'breadth>=0.65' g,* FROM pf(breadth>=0.65);

SELECT '=== C) CUMULATIVE ceiling: keep breadth <= N (washout-in-a-puking-tape hypothesis) ===' z;
SELECT 'breadth<0.5'  g,* FROM pf(breadth<0.5);
SELECT 'breadth<0.35' g,* FROM pf(breadth<0.35);
SELECT 'breadth<0.2'  g,* FROM pf(breadth<0.2);

SELECT '=== D) baseline ===' z;
SELECT 'all covered' g,* FROM pf(breadth IS NOT NULL);
SELECT 'ENTIRE pop'  g,* FROM pf(true);
