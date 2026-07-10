-- MaxFlyerV3: post-hoc breakdown of the arm-window (bars-since-signal) on the effectively-infinite-timer run.
-- One engine run (--ema-arm-bars 100000) lets every signal that EVER crosses under fire; we slice the lag here.
-- lag = entry_time - signal_time (minutes) = how many bars after the signal the 9-EMA finally rolled under.
-- A-book (brv20d>=100 & ATR%>=0.03), 2020+, raw PF only (short win bounded +100%).
-- Run: duckdb -readonly data/trading.db < scripts/equity/mfv3_armbars_breakdown.sql

CREATE OR REPLACE TEMP TABLE e AS
SELECT r.symbol, r.trade_date, r.ret_moc, r.net_pnl, r.exit_reason,
       date_diff('minute', r.signal_time, r.entry_time) AS lag_min
FROM read_csv_auto('/tmp/mfv3_arminf.csv') r
JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0
  AND r.breakout_bar_vol / NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0) >= 100;

.mode box
SELECT '=== lag distribution (bars from signal to the 9-EMA cross-under entry) ===' z;
SELECT COUNT(*) n,
  ROUND(AVG(lag_min),1) mean, quantile_cont(lag_min,0.5) med,
  quantile_cont(lag_min,0.9) p90, quantile_cont(lag_min,0.99) p99, MAX(lag_min) max
FROM e;

SELECT '=== PER-BAND: lag bucket (non-cumulative) ===' z;
WITH b(lo,hi,lbl) AS (VALUES
  (1,10,'1-10'),(11,20,'11-20'),(21,30,'21-30'),(31,40,'31-40'),(41,60,'41-60'),
  (61,90,'61-90'),(91,120,'91-120'),(121,100000,'>120'))
SELECT b.lbl AS lag_band, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN -ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),2) raw_pf,
  ROUND(100.0*AVG(-ret_moc),2) avg_pct,
  ROUND(SUM(net_pnl),0) net, ROUND(MIN(net_pnl),0) worst
FROM e, b WHERE lag_min>=b.lo AND lag_min<=b.hi
GROUP BY b.lo,b.hi,b.lbl ORDER BY b.lo;

SELECT '=== CUMULATIVE: arm window <= N bars (this is the timer-length sweep, done post-hoc) ===' z;
WITH f(x) AS (VALUES (10),(20),(30),(40),(60),(90),(120),(100000))
SELECT CASE WHEN f.x>=100000 THEN 'any (inf)' ELSE printf('lag <= %d', f.x) END AS arm_window,
  COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN -ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),2) raw_pf,
  ROUND(100.0*AVG(-ret_moc),2) avg_pct,
  ROUND(SUM(net_pnl),0) net, ROUND(MIN(net_pnl),0) worst
FROM e, f WHERE lag_min<=f.x GROUP BY f.x ORDER BY f.x;
