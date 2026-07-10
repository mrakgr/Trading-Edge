-- MaxFlyerV3: buffer × re-entry-CAP grid, tail measured by WORST DAY (the honest unit once re-entries stack
-- multiple losing legs on one pop/day). Per (buffer, cap): net, raw PF, worst single TRADE, worst SYMBOL-DAY
-- (sum of all legs for one symbol,date), worst CALENDAR-DAY (sum across all symbols on a date). A-book, 2020+,
-- max-conc 0. re_idx<=cap slices the re-entry cap post-hoc. Run: duckdb -readonly ... < this → results_days.csv
CREATE OR REPLACE TEMP MACRO cellday(path, buf, cap) AS TABLE
  WITH t AS (
    SELECT r.symbol, r.trade_date, r.ret_moc::DOUBLE ret_moc, r.net_pnl::DOUBLE net_pnl
    FROM read_csv_auto(path) r
    JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
    WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0
      AND r.breakout_bar_vol/NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0)>=100
      AND r.re_idx <= cap),
  symday AS (SELECT symbol, trade_date, SUM(net_pnl) sd_net FROM t GROUP BY symbol, trade_date),
  calday AS (SELECT trade_date, SUM(net_pnl) cd_net FROM t GROUP BY trade_date)
  SELECT buf AS buffer_pct, cap AS re_cap,
    (SELECT COUNT(*) FROM t) n,
    (SELECT ROUND(SUM(net_pnl)/1000.0,0) FROM t) net_k,
    (SELECT ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),2) FROM t) raw_pf,
    (SELECT ROUND(MIN(net_pnl),0) FROM t) worst_trade,
    (SELECT ROUND(MIN(sd_net),0) FROM symday) worst_symday,
    (SELECT ROUND(MIN(cd_net),0) FROM calday) worst_calday;

.mode csv
.headers on
COPY (
  SELECT * FROM cellday('/tmp/mfv3_grid2/b00.csv',0,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b00.csv',0,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b00.csv',0,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b00.csv',0,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b00.csv',0,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b00.csv',0,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b01.csv',1,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b01.csv',1,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b01.csv',1,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b01.csv',1,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b01.csv',1,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b01.csv',1,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b02.csv',2,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b02.csv',2,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b02.csv',2,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b02.csv',2,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b02.csv',2,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b02.csv',2,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b03.csv',3,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b03.csv',3,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b03.csv',3,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b03.csv',3,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b03.csv',3,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b03.csv',3,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b04.csv',4,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b04.csv',4,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b04.csv',4,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b04.csv',4,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b04.csv',4,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b04.csv',4,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b05.csv',5,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b05.csv',5,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b05.csv',5,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b05.csv',5,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b05.csv',5,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b05.csv',5,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b06.csv',6,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b06.csv',6,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b06.csv',6,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b06.csv',6,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b06.csv',6,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b06.csv',6,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b07.csv',7,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b07.csv',7,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b07.csv',7,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b07.csv',7,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b07.csv',7,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b07.csv',7,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b08.csv',8,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b08.csv',8,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b08.csv',8,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b08.csv',8,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b08.csv',8,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b08.csv',8,999)
  UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b09.csv',9,0) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b09.csv',9,1) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b09.csv',9,2) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b09.csv',9,3) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b09.csv',9,5) UNION ALL SELECT * FROM cellday('/tmp/mfv3_grid2/b09.csv',9,999)
) TO '/tmp/mfv3_grid2/results_days.csv' (HEADER, DELIMITER ',');
