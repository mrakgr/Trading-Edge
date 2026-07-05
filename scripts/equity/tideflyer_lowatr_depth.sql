-- TideFlyer — can the DEPTH levers (1d / 3d / true-prior2d) rescue the weak [0.08,0.10) ATR bucket?
-- That bucket is the softest kept slice (PF 1.631). Is it weak INHERENTLY, or just because it holds
-- shallower washouts? If depth lifts it, the ATR floor can stay at 0.08 (keep the trips); if not, 0.10
-- is the better floor. Population = the NEW production book /tmp/tide_atr.csv (14,645 / PF 2.105),
-- restricted to the [0.08,0.10) ATR bucket. Compare vs the >=0.10 bucket to see if depth closes the gap.
-- Returns joined from daily_episodes (episode LAG, no-lookahead). Run:
--   duckdb -readonly data/trading.db < scripts/equity/tideflyer_lowatr_depth.sql

CREATE OR REPLACE TEMP TABLE lags AS
SELECT ticker, date, adj_close,
       LAG(adj_close,1) OVER w AS c1, LAG(adj_close,3) OVER w AS c3, LAG(adj_close,60) OVER w AS c60
FROM daily_episodes WINDOW w AS (PARTITION BY ticker,episode ORDER BY date);

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.signal_date, r.atr_pct_14_at_entry AS atr, r.pct_up_at_entry AS d1,
       r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       CASE WHEN l.c3>0 THEN r.entry_price/l.c3-1.0 END AS chg_3d,
       CASE WHEN l.c1>0 AND l.c3>0 THEN l.c1/l.c3-1.0 END AS true_p2d
FROM read_csv_auto('/tmp/tide_atr.csv') r
LEFT JOIN lags l ON l.ticker=r.symbol AND l.date=r.signal_date;

-- the two ATR cohorts
CREATE OR REPLACE TEMP TABLE lo AS SELECT * FROM t WHERE atr>=0.08 AND atr<0.10;   -- weak bucket
CREATE OR REPLACE TEMP TABLE hi AS SELECT * FROM t WHERE atr>=0.10 AND atr<0.25;   -- strong bucket

CREATE OR REPLACE TEMP MACRO pf(tbl,cond) AS TABLE
SELECT COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM query_table(tbl) WHERE cond;

.mode box
SELECT '=== baseline: the two ATR cohorts ===' z;
SELECT '[0.08,0.10) weak' g,* FROM pf('lo', true);
SELECT '[0.10,0.25) strong' g,* FROM pf('hi', true);

-- Does deepening 1d help the weak bucket? (default ceiling is -5%)
SELECT '=== [0.08,0.10): 1d depth ===' z;
SELECT '1d<=-5 (all)' g,* FROM pf('lo', d1<=-0.05);
SELECT '1d<=-8'       g,* FROM pf('lo', d1<=-0.08);
SELECT '1d<=-12'      g,* FROM pf('lo', d1<=-0.12);
SELECT '1d<=-18'      g,* FROM pf('lo', d1<=-0.18);

-- Does deepening 3d help? (default ceiling -15%)
SELECT '=== [0.08,0.10): 3d depth ===' z;
SELECT '3d<=-15 (all)' g,* FROM pf('lo', chg_3d<=-0.15);
SELECT '3d<=-20'       g,* FROM pf('lo', chg_3d<=-0.20);
SELECT '3d<=-30'       g,* FROM pf('lo', chg_3d<=-0.30);

-- Does deepening true-prior2d help? (default ceiling -10%)
SELECT '=== [0.08,0.10): true-prior2d depth ===' z;
SELECT 'p2d<=-10 (all)' g,* FROM pf('lo', true_p2d<=-0.10);
SELECT 'p2d<=-15'       g,* FROM pf('lo', true_p2d<=-0.15);
SELECT 'p2d<=-20'       g,* FROM pf('lo', true_p2d<=-0.20);

-- Best combined deepening on the weak bucket vs leaving it out entirely
SELECT '=== [0.08,0.10): combined deepening ===' z;
SELECT 'p2d<=-15 & 3d<=-25'   g,* FROM pf('lo', true_p2d<=-0.15 AND chg_3d<=-0.25);
SELECT 'p2d<=-15 & 1d<=-12'   g,* FROM pf('lo', true_p2d<=-0.15 AND d1<=-0.12);

-- For contrast: does the SAME deepening help the strong bucket too (i.e. is depth ATR-independent)?
SELECT '=== [0.10,0.25): same deepening (is depth orthogonal to ATR?) ===' z;
SELECT 'p2d<=-10 (all)' g,* FROM pf('hi', true_p2d<=-0.10);
SELECT 'p2d<=-15'       g,* FROM pf('hi', true_p2d<=-0.15);
SELECT 'p2d<=-20'       g,* FROM pf('hi', true_p2d<=-0.20);
