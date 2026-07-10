-- MaxFlyerV3: max-EMA-stop ANCHOR comparison — session-max vs rolling-30m local max vs pure entry-EMA %-stop.
-- All A-book, 2020+, arm<=60 (post-hoc slice). Goal: cut the fat left tail (worst -153%) the SESSION anchor
-- allows when we short a later/lower pop below an early session EMA spike. raw PF. Run: duckdb -readonly ... < this.
CREATE OR REPLACE TEMP MACRO ab(path) AS TABLE
  SELECT r.ret_moc, r.net_pnl, r.exit_reason
  FROM read_csv_auto(path) r
  JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
  WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0
    AND r.breakout_bar_vol/NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0)>=100
    AND date_diff('minute', r.signal_time, r.entry_time) <= 60;
CREATE OR REPLACE TEMP MACRO st(path) AS TABLE
  SELECT COUNT(*) n,
    ROUND(100.0*AVG(CASE WHEN -ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
    ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),2) raw_pf,
    ROUND(SUM(net_pnl),0) net, ROUND(MIN(net_pnl),0) worst, ROUND(100.0*MAX(ret_moc),0) worst_pct,
    SUM(CASE WHEN ret_moc>0.70 THEN 1 ELSE 0 END) n_gt70, SUM(CASE WHEN ret_moc>1.0 THEN 1 ELSE 0 END) n_gt100
  FROM ab(path);
.mode box
SELECT '=== anchor comparison (A-book, arm<=60, 2020+) ===' z;
SELECT 'session-max buf30 (F6)' cfg, * FROM st('/tmp/mfv3_arminf.csv')
UNION ALL SELECT 'roll30m buf20', * FROM st('/tmp/mfv3_roll30_0p20.csv')
UNION ALL SELECT 'roll30m buf30', * FROM st('/tmp/mfv3_roll30_0p30.csv')
UNION ALL SELECT 'entry-EMA pct70 (ref)', * FROM st('/tmp/mfv3_pct_0p70.csv');
