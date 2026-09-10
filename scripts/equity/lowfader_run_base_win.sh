#!/bin/bash
# 2026-09-10: a LowFader --base-run over an arbitrary window on the FULL universe (the Scanner trades-tape harness).
# usage: lowfader_run_base_win.sh <start> <end> <tag>   -> data/lowfader_base_<tag>/ + data/lowfader_base_<tag>.log
cd /home/mrakgr/Trading-Edge/research; START=${1:?}; END=${2:?}; TAG=${3:?}; OUT=data/lowfader_base_$TAG
DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader --base-run \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date $START --end-date $END --min-volat-20m 0.003 --out-dir $OUT > data/lowfader_base_$TAG.log 2>&1
echo "ENGINE_EXIT=$?" >> data/lowfader_base_$TAG.log
