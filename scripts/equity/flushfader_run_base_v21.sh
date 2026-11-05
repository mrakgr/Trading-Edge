#!/bin/bash
# 2026-11-04 (S49ao, user): base_v20 + the Kalman local-linear-trend grid (kal_{slope,t,gap}_<l>_<h>: 3 level gains x slope half-responses 60/300/1200 bars), appended after S49an's columns.
# The old 364 columns (base_v20's) are bit-identical on the 2024-03-04..08 control (21,441 = 21,441); the Kalman values
# match an independent Python filter to 1e-10 relative (200 trips x 9 filters). Same flags as base_v19 / v20.
cd /home/mrakgr/Trading-Edge/research
OUT=data/equity/flushfader/base_v21
DOTNET_GCHeapHardLimitPercent=55 /usr/bin/time -v ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader --candidates flushfader_v17tkd_cand --base-run \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
  --out-dir $OUT
echo "ENGINE_EXIT=$?"

