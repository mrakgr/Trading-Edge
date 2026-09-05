#!/bin/bash
# LowFlyer PRODUCTION run (1m engine) — regenerates the gated trips CSV for the by-year robustness check (2026-09-05).
cd /home/mrakgr/Trading-Edge/research
OUT=data/lowflyer_long_prod.csv
./TradingEdge.LowFlyer/bin/Release/net10.0/TradingEdge.LowFlyer \
  --db-path data/trading.db --minute-dir data/minute_aggs --out $OUT \
  --start-date 2003-09-10 --end-date 2026-08-21 \
  --min-bar-flush -0.007 --min-bar-flush-floor -0.12 --max-intraday-atr-pct 0.02 --vol-high-frac 0.90
echo "exit=$?"
