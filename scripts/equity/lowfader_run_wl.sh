#!/bin/bash
# LowFader --base-run on the session-LOW prepass whitelist. usage: lowfader_run_wl.sh <start> <end> <tag>
cd /home/mrakgr/Trading-Edge/research; START=${1:?}; END=${2:?}; TAG=${3:?}; TABLE=${4:-lowfader_whitelist}; OUT=data/lowfader_wl_$TAG; rm -rf $OUT
FF_CANDIDATE_TABLE=$TABLE DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date $START --end-date $END --base-run --out-dir $OUT > data/lowfader_wl_$TAG.log 2>&1
echo "ENGINE_EXIT=$?" >> data/lowfader_wl_$TAG.log
