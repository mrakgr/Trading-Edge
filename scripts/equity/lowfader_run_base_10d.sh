#!/bin/bash
# 2026-09-10: the 10-DAY LowFader BASE for the Scanner trades-tape harness — the harness window on the FULL universe
# (mr_candidate_1s_v2, no whitelist); same flags as the ratified corpus (--base-run --min-volat-20m 0.003).
cd /home/mrakgr/Trading-Edge/research
OUT=data/lowfader_base_10d
DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader --base-run \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2026-07-27 --end-date 2026-08-07 --min-volat-20m 0.003 --out-dir $OUT > data/lowfader_base_10d.log 2>&1
echo "ENGINE_EXIT=$?" >> data/lowfader_base_10d.log
