-- TideFlyer — is a STRICTLY MONOTONE descent into the entry better than a choppy one?
-- Indexing: d0=entry close (today), d1=yesterday, d2=2 days ago, d3=3 days ago.
-- The gates already imply d3 > d1 > d0 (1d<=-5% => d1>d0; prior-2d<=-10% => d1<d3). The only free
-- close is d2. A strict staircase-down is d3 > d2 > d1 > d0, which (given d3>d1>d0) reduces to the
-- two conditions the user named: d2 < d3 (i.e. d3>d2, down from d3 to d2) AND d2 > d1 (down from d2 to d1).
-- Test each condition and their conjunction (= the clean monotone descent) vs the complements.
-- Population = production book /tmp/tide_breadth.csv (4,820 / PF 5.307). RAW PF.
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_monotone.sql

CREATE OR REPLACE TEMP TABLE lags AS
SELECT ticker, date, adj_close,
       LAG(adj_close,1) OVER w AS d1, LAG(adj_close,2) OVER w AS d2, LAG(adj_close,3) OVER w AS d3
FROM daily_episodes WINDOW w AS (PARTITION BY ticker,episode ORDER BY date);

CREATE OR REPLACE TEMP TABLE t AS
SELECT r.symbol, r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       r.entry_price AS d0, l.d1, l.d2, l.d3
FROM read_csv_auto('/tmp/tide_breadth.csv') r
LEFT JOIN lags l ON l.ticker=r.symbol AND l.date=r.signal_date
WHERE l.d1 IS NOT NULL AND l.d2 IS NOT NULL AND l.d3 IS NOT NULL;

CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n, ROUND(100.0*COUNT(*)/(SELECT COUNT(*) FROM t),1) pct,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM t WHERE cond;

.mode box
SELECT '=== sanity: confirm the gates imply d3>d1>d0 ===' z;
SELECT ROUND(100.0*AVG(CASE WHEN d3>d1 THEN 1 ELSE 0 END),1) pct_d3_gt_d1,
       ROUND(100.0*AVG(CASE WHEN d1>d0 THEN 1 ELSE 0 END),1) pct_d1_gt_d0 FROM t;

SELECT '=== A) the two conditions individually ===' z;
SELECT 'd3 > d2 (down d3->d2)' g,* FROM pf(d3 > d2);
SELECT 'd3 <= d2 (up/flat d3->d2)' g,* FROM pf(d3 <= d2);
SELECT 'd2 > d1 (down d2->d1)' g,* FROM pf(d2 > d1);
SELECT 'd2 <= d1 (up/flat d2->d1)' g,* FROM pf(d2 <= d1);

SELECT '=== B) the conjunction = STRICT monotone descent d3>d2>d1>d0 ===' z;
SELECT 'MONOTONE  (d3>d2>d1)'   g,* FROM pf(d3>d2 AND d2>d1);
SELECT 'NON-mono  (else)'       g,* FROM pf(NOT (d3>d2 AND d2>d1));

SELECT '=== C) the 4 quadrants of d2 placement ===' z;
SELECT 'd3>d2 & d2>d1  (staircase)'      g,* FROM pf(d3>d2 AND d2>d1);
SELECT 'd3>d2 & d2<=d1 (d2 dipped below d1 then up)' g,* FROM pf(d3>d2 AND d2<=d1);
SELECT 'd3<=d2 & d2>d1 (d2 spiked above d3 then down)' g,* FROM pf(d3<=d2 AND d2>d1);
SELECT 'd3<=d2 & d2<=d1 (choppy)'        g,* FROM pf(d3<=d2 AND d2<=d1);

SELECT '=== D) baseline ===' z;
SELECT 'all (3-lag coverage)' g,* FROM pf(true);
