#!/bin/bash
# RESUME 2 (2026-09-05): h2b (5 workers) was climbing toward the kill line at 49%; stopped at 2025-10-07 flushed.
# h2b is KEPT for trade_date < 2025-09-23 (10 whitelist trading days of margin for the <=5 in-flight days at the stop);
# this reruns 2025-09-23..2026-08-21 with THREE workers in two slices (h2c, h2d). Corpus = h1, h1b, h2, h2b(<B), h2c, h2d.
cd /home/mrakgr/Trading-Edge/research
run() { OUT=data/lowfader_alltape_$3; rm -rf $OUT
  FF_CANDIDATE_TABLE=lowfader_alltape_whitelist DOTNET_GCHeapHardLimitPercent=45 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader \
    --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
    --start-date $1 --end-date $2 --base-run --min-dv-0945-tape 0 --min-barnum 0 --min-volat-20m 0.002 --workers 3 --out-dir $OUT > data/lowfader_alltape_$3.log 2>&1
  echo "ENGINE_EXIT=$?" >> data/lowfader_alltape_$3.log; }
run 2025-09-23 2025-12-31 h2c
run 2026-01-01 2026-08-21 h2d
