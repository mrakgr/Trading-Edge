#!/bin/bash
# 2026-09-08 (S49f): the EARLY-EPISODE side corpus (barnum 1..21) for the barnum control — same flags as base_v19, --min-barnum 0.
cd /home/mrakgr/Trading-Edge/research
OUT=data/equity/flushfader/base_v20_early
FF_CANDIDATE_TABLE=flushfader_early_cand DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader --base-run --min-barnum 0 \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
  --out-dir $OUT > data/equity/flushfader/base_v20_early.log 2>&1
echo "ENGINE_EXIT=$?" >> data/equity/flushfader/base_v20_early.log
