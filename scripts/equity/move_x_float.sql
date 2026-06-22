ATTACH 'data/equity/float/float.db' AS f (READ_ONLY);
CREATE OR REPLACE TEMP TABLE flt AS
SELECT tc.ticker, fs.known_date, fs.period_end, fs.value float_usd
FROM f.float_sec fs JOIN f.ticker_cik tc ON tc.cik=fs.cik WHERE fs.value>0;
CREATE OR REPLACE TEMP TABLE t AS
WITH raw AS (SELECT * FROM read_csv_auto('/tmp/v4_nomovecap.csv')),
br AS (SELECT date, LAG(pct_above_20) OVER (ORDER BY date) b_lag1 FROM 'data/equity/momentum_v0/breadth.parquet'),
hn AS (SELECT date, h10 FROM 'data/equity/momentum_v0/heat.parquet'),
wf AS (SELECT raw.symbol, raw.entry_date, raw.pct_up_at_entry mv, (raw.exit_price/raw.entry_price-1.0) ret, fl.float_usd, fl.period_end flt_pe
  FROM raw ASOF LEFT JOIN flt fl ON fl.ticker=raw.symbol AND fl.known_date<=raw.entry_date)
SELECT w.symbol, w.entry_date, w.mv, w.ret,
  CASE WHEN w.float_usd IS NOT NULL AND pe.adj_close>0 AND en.adj_close>0
       THEN w.float_usd*en.adj_close/pe.adj_close END float_usd_at_entry
FROM wf w JOIN br ON br.date=w.entry_date LEFT JOIN hn ON hn.date=w.entry_date
ASOF LEFT JOIN split_adjusted_prices pe ON pe.ticker=w.symbol AND pe.date<=w.flt_pe
LEFT JOIN split_adjusted_prices en ON en.ticker=w.symbol AND en.date=w.entry_date
WHERE br.b_lag1>0.5 AND w.entry_date>=DATE '2005-01-01' AND (hn.h10 IS NULL OR hn.h10<0.25);

-- helper: low float = covered AND < $300M ; high/none otherwise
SELECT '=== move band PF, ALL (covered+nodata), production pop ===' z;
SELECT CASE WHEN mv<0.30 THEN '1:10-30%' WHEN mv<0.50 THEN '2:30-50%' WHEN mv<0.80 THEN '3:50-80%' ELSE '4:80%+' END move_band,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t GROUP BY 1 ORDER BY 1;

SELECT '=== move band x FLOAT (low=<300M covered vs high=>=300M covered) ===' z;
SELECT
  CASE WHEN mv<0.30 THEN '1:10-30%' WHEN mv<0.50 THEN '2:30-50%' WHEN mv<0.80 THEN '3:50-80%' ELSE '4:80%+' END move_band,
  CASE WHEN float_usd_at_entry IS NULL THEN 'c:nodata'
       WHEN float_usd_at_entry<300e6 THEN 'a:LOW <300M' ELSE 'b:HIGH >=300M' END flt_grp,
  COUNT(*) n, ROUND(100.0*AVG(CASE WHEN ret>0 THEN 1 ELSE 0 END),1) win,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip
FROM t GROUP BY 1,2 ORDER BY 1,2;

-- RAW PF too (unclipped) for the 30%+ low-float cells, since the whole point is whether the
-- violent-move winners pay -- the clip would HIDE exactly the upside we're asking about.
SELECT '=== 30%+ move, low-float: RAW vs CLIP (does the violent upside pay?) ===' z;
SELECT
  CASE WHEN mv<0.30 THEN '1:10-30%' WHEN mv<0.50 THEN '2:30-50%' ELSE '3:50%+' END move_band,
  CASE WHEN float_usd_at_entry<300e6 THEN 'LOW <300M' ELSE 'HIGH/none' END flt_grp,
  COUNT(*) n,
  ROUND(SUM(CASE WHEN ret>0 THEN LEAST(ret,0.50) ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_clip,
  ROUND(SUM(CASE WHEN ret>0 THEN ret ELSE 0 END)/NULLIF(-SUM(CASE WHEN ret<0 THEN ret ELSE 0 END),0),3) pf_raw,
  ROUND(AVG(ret),3) avg_ret
FROM t WHERE float_usd_at_entry IS NOT NULL
GROUP BY 1,2 ORDER BY 1,2;
