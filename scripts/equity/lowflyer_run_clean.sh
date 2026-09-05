#!/bin/bash
# LowFlyer PRODUCTION gates on the CLEAN universe (mr_candidate_1s) — the §S39d lookahead CONTROL (2026-09-05).
cd /home/mrakgr/Trading-Edge/research
OUT=data/lowflyer_long_clean.csv
./TradingEdge.LowFlyer/bin/Release/net10.0/TradingEdge.LowFlyer \
  --db-path data/trading.db --minute-dir data/minute_aggs --out $OUT --candidate-table mr_candidate_1s \
  --start-date 2016-08-08 --end-date 2026-06-25 \
  --min-bar-flush -0.007 --min-bar-flush-floor -0.12 --max-intraday-atr-pct 0.02 --vol-high-frac 0.90
echo "exit=$?"
