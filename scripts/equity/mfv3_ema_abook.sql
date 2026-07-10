-- MaxFlyerV3: A-book breakdown of the 9-EMA arm-timer entry + max-EMA stop book.
-- A-book filter = brv20d>=100 & intraday ATR%>=0.03, where brv20d = breakout_bar_vol / (avgvol20*adj_ratio/390).
-- Under EmaEntry breakout_bar_vol = the SIGNAL (arm) bar's volume, ATR% = the signal-bar ATR — the pop we fade.
-- ret = -ret_moc (short P&L return); clip = min(short_ret, 0.50). net_pnl already carries the short sign.
-- Usage: duckdb -readonly data/trading.db < scripts/equity/mfv3_ema_abook.sql

CREATE OR REPLACE TEMP TABLE e AS
SELECT r.symbol, r.trade_date, YEAR(r.trade_date) yr,
       r.ret_moc, r.net_pnl, r.qty, r.entry_price, r.exit_reason,
       r.intraday_atr_pct_at_entry AS iatr,
       r.signal_time, r.entry_time, r.signal_high, r.signal_volume, r.sess_vol_high_at_signal,
       r.breakout_bar_vol / NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0) AS brv20d
FROM read_csv_auto('/tmp/mfv3_ema_book2020.csv') r
JOIN mr_candidate mc ON mc.ticker=r.symbol AND mc.date=r.trade_date
WHERE r.intraday_atr_pct_at_entry>=0.03 AND mc.avgvol20>0 AND mc.adj_ratio>0 AND
      r.breakout_bar_vol / NULLIF(mc.avgvol20*mc.adj_ratio/390.0,0) >= 100;

.mode box
-- NB: NO clip PF for the short book — a short's max gain is +100% (price -> 0), so the win side is already
-- bounded. raw PF is the honest metric here (unlike the long momentum books that need the +50% clip).
SELECT '=== EMA book (per-signal pending entry + max-EMA stop), A-book filtered — OVERALL ===' z;
SELECT COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN -ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),3) raw_pf,
  ROUND(100.0*AVG(-ret_moc),2) avg_pct,
  ROUND(SUM(net_pnl),0) net_pnl,
  ROUND(MIN(net_pnl),0) worst_net,
  ROUND(100.0*MAX(ret_moc),1) worst_ret_pct
FROM e;

SELECT '=== exit-reason mix (A-book) ===' z;
SELECT exit_reason, COUNT(*) n, ROUND(100.0*AVG(-ret_moc),2) avg_ret_pct, ROUND(SUM(net_pnl),0) net
FROM e GROUP BY exit_reason ORDER BY n DESC;

SELECT '=== entry lag: minutes from signal to entry (A-book) ===' z;
SELECT ROUND(AVG(date_diff('minute', signal_time, entry_time)),1) mean_lag_min,
       quantile_cont(date_diff('minute', signal_time, entry_time), 0.5) med_lag_min,
       MAX(date_diff('minute', signal_time, entry_time)) max_lag_min,
       ROUND(100.0*AVG(entry_price/signal_high-1),1) mean_entry_vs_sighi_pct
FROM e;

SELECT '=== by-year (A-book): raw PF, net, worst ===' z;
SELECT yr, COUNT(*) n,
  ROUND(100.0*AVG(CASE WHEN -ret_moc>0 THEN 1 ELSE 0 END),1) win_pct,
  ROUND(SUM(CASE WHEN -ret_moc>0 THEN -ret_moc ELSE 0 END)/NULLIF(-SUM(CASE WHEN -ret_moc<0 THEN -ret_moc ELSE 0 END),0),2) raw_pf,
  ROUND(SUM(net_pnl)/1000.0,0) net_k, ROUND(MIN(net_pnl),0) worst_net
FROM e GROUP BY yr ORDER BY yr;

SELECT '=== worst 15 EMA-book losers (A-book) — did the max-EMA stop cap them? ===' z;
SELECT symbol, trade_date, signal_time, entry_time, exit_reason,
  ROUND(entry_price,2) entry_px, ROUND(100.0*ret_moc,0) ret_pct, ROUND(net_pnl,0) net
FROM e ORDER BY net_pnl LIMIT 15;
