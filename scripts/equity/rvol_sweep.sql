CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_rvol_wide.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.* FROM raw JOIN br ON br.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01'
  AND raw.atr_pct_14_at_entry<0.11 AND raw.tightness_14_at_entry<4.5
  AND raw.pct_up_at_entry>=0.10;        -- move floor held at 10%

.mode box
SELECT '== NON-CUMULATIVE rvol bands (move>=10, caps on) ==' z;
SELECT CASE WHEN rvol_at_entry<2 THEN '01: 1-2' WHEN rvol_at_entry<3 THEN '02: 2-3'
            WHEN rvol_at_entry<4 THEN '03: 3-4' WHEN rvol_at_entry<5 THEN '04: 4-5'
            WHEN rvol_at_entry<6 THEN '05: 5-6' WHEN rvol_at_entry<8 THEN '06: 6-8'
            WHEN rvol_at_entry<10 THEN '07: 8-10' WHEN rvol_at_entry<12 THEN '08: 10-12'
            WHEN rvol_at_entry<15 THEN '09: 12-15' ELSE '10: 15+' END AS band,
  COUNT(*) n, ROUND(AVG(net_pnl),0) mean_pnl,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t GROUP BY 1 ORDER BY 1;

CREATE OR REPLACE TEMP MACRO floor(f) AS TABLE
SELECT COUNT(*) n, ROUND(AVG(net_pnl),0) mean_pnl, ROUND(SUM(net_pnl),0) tot,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t WHERE rvol_at_entry >= f;
SELECT '== CUMULATIVE rvol floor ==' z;
SELECT 'rvol>=1' g, * FROM floor(1);
SELECT 'rvol>=2' g, * FROM floor(2);
SELECT 'rvol>=3' g, * FROM floor(3);
SELECT 'rvol>=4' g, * FROM floor(4);
SELECT 'rvol>=5' g, * FROM floor(5);
SELECT 'rvol>=6' g, * FROM floor(6);
SELECT 'rvol>=8' g, * FROM floor(8);
SELECT 'rvol>=10' g, * FROM floor(10);
SELECT 'rvol>=15' g, * FROM floor(15);
SELECT '== rvol BAND gates vs production (move>=10, caps on) ==' z;
CREATE OR REPLACE TEMP MACRO bandgate(lo, hi) AS TABLE
SELECT COUNT(*) n, ROUND(SUM(net_pnl),0) tot,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date<DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_pre,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM t WHERE rvol_at_entry>=lo AND rvol_at_entry<hi;
SELECT 'rvol[6,20] (CURRENT)' g, * FROM bandgate(6,20);
SELECT 'rvol[5,15]' g, * FROM bandgate(5,15);
SELECT 'rvol[5,12]' g, * FROM bandgate(5,12);
SELECT 'rvol[6,15]' g, * FROM bandgate(6,15);
SELECT 'rvol[3,15]' g, * FROM bandgate(3,15);
