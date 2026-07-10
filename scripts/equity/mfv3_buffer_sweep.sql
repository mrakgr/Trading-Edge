-- MaxFlyerV3: max-EMA-stop BUFFER sweep (A-book, 2020+). buffer = fraction the stop sits ABOVE the session-max
-- 9-EMA (0.10 = stop 10% above). Looser buffer = fewer stop-outs = more net held but a wider tail. raw PF only
-- (short win bounded +100%). One SELECT per buffer CSV, UNION'd. Run: duckdb -readonly data/trading.db < this.
CREATE OR REPLACE TEMP MACRO abook(path) AS TABLE
  SELECT r.ret_moc, r.net_pnl, r.exit_reason
  FROM read_csv_auto(path) r
  JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
  WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0
    AND r.breakout_bar_vol / NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0) >= 100;

CREATE OR REPLACE TEMP MACRO stats(path) AS TABLE
  SELECT COUNT(*) n,
    ROUND(100.0*AVG(CASE WHEN -ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
    ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),3) raw_pf,
    ROUND(100.0*AVG(-ret_moc),2) avg_pct,
    ROUND(SUM(net_pnl),0) net,
    ROUND(MIN(net_pnl),0) worst,
    SUM(CASE WHEN exit_reason='ema_max_stop' THEN 1 ELSE 0 END) n_stopped
  FROM abook(path);

.mode box
SELECT '=== max-EMA-stop BUFFER sweep (A-book, 2020+) ===' z;
SELECT '0%  (at max)' buffer, * FROM stats('/tmp/mfv3_fbuf_0p0.csv')
UNION ALL SELECT '10% above', * FROM stats('/tmp/mfv3_fbuf_0p10.csv')
UNION ALL SELECT '20% above', * FROM stats('/tmp/mfv3_fbuf_0p20.csv')
UNION ALL SELECT '30% above', * FROM stats('/tmp/mfv3_fbuf_0p30.csv')
UNION ALL SELECT '40% above', * FROM stats('/tmp/mfv3_fbuf_0p40.csv');
