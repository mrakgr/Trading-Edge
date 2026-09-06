#!/bin/bash
# 2026-09-06: record the rr-qualified leg counts (LowFader §L16 port) into FRESH corpora — v49_spec20 / spikefader_s47 stay the references.
# Flags = the v49 / s47 run banners (data/equity/flushfader/v49_spec20.log, data/spikefader_s47.log).
cd /home/mrakgr/Trading-Edge/research
OUT=data/equity/flushfader/v50_rr; rm -rf $OUT
FF_CANDIDATE_TABLE=flushfader_v49tkd_cand DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 --min-lows-180 0 \
  --out-dir $OUT > data/equity/flushfader/v50_rr.log 2>&1
echo "ENGINE_EXIT=$?" >> data/equity/flushfader/v50_rr.log
OUT=data/spikefader_s48_rr; rm -rf $OUT
FF_CANDIDATE_TABLE=spikefader_s44_whitelist DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.SpikeFader/bin/Release/net10.0/TradingEdge.SpikeFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-07-17 --out-dir $OUT > data/spikefader_s48_rr.log 2>&1
echo "ENGINE_EXIT=$?" >> data/spikefader_s48_rr.log
echo ALL_DONE
