#!/bin/bash
# rr8 whitelist on the mini-tape, one run per stop offset. usage: maxfader_run_rr8.sh <pct> <tag>
cd /home/mrakgr/Trading-Edge/research; PCT=${1:?pct}; TAG=${2:?tag}; OUT=data/maxfader_rr8_$TAG; rm -rf $OUT
FF_CANDIDATE_TABLE=maxfader_rr8_whitelist ./TradingEdge.MaxFader/bin/Release/net10.0/TradingEdge.MaxFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir data/intraday_1s_rr8 \
  --base-run --stop-pct $PCT --out-dir $OUT > data/maxfader_rr8_$TAG.log 2>&1
echo "ENGINE_EXIT=$?" >> data/maxfader_rr8_$TAG.log
