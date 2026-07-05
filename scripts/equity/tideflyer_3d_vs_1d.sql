-- TideFlyer — does requiring 3d-return < 1d-return help? (i.e. already sliding into today's flush)
--   3d < 1d  => the prior 2 days ALSO fell (today continues an existing multi-day decline)
--   3d >= 1d => today's 1d drop is the worst of the window (was flat/up, cracked today)
-- Tested on the production population: base book (1d in [-40,-5], volfrac[0.5,1.5]) + 3d<=-15% gate.
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_3d_vs_1d.sql

CREATE OR REPLACE TEMP TABLE lags AS
SELECT ticker, date, adj_close, LAG(adj_close, 3) OVER w AS c3
FROM daily_episodes WINDOW w AS (PARTITION BY ticker, episode ORDER BY date);

CREATE OR REPLACE TEMP TABLE b AS
SELECT r.symbol, r.signal_date, r.pct_up_at_entry AS d1,
       r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       CASE WHEN l.c3 > 0 THEN r.entry_price/l.c3 - 1.0 END AS chg_3d
FROM read_csv_auto('/tmp/tide_base.csv') r
LEFT JOIN lags l ON l.ticker = r.symbol AND l.date = r.signal_date;

CREATE OR REPLACE TEMP MACRO pf(cond) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM b WHERE cond;

.mode box
-- production book for reference
SELECT '=== production book: 3d<=-15% (any 3d-vs-1d) ===' z;
SELECT 'all' lbl, * FROM pf(chg_3d <= -0.15);

SELECT '=== SPLIT by 3d < 1d, WITHIN the 3d<=-15% gate ===' z;
SELECT '3d <  1d  (already sliding into today)' lbl, * FROM pf(chg_3d <= -0.15 AND chg_3d <  d1);
SELECT '3d >= 1d  (today is the worst of the 3d)' lbl, * FROM pf(chg_3d <= -0.15 AND chg_3d >= d1);

-- how much does "already sliding" overlap with just "deeper 3d"? show the gap magnitude
SELECT '=== the same split, but NO 3d floor (whole base book) ===' z;
SELECT 'all base'                       lbl, * FROM pf(chg_3d IS NOT NULL);
SELECT '3d <  1d (already sliding)'      lbl, * FROM pf(chg_3d <  d1);
SELECT '3d >= 1d (cracked today)'        lbl, * FROM pf(chg_3d >= d1);

-- magnitude of the "extra fall" (3d minus today's 1d = the prior-2-day contribution) as a lever
SELECT '=== prior-2d fall = (3d - 1d) bands, within 3d<=-15% ===' z;
WITH pb(lo,hi,lbl) AS (VALUES
  (-1e9,-0.20,'prior2d < -20%'),(-0.20,-0.10,'-20..-10%'),(-0.10,-0.02,'-10..-2%'),
  (-0.02,0.02,'~flat +-2%'),(0.02,0.10,'+2..+10%'),(0.10,1e9,'> +10% (bounced then flushed)'))
SELECT pb.lbl prior2d_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN b.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN b.pnl>0 THEN b.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN b.pnl<0 THEN b.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(b.ret),3) avg_pct
FROM b, pb WHERE b.chg_3d <= -0.15 AND (b.chg_3d - b.d1) >= pb.lo AND (b.chg_3d - b.d1) < pb.hi
GROUP BY pb.lo,pb.hi,pb.lbl ORDER BY pb.lo;
