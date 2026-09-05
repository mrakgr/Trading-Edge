#!/bin/bash
# RESUME (2026-09-05): the two halves were OOM-killed at 2022-02-08 (h1) and 2024-12-27 (h2) — 14 day-workers x ~2 days
# of all-tape bars each. Rerun ONLY the missing spans into side dirs with --workers 5 (trip SET identical at any worker
# count); the corpus = the four globs h1, h1b, h2, h2b.
cd /home/mrakgr/Trading-Edge/research
run() { OUT=data/lowfader_alltape_$3; rm -rf $OUT
  FF_CANDIDATE_TABLE=lowfader_alltape_whitelist DOTNET_GCHeapHardLimitPercent=45 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader \
    --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
    --start-date $1 --end-date $2 --base-run --min-dv-0945-tape 0 --min-barnum 0 --min-volat-20m 0.002 --workers 5 --out-dir $OUT > data/lowfader_alltape_$3.log 2>&1
  echo "ENGINE_EXIT=$?" >> data/lowfader_alltape_$3.log; }
run 2022-02-09 2022-12-31 h1b
run 2024-12-28 2026-08-21 h2b
