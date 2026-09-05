#!/bin/bash
# Fallback (2026-09-05): the all-tape base run in TWO date halves, sequentially, each into its own dir (merge = read both globs).
# Halves the resident candidate array; a kill in one half leaves the other intact. Same flags as lowfader_run_alltape.sh.
cd /home/mrakgr/Trading-Edge/research
run() { OUT=data/lowfader_alltape_$3; rm -rf $OUT
  FF_CANDIDATE_TABLE=lowfader_alltape_whitelist DOTNET_GCHeapHardLimitPercent=45 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader \
    --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
    --start-date $1 --end-date $2 --base-run --min-dv-0945-tape 0 --min-barnum 0 --min-volat-20m 0.002 --out-dir $OUT > data/lowfader_alltape_$3.log 2>&1
  echo "ENGINE_EXIT=$?" >> data/lowfader_alltape_$3.log; }
run 2020-01-02 2022-12-31 h1
run 2023-01-01 2026-08-21 h2
