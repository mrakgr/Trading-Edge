#!/bin/bash
# 2026-09-08: consol port smoke — v49 whitelist at the FULL spec; must be bit-identical to v49_spec20 on every old column.
cd /home/mrakgr/Trading-Edge/research
OUT=data/equity/flushfader/v51_consol; rm -rf $OUT
FF_CANDIDATE_TABLE=flushfader_v49tkd_cand DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 --min-lows-180 0 \
  --out-dir $OUT > data/equity/flushfader/v51_consol.log 2>&1
echo "ENGINE_EXIT=$?" >> data/equity/flushfader/v51_consol.log
