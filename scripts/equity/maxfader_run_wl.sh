#!/bin/bash
# MaxFader --base-run on the prepass whitelist. Live log + exit code (CLAUDE.md).
cd /home/mrakgr/Trading-Edge/research
OUT=data/maxfader_wl; rm -rf $OUT
FF_CANDIDATE_TABLE=maxfader_whitelist DOTNET_GCHeapHardLimitPercent=55 \
  ./TradingEdge.MaxFader/bin/Release/net10.0/TradingEdge.MaxFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db \
  --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --base-run --out-dir $OUT > data/maxfader_wl.log 2>&1
echo "ENGINE_EXIT=$?" >> data/maxfader_wl.log
