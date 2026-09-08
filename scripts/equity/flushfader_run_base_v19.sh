#!/bin/bash
# 2026-09-08: THE BASE on the current engine (SPEC v3.1 time-clock floors + consol columns), every spec gate OFF.
# Same shape as base_v18 (flushfader_v17tkd_cand whitelist, volat >= 20bp, window 09:45-16:00, end 2026-08-21 = the whitelist's span).
# This is the control corpus for the gate review (§S49).
cd /home/mrakgr/Trading-Edge/research
OUT=data/equity/flushfader/base_v19
FF_CANDIDATE_TABLE=flushfader_v17tkd_cand DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader --base-run \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
  --out-dir $OUT > data/equity/flushfader/base_v19.log 2>&1
echo "ENGINE_EXIT=$?" >> data/equity/flushfader/base_v19.log
