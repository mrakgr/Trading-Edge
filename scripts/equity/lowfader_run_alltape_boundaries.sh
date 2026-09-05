#!/bin/bash
# BOUNDARY reruns (2026-09-05): the OOM-killed halves ran 14 day-workers, so up to 14 days before each kill boundary can be
# PARTIAL (2022-01-24 was missing outright). Rerun the last 15 whitelist days before each boundary into side globs; the
# merge cuts h1 to < 2022-01-19 and h2 to < 2024-12-06.
cd /home/mrakgr/Trading-Edge/research
run() { OUT=data/lowfader_alltape_$3; rm -rf $OUT
  FF_CANDIDATE_TABLE=lowfader_alltape_whitelist DOTNET_GCHeapHardLimitPercent=45 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader \
    --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
    --start-date $1 --end-date $2 --base-run --min-dv-0945-tape 0 --min-barnum 0 --min-volat-20m 0.002 --workers 3 --out-dir $OUT > data/lowfader_alltape_$3.log 2>&1
  echo "ENGINE_EXIT=$?" >> data/lowfader_alltape_$3.log; }
run 2022-01-19 2022-02-08 h1c
run 2024-12-06 2024-12-27 h2e
