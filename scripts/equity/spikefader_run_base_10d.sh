#!/bin/bash
# 2026-09-10: the 10-DAY SpikeFader run for the Scanner trades-tape harness — the harness window on the FULL universe
# (mr_candidate_1s_v2, no whitelist); default config = the s47 frame (no gate flags, as data/spikefader_s47.log).
cd /home/mrakgr/Trading-Edge/research
OUT=data/spikefader_base_10d
DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.SpikeFader/bin/Release/net10.0/TradingEdge.SpikeFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2026-07-27 --end-date 2026-08-07 --out-dir $OUT > data/spikefader_base_10d.log 2>&1
echo "ENGINE_EXIT=$?" >> data/spikefader_base_10d.log
