-- TideFlyer — is the 60d POINT return the best washout-depth reference, or is a longer window / a
-- MOVING AVERAGE smoother & better? A single-point 60d return depends entirely on where price was
-- exactly 60 bars ago (noisy). An MA of the close is a smoother "where has this name been trading".
-- Test on the pre-washout-depth book /tmp/tide_pre60.csv (all other gates ON, 22,881 / PF 1.890).
--
-- References (all = entry_close / REF - 1, so more-negative = deeper washout):
--   pt60/pt90/pt120  = point return: entry / close[t-N] - 1
--   ma60/ma90/ma120  = MA depth:     entry / mean(close over prior N bars) - 1
-- For each, sweep a CEILING (keep depth <= X) and report the book PF at matched trip counts.
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_washout_ref.sql

-- per (ticker, episode) lagged closes + trailing means, computed once
CREATE OR REPLACE TEMP TABLE refs AS
SELECT ticker, date, adj_close,
       LAG(adj_close,60)  OVER w AS c60,
       LAG(adj_close,90)  OVER w AS c90,
       LAG(adj_close,120) OVER w AS c120,
       -- trailing means EXCLUDING today (prior-window convention): rows 1..N preceding
       AVG(adj_close) OVER (PARTITION BY ticker,episode ORDER BY date ROWS BETWEEN 60  PRECEDING AND 1 PRECEDING) AS m60,
       AVG(adj_close) OVER (PARTITION BY ticker,episode ORDER BY date ROWS BETWEEN 90  PRECEDING AND 1 PRECEDING) AS m90,
       AVG(adj_close) OVER (PARTITION BY ticker,episode ORDER BY date ROWS BETWEEN 120 PRECEDING AND 1 PRECEDING) AS m120,
       COUNT(*)       OVER (PARTITION BY ticker,episode ORDER BY date ROWS BETWEEN 120 PRECEDING AND 1 PRECEDING) AS nbars120
FROM daily_episodes WINDOW w AS (PARTITION BY ticker,episode ORDER BY date);

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       CASE WHEN x.c60 >0 THEN r.entry_price/x.c60 -1.0 END AS pt60,
       CASE WHEN x.c90 >0 THEN r.entry_price/x.c90 -1.0 END AS pt90,
       CASE WHEN x.c120>0 THEN r.entry_price/x.c120-1.0 END AS pt120,
       CASE WHEN x.m60 >0 THEN r.entry_price/x.m60 -1.0 END AS ma60,
       CASE WHEN x.m90 >0 AND x.nbars120>=90  THEN r.entry_price/x.m90 -1.0 END AS ma90,
       CASE WHEN x.m120>0 AND x.nbars120>=120 THEN r.entry_price/x.m120-1.0 END AS ma120
FROM read_csv_auto('/tmp/tide_pre60.csv') r
LEFT JOIN refs x ON x.ticker=r.symbol AND x.date=r.signal_date;

CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t WHERE cond;

.mode box
SELECT '=== baseline (pre-washout-depth) + coverage ===' z;
SELECT COUNT(*) n, COUNT(pt60) n_pt60, COUNT(ma60) n_ma60, COUNT(ma90) n_ma90, COUNT(ma120) n_ma120,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf FROM t;

-- The production cut is pt60<=-0.40 (~13k trips). Find the MATCHED-trip-count ceiling for each ref,
-- so we compare references at the SAME selectivity, not the same nominal threshold.
SELECT '=== A) pt60 <= -0.40 (PRODUCTION reference) ===' z;
SELECT 'pt60<=-40' g,* FROM pf(pt60<=-0.40);

SELECT '=== B) POINT-return alternatives, swept ===' z;
SELECT 'pt90<=-40'  g,* FROM pf(pt90<=-0.40);
SELECT 'pt90<=-50'  g,* FROM pf(pt90<=-0.50);
SELECT 'pt120<=-40' g,* FROM pf(pt120<=-0.40);
SELECT 'pt120<=-50' g,* FROM pf(pt120<=-0.50);

SELECT '=== C) MA-DEPTH references (close vs N-day mean close), swept ===' z;
SELECT 'ma60<=-30'  g,* FROM pf(ma60<=-0.30);
SELECT 'ma60<=-35'  g,* FROM pf(ma60<=-0.35);
SELECT 'ma60<=-40'  g,* FROM pf(ma60<=-0.40);
SELECT 'ma90<=-35'  g,* FROM pf(ma90<=-0.35);
SELECT 'ma90<=-40'  g,* FROM pf(ma90<=-0.40);
SELECT 'ma120<=-35' g,* FROM pf(ma120<=-0.35);
SELECT 'ma120<=-40' g,* FROM pf(ma120<=-0.40);

-- Matched-selectivity head-to-head: for each ref find the ceiling giving ~13,100 trips (== production).
SELECT '=== D) MATCHED ~13.1k trips: which reference has the best PF at equal selectivity? ===' z;
SELECT 'pt60'  g,* FROM pf(pt60  <= (SELECT quantile_cont(pt60, 13122.0/(SELECT COUNT(pt60) FROM t)) FROM t WHERE pt60 IS NOT NULL));
SELECT 'pt90'  g,* FROM pf(pt90  <= (SELECT quantile_cont(pt90, 13122.0/(SELECT COUNT(pt90) FROM t)) FROM t WHERE pt90 IS NOT NULL));
SELECT 'pt120' g,* FROM pf(pt120 <= (SELECT quantile_cont(pt120,13122.0/(SELECT COUNT(pt120) FROM t)) FROM t WHERE pt120 IS NOT NULL));
SELECT 'ma60'  g,* FROM pf(ma60  <= (SELECT quantile_cont(ma60, 13122.0/(SELECT COUNT(ma60) FROM t)) FROM t WHERE ma60 IS NOT NULL));
SELECT 'ma90'  g,* FROM pf(ma90  <= (SELECT quantile_cont(ma90, 13122.0/(SELECT COUNT(ma90) FROM t)) FROM t WHERE ma90 IS NOT NULL));
SELECT 'ma120' g,* FROM pf(ma120 <= (SELECT quantile_cont(ma120,13122.0/(SELECT COUNT(ma120) FROM t)) FROM t WHERE ma120 IS NOT NULL));
