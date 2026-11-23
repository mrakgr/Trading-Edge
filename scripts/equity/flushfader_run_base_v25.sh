#!/bin/bash
# 2026-11-18 (user: "Yes" — the reference with the retail share): THE BASE ON THE SHARE-COLUMN CORPUS. base_v24's run
# (same candidates, same flags, same span 2020-01-02..2026-08-21) on data/intraday_1s_slim rebuilt with total_volume /
# retail_volume / mid_volume, so every trip records retail_share_1200 / mid_share_1200 (research 8f6b98e).
#
# CHAINED to the in-place corpus rebuild (build_all_1s_bars.fsx --newest-first, logs/bars_share_rebuild.log), which
# reaches 2020-01-02 well before its 2016-2019 tail. The two must never run together (the engine ~11 GB + the builder's
# 8 GB DuckDB cap on a 15 GB box), so:
#   1. wait until 2020-01-02's file has the share columns (the builder writes a day atomically: .tmp, then a move);
#   2. stop the rebuild (resumable: a killed day is only a .tmp, swept and redone on restart);
#   3. the base run, alone;
#   4. restart the rebuild for the rest (2016-2019), as before.
#   cd research && setsid nohup bash scripts/equity/flushfader_run_base_v25.sh > logs/base_v25_chain.log 2>&1 &
cd /home/mrakgr/Trading-Edge/research
BARS=data/intraday_1s_slim
has_share() { python3 -c "import pyarrow.parquet as pq,sys; sys.exit(0 if 'total_volume' in pq.read_schema('$BARS/2020-01-02.parquet').names else 1)" 2>/dev/null; }

echo "$(date '+%F %T') waiting for $BARS/2020-01-02.parquet with the share columns"
until has_share; do sleep 60; done
echo "$(date '+%F %T') 2020-01-02 rebuilt: $(tail -1 logs/bars_share_rebuild.log)"

# the builder's PID: its own command line, never this script's (no pgrep -f: it can match the searcher itself)
PID=$(ps -eo pid=,args= | awk '$0 ~ /fsi\.dll scripts\/conversion\/build_all_1s_bars\.fsx/ && $0 !~ /awk/ {print $1}' | head -1)
if [ -n "$PID" ]; then
  echo "$(date '+%F %T') stopping the rebuild (pid $PID)"
  kill "$PID"
  while kill -0 "$PID" 2>/dev/null; do sleep 2; done
  echo "$(date '+%F %T') rebuild stopped"
else
  echo "$(date '+%F %T') ⚠ no rebuild process found (finished?) — going on"
fi

echo "$(date '+%F %T') base_v25 starting"
DOTNET_GCHeapHardLimitPercent=55 /usr/bin/time -v ./TradingEdge.FlushFader/bin/Release/net11.0/TradingEdge.FlushFader \
  --candidates data/equity/flushfader/v24/cands_b11.parquet --base-run --min-barnum 11 \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/$BARS \
  --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
  --out-dir data/equity/flushfader/base_v25 > logs/flushfader_base_v25.log 2>&1
echo "$(date '+%F %T') base_v25 ENGINE_EXIT=$?"

echo "$(date '+%F %T') resuming the rebuild (2016-2019)"
dotnet fsi scripts/conversion/build_all_1s_bars.fsx -- --newest-first --stage-dir data/.stage_1s >> logs/bars_share_rebuild.log 2>&1
echo "$(date '+%F %T') rebuild exit $?"
