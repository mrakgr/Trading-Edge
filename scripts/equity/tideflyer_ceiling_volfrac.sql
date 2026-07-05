-- TideFlyer — two questions on /tmp/tide_base.csv (long-MR, 1d in [-40,-5], volfrac[0.5,1.5], 5d stop, PRE-3d-gate ~355k):
--   Q1: does moving the 1d DOWN-day ceiling from <=-5% to <=-10% help PF? (both raw and under the 3d<=-15% gate)
--   Q2: does the volfrac[0.5,1.5] band still MOVE PF once the 3d<=-15% gate is on? (overlap hypothesis)
-- The base CSV already has volfrac in [0.5,1.5] baked in (it was an engine default when generated),
-- so to test volfrac we must go back to the WIDER population /tmp/tide_low_5pct.csv (1d<=-5% only, no volfrac, no -40 floor).
-- Run: duckdb -readonly data/trading.db < scripts/equity/tideflyer_ceiling_volfrac.sql

CREATE OR REPLACE TEMP TABLE lags AS
SELECT ticker, date, adj_close, LAG(adj_close, 3) OVER w AS c3
FROM daily_episodes WINDOW w AS (PARTITION BY ticker, episode ORDER BY date);

-- === Q1: loss ceiling, on the base book (already 1d>=-40, volfrac[0.5,1.5]) ===
CREATE OR REPLACE TEMP TABLE b AS
SELECT r.symbol, r.signal_date, r.pct_up_at_entry AS d1, r.vol_max_7d_at_entry AS vmax,
       r.entry_adj_volume AS evol, r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
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
SELECT '=== Q1: 1d loss-ceiling on the BASE book (no 3d gate) ===' z;
SELECT '1d<=-5% (current default)' lbl, * FROM pf(d1 <= -0.05);
SELECT '1d<=-8%'                  lbl, * FROM pf(d1 <= -0.08);
SELECT '1d<=-10% (proposed)'      lbl, * FROM pf(d1 <= -0.10);
SELECT '1d<=-12%'                 lbl, * FROM pf(d1 <= -0.12);
SELECT '1d<=-15%'                 lbl, * FROM pf(d1 <= -0.15);

SELECT '=== Q1b: same ceilings, WITH the 3d<=-15% gate (production) ===' z;
SELECT '1d<=-5%  & 3d<=-15%' lbl, * FROM pf(d1 <= -0.05 AND chg_3d <= -0.15);
SELECT '1d<=-8%  & 3d<=-15%' lbl, * FROM pf(d1 <= -0.08 AND chg_3d <= -0.15);
SELECT '1d<=-10% & 3d<=-15%' lbl, * FROM pf(d1 <= -0.10 AND chg_3d <= -0.15);
SELECT '1d<=-12% & 3d<=-15%' lbl, * FROM pf(d1 <= -0.12 AND chg_3d <= -0.15);
SELECT '1d<=-15% & 3d<=-15%' lbl, * FROM pf(d1 <= -0.15 AND chg_3d <= -0.15);

-- === Q2: does volfrac still move PF once 3d<=-15% is on? Use the WIDER low_5pct book (no volfrac baked in) ===
CREATE OR REPLACE TEMP TABLE w AS
SELECT r.symbol, r.signal_date, r.pct_up_at_entry AS d1,
       r.entry_adj_volume / NULLIF(r.vol_max_7d_at_entry,0) AS volfrac,
       r.net_pnl AS pnl, r.exit_price/NULLIF(r.entry_price,0)-1.0 AS ret,
       CASE WHEN l.c3 > 0 THEN r.entry_price/l.c3 - 1.0 END AS chg_3d
FROM read_csv_auto('/tmp/tide_low_5pct.csv') r
LEFT JOIN lags l ON l.ticker = r.symbol AND l.date = r.signal_date;

CREATE OR REPLACE TEMP MACRO pfw(cond) AS TABLE
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN pnl>0 THEN pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN pnl<0 THEN pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(ret),3) avg_pct
FROM w WHERE cond;

-- baseline population for Q2 = 1d in [-40,-5] (match the base book's 1d band), 3d<=-15%
SELECT '=== Q2: volfrac lever BEFORE vs AFTER the 3d gate (1d in [-40,-5]) ===' z;
SELECT 'NO 3d gate, any volfrac'        lbl, * FROM pfw(d1<=-0.05 AND d1>=-0.40);
SELECT 'NO 3d gate, volfrac[0.5,1.5]'   lbl, * FROM pfw(d1<=-0.05 AND d1>=-0.40 AND volfrac>=0.5 AND volfrac<=1.5);
SELECT '3d<=-15%, any volfrac'          lbl, * FROM pfw(d1<=-0.05 AND d1>=-0.40 AND chg_3d<=-0.15);
SELECT '3d<=-15%, volfrac[0.5,1.5]'     lbl, * FROM pfw(d1<=-0.05 AND d1>=-0.40 AND chg_3d<=-0.15 AND volfrac>=0.5 AND volfrac<=1.5);

-- finer volfrac breakdown UNDER the 3d gate, to see if the inverted-U survived
SELECT '=== Q2b: volfrac bands UNDER the 3d<=-15% gate ===' z;
WITH vb(lo,hi,lbl) AS (VALUES
  (0.0,0.5,'0-0.5'),(0.5,0.8,'0.5-0.8'),(0.8,1.0,'0.8-1.0'),
  (1.0,1.5,'1.0-1.5'),(1.5,2.5,'1.5-2.5'),(2.5,1e9,'>=2.5'))
SELECT vb.lbl volfrac_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN w.ret>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN w.pnl>0 THEN w.pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN w.pnl<0 THEN w.pnl ELSE 0 END),0),3) pf,
  ROUND(100.0*AVG(w.ret),3) avg_pct
FROM w, vb WHERE w.d1<=-0.05 AND w.d1>=-0.40 AND w.chg_3d<=-0.15 AND w.volfrac>=vb.lo AND w.volfrac<vb.hi
GROUP BY vb.lo,vb.hi,vb.lbl ORDER BY vb.lo;
