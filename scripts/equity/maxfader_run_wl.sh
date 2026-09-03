#!/bin/bash
# MaxFader --base-run on the prepass whitelist, one date range per output dir so
# ranges merge by glob (data/maxfader_wl_*/*.parquet). Live log + exit code.
#   usage: maxfader_run_wl.sh <start yyyy-mm-dd> <end yyyy-mm-dd> <tag>
cd /home/mrakgr/Trading-Edge/research
START=${1:?start}; END=${2:?end}; TAG=${3:?tag}
OUT=data/maxfader_wl_$TAG; rm -rf $OUT
FF_CANDIDATE_TABLE=maxfader_whitelist DOTNET_GCHeapHardLimitPercent=55 \
  ./TradingEdge.MaxFader/bin/Release/net10.0/TradingEdge.MaxFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db \
  --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date $START --end-date $END \
  --base-run --out-dir $OUT > data/maxfader_wl_$TAG.log 2>&1
echo "ENGINE_EXIT=$?" >> data/maxfader_wl_$TAG.log
