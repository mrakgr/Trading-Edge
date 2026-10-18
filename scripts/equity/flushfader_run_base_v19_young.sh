#!/bin/bash
# 2026-10-18 (user: FlushFader floor at barnum 11, barnum 11-21 sized x0.8): the base_v19 SPLICE for the listings' sessions
# 11-21 — the exact base_v19 command (flushfader_run_base_v19.sh) on the barnum 11..21 ticker-days of mr_candidate_1s_v2
# (data/equity/flushfader/young/cands_b11_21.parquet, 2020-01-02 .. 2026-08-21) with --min-barnum 11. Disjoint from
# base_v19 (which is barnum >= 22) by ticker-day. CONTROL: $1 = control runs the same command on 5 barnum >= 22 days, which
# must equal base_v19's trips for those days.
cd /home/mrakgr/Trading-Edge/research
if [ "$1" = control ]; then
  CANDS=data/equity/flushfader/young/cands_control.parquet; OUT=data/equity/flushfader/young/control; S=2025-03-03; E=2025-03-07; MB=22
else
  CANDS=data/equity/flushfader/young/cands_b11_21.parquet; OUT=data/equity/flushfader/young/base_v19_young; S=2020-01-02; E=2026-08-21; MB=11
fi
DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader --candidates $CANDS --base-run \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date $S --end-date $E --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 --min-barnum $MB \
  --out-dir $OUT > $OUT.log 2>&1
echo "ENGINE_EXIT=$?" >> $OUT.log
