#!/bin/bash
# RESUME of flushfader_run_base_v25.sh: OOM-killed (signal 9) at 67.8 % / 2024-12-11 after 91 min, 12.4 GB RSS (the engine's
# memory GROWS through a run — base_v24 died the same way at 81.5 %). ⚠ /usr/bin/time -v reported "Exit status: 0" and the
# chain printed ENGINE_EXIT=0: a signal kill must be read from "Command terminated by signal" in the log, not $?.
# 23 parts written, through 2024-09-25. This reruns 2024-08-26..2026-08-21 in TWO sequential halves (each a short run, so
# the growth never reaches the cap) into side directories; 2024-08-26..09-25 overlaps the parts (the control).
#   cd research && setsid nohup bash scripts/equity/flushfader_run_base_v25_tail.sh > logs/base_v25_tail_chain.log 2>&1 &
cd /home/mrakgr/Trading-Edge/research
run() {  # $1 start, $2 end, $3 out-dir
  echo "$(date '+%F %T') $3: $1 .. $2"
  DOTNET_GCHeapHardLimitPercent=55 /usr/bin/time -v ./TradingEdge.FlushFader/bin/Release/net11.0/TradingEdge.FlushFader \
    --candidates data/equity/flushfader/v24/cands_b11.parquet --base-run --min-barnum 11 \
    --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
    --start-date "$1" --end-date "$2" --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
    --out-dir "$3" > "logs/flushfader_$(basename $3).log" 2>&1
  rc=$?
  if grep -q "terminated by signal" "logs/flushfader_$(basename $3).log"; then rc=137; fi
  echo "$(date '+%F %T') $3 ENGINE_EXIT=$rc"
  return $rc
}
run 2024-08-26 2025-08-15 data/equity/flushfader/base_v25_tail1 || exit 1
run 2025-08-18 2026-08-21 data/equity/flushfader/base_v25_tail2
