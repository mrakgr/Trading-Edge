-- MaxFlyerV3: bars-since-9EMA-high ENTRY threshold sweep. Enter on the FIRST weakness (barsSinceEmaHigh reaches
-- N) instead of the 9-EMA cross-under. A-book, 2020+, raw PF. Stop = roll30m/buf20 (F7 defaults). Compare vs the
-- F6/F7 cross-under book. NB: no arm-window slice here — the trigger fires at a fixed N-bar lag. Run: duckdb ...
CREATE OR REPLACE TEMP MACRO ab(path) AS TABLE
  SELECT r.ret_moc::DOUBLE AS ret_moc, r.net_pnl::DOUBLE AS net_pnl
  FROM read_csv_auto(path, types={'signal_time':'VARCHAR','entry_time':'VARCHAR'}) r
  JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
  WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0
    AND r.breakout_bar_vol/NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0)>=100;
CREATE OR REPLACE TEMP MACRO st(path) AS TABLE
  SELECT COUNT(*) n,
    ROUND(100.0*AVG(CASE WHEN -ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
    ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),2) raw_pf,
    ROUND(SUM(net_pnl),0) net, ROUND(MIN(net_pnl),0) worst, ROUND(100.0*MAX(ret_moc),0) worst_pct,
    SUM(CASE WHEN ret_moc>0.70 THEN 1 ELSE 0 END) n_gt70, SUM(CASE WHEN ret_moc>1.0 THEN 1 ELSE 0 END) n_gt100
  FROM ab(path);
.mode box
SELECT '=== bars-since-9EMA-high ENTRY threshold sweep (A-book, 2020+, roll30/buf20 stop) ===' z;
SELECT 'bsh=1' cfg, * FROM st('/tmp/mfv3_bsh_1.csv')
UNION ALL SELECT 'bsh=2', * FROM st('/tmp/mfv3_bsh_2.csv')
UNION ALL SELECT 'bsh=3', * FROM st('/tmp/mfv3_bsh_3.csv')
UNION ALL SELECT 'bsh=4', * FROM st('/tmp/mfv3_bsh_4.csv')
UNION ALL SELECT 'bsh=5', * FROM st('/tmp/mfv3_bsh_5.csv')
ORDER BY cfg;
