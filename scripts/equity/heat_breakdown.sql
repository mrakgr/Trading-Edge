ATTACH 'data/trading.db' AS db (READ_ONLY);
-- heat with 30-CALENDAR-DAY ADV >= $100k (convention), CS/ADRC not required for heat (top gainers can be any liquid name) — keep heat universe = liquid by 30cal ADV
CREATE OR REPLACE TEMP TABLE heat AS
WITH r AS (
  SELECT ticker, date,
    adj_close/LAG(adj_close) OVER (PARTITION BY ticker ORDER BY date) - 1.0 AS ret,
    AVG(adj_close*adj_volume) OVER (PARTITION BY ticker ORDER BY date RANGE BETWEEN INTERVAL 30 DAYS PRECEDING AND CURRENT ROW) AS adv30
  FROM db.split_adjusted_prices WHERE adj_close > 0
),
q AS (SELECT date, ret FROM r WHERE adv30 >= 100000 AND ret IS NOT NULL AND ret <= 10.0),
ranked AS (SELECT date, ret, PERCENT_RANK() OVER (PARTITION BY date ORDER BY ret) pr FROM q)
SELECT date, AVG(ret) AS heat FROM ranked WHERE pr >= 0.99 GROUP BY date;

CREATE OR REPLACE TEMP TABLE heat_ma AS
SELECT date, AVG(heat) OVER (ORDER BY date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) AS h10 FROM heat;

CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v2_default_B.csv') WHERE open=0),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet')
SELECT raw.net_pnl, raw.entry_date, (raw.exit_price/raw.entry_price-1.0) ret, hm.h10
FROM raw JOIN br ON br.date=raw.entry_date JOIN heat_ma hm ON hm.date=raw.entry_date
WHERE br.b_lag1>0.5 AND raw.entry_date>=DATE '2005-01-01';

.mode box
SELECT '== heat (30-cal-ADV>=100k) p80 threshold + froth cut ==' z;
SELECT ROUND(100*quantile_cont(h10,0.8),2) h10_p80_pct FROM t WHERE h10 IS NOT NULL;
WITH q AS (SELECT *, NTILE(5) OVER (ORDER BY h10) quint FROM t WHERE h10 IS NOT NULL)
SELECT CASE WHEN quint<=4 THEN 'keep Q1-4' ELSE 'Q5 (excluded)' END grp, COUNT(*) n,
  ROUND(100*MEDIAN(ret),2) med, ROUND(100*AVG(ret),2) mean,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN entry_date>=DATE '2015-01-01' AND net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf_post
FROM q GROUP BY 1 ORDER BY 1;
SELECT '== corrected ladder (30-cal-ADV universe): heat-10d quintile boundaries + filter ==' z;
WITH q AS (SELECT *, NTILE(5) OVER (ORDER BY h10) quint FROM t WHERE h10 IS NOT NULL)
SELECT quint, COUNT(*) n, ROUND(100*MIN(h10),1) lo_pct, ROUND(100*MAX(h10),1) hi_pct,
  ROUND(100*MEDIAN(ret),2) med_ret, ROUND(100*AVG(ret),2) mean_ret
FROM q GROUP BY quint ORDER BY quint;
SELECT '== baseline vs keep<24% vs excluded ==' z;
SELECT 'baseline (all heat)' g, COUNT(*) n,
  ROUND(SUM(CASE WHEN net_pnl>0 THEN net_pnl ELSE 0 END)/NULLIF(-SUM(CASE WHEN net_pnl<0 THEN net_pnl ELSE 0 END),0),3) pf,
  ROUND(SUM(net_pnl),0) tot FROM t WHERE h10 IS NOT NULL;
