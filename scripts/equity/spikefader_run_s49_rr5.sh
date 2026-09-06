#!/bin/bash
# 2026-09-06 addendum 5: SpikeFader s47 frame rerun with the rr 5/8/12 leg counts (record-only) -> data/spikefader_s49_rr5
cd /home/mrakgr/Trading-Edge/research; OUT=data/spikefader_s49_rr5; rm -rf $OUT
FF_CANDIDATE_TABLE=spikefader_s44_whitelist DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.SpikeFader/bin/Release/net10.0/TradingEdge.SpikeFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-07-17 --out-dir $OUT > data/spikefader_s49_rr5.log 2>&1
echo "ENGINE_EXIT=$?" >> data/spikefader_s49_rr5.log
