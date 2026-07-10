-- MaxFlyerV3: pure 9-EMA %-STOP (off the ENTRY 9-EMA) vs the session-max-anchored stop. A-book, 2020+, arm<=60
-- (sliced post-hoc from the infinite-arm runs). raw PF. Goal: push down the fat left tail (the -153% MGIH loser)
-- that the session-max anchor allows, keep the 60-70% losses. Run: duckdb -readonly data/trading.db < this.
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
    ROUND(SUM(net_pnl),0) net, ROUND(MIN(net_pnl),0) worst,
    ROUND(100.0*MAX(ret_moc),0) worst_pct,
    SUM(CASE WHEN ret_moc>0.70 THEN 1 ELSE 0 END) n_gt70, SUM(CASE WHEN ret_moc>1.0 THEN 1 ELSE 0 END) n_gt100
  FROM ab(path);

.mode box
SELECT '=== stop comparison (A-book, arm<=60, 2020+) ===' z;
SELECT 'session-max buf30% (F6)' stop, * FROM st('/tmp/mfv3_arminf.csv')
UNION ALL SELECT 'ema-pct 60%', * FROM st('/tmp/mfv3_pct_0p60.csv')
UNION ALL SELECT 'ema-pct 70%', * FROM st('/tmp/mfv3_pct_0p70.csv')
UNION ALL SELECT 'ema-pct 80%', * FROM st('/tmp/mfv3_pct_0p80.csv')
UNION ALL SELECT 'ema-pct 100%', * FROM st('/tmp/mfv3_pct_1p00.csv');
