-- MaxFlyerV3: buffer × re-entry-CAP grid, from 10 runs (buffer 0-9%, unlimited re-entries) sliced POST-HOC by
-- re_idx <= cap. A-book, 2020+, max-conc 0, down-tick, roll30. re_idx 0=original, 1=1st re-entry, … raw PF.
-- One row per (buffer, cap). Run: duckdb -readonly data/trading.db < this  → writes /tmp/mfv3_grid2/results.csv
CREATE OR REPLACE TEMP MACRO cellcap(path, buf, cap) AS TABLE
  SELECT buf AS buffer_pct, cap AS re_cap,
    COUNT(*) n,
    ROUND(100.0*AVG(CASE WHEN -r.ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
    ROUND(SUM(CASE WHEN -r.ret_moc>0 THEN -r.ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -r.ret_moc<0 THEN -r.ret_moc ELSE 0 END),0),2) raw_pf,
    ROUND(SUM(r.net_pnl)/1000.0,0) net_k,
    ROUND(100.0*MAX(r.ret_moc),0) worst_pct,
    SUM(CASE WHEN r.ret_moc>0.5 THEN 1 ELSE 0 END) n_gt50
  FROM read_csv_auto(path) r
  JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
  WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0
    AND r.breakout_bar_vol/NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0)>=100
    AND r.re_idx <= cap;

.mode csv
.headers on
COPY (
  WITH bufs(b, path) AS (VALUES
    (0,'/tmp/mfv3_grid2/b00.csv'),(1,'/tmp/mfv3_grid2/b01.csv'),(2,'/tmp/mfv3_grid2/b02.csv'),
    (3,'/tmp/mfv3_grid2/b03.csv'),(4,'/tmp/mfv3_grid2/b04.csv'),(5,'/tmp/mfv3_grid2/b05.csv'),
    (6,'/tmp/mfv3_grid2/b06.csv'),(7,'/tmp/mfv3_grid2/b07.csv'),(8,'/tmp/mfv3_grid2/b08.csv'),
    (9,'/tmp/mfv3_grid2/b09.csv'))
  -- unrolled per buffer file (macro needs a literal path)
  SELECT * FROM cellcap('/tmp/mfv3_grid2/b00.csv',0,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b00.csv',0,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b00.csv',0,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b00.csv',0,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b00.csv',0,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b00.csv',0,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b00.csv',0,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b01.csv',1,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b01.csv',1,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b01.csv',1,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b01.csv',1,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b01.csv',1,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b01.csv',1,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b01.csv',1,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b02.csv',2,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b02.csv',2,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b02.csv',2,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b02.csv',2,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b02.csv',2,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b02.csv',2,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b02.csv',2,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b03.csv',3,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b03.csv',3,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b03.csv',3,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b03.csv',3,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b03.csv',3,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b03.csv',3,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b03.csv',3,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b04.csv',4,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b04.csv',4,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b04.csv',4,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b04.csv',4,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b04.csv',4,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b04.csv',4,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b04.csv',4,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b05.csv',5,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b05.csv',5,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b05.csv',5,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b05.csv',5,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b05.csv',5,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b05.csv',5,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b05.csv',5,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b06.csv',6,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b06.csv',6,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b06.csv',6,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b06.csv',6,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b06.csv',6,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b06.csv',6,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b06.csv',6,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b07.csv',7,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b07.csv',7,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b07.csv',7,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b07.csv',7,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b07.csv',7,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b07.csv',7,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b07.csv',7,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b08.csv',8,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b08.csv',8,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b08.csv',8,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b08.csv',8,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b08.csv',8,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b08.csv',8,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b08.csv',8,999)
  UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b09.csv',9,0) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b09.csv',9,1) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b09.csv',9,2) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b09.csv',9,3) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b09.csv',9,4) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b09.csv',9,5) UNION ALL SELECT * FROM cellcap('/tmp/mfv3_grid2/b09.csv',9,999)
) TO '/tmp/mfv3_grid2/results.csv' (HEADER, DELIMITER ',');
