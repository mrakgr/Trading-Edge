#!/bin/bash
# 2026-09-09: the 10-DAY BASE for the Scanner trades-tape harness (docs/flushfader_results.md §S49aj).
# Same engine and flags as flushfader_run_base_v19.sh, but (a) the harness window 2026-07-27..2026-08-07 and
# (b) the FULL universe (mr_candidate_1s_v2 — no whitelist), because the Scanner's --gate path promotes every
# ticker that clears dv >= $2M & n_bars >= 200 in-stream and the comparison must cover the whole promoted set.
cd /home/mrakgr/Trading-Edge/research
OUT=data/equity/flushfader/base_10d
DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader --base-run \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2026-07-27 --end-date 2026-08-07 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
  --out-dir $OUT > data/equity/flushfader/base_10d.log 2>&1
echo "ENGINE_EXIT=$?" >> data/equity/flushfader/base_10d.log
