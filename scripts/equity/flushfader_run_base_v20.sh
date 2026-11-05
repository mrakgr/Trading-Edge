#!/bin/bash
# 2026-11-04 (S49an, user): THE BASE with the trend measures — Mann–Kendall tau (60..1200 present bars) and the CUSUMs
# (normalised k 0.25 / 0.5 / 1; raw k 0 / 4.35 / 10 bp), RECORD-ONLY, appended after every old column. Same flags as
# base_v19. The old 335 columns are bit-identical to the committed engine's (the 2024-03-04..08 control: 21,441 = 21,441).
cd /home/mrakgr/Trading-Edge/research
OUT=data/equity/flushfader/base_v20
DOTNET_GCHeapHardLimitPercent=55 /usr/bin/time -v ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader --candidates flushfader_v17tkd_cand --base-run \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
  --out-dir $OUT
echo "ENGINE_EXIT=$?"
