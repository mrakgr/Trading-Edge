#!/bin/bash
# RESUME of flushfader_run_base_v24.sh (OOM-killed at 81.5 % in its 11G scope, 11:50 ET; 26 parts written, through 2025-07-14):
# the dates from 2025-04-15 into a side directory; 2025-04-15..05-14 overlaps the parts (the control), from 05-15 it replaces them.
# 2026-11-04 (user: "we should rebuild the reference with the open and close markers removed"): THE BASE ON TODAY'S DATA.
# The current engine (S49an..aq columns included) on the FULL current universe: every barnum >= 11 ticker-day of
# data/v2_candidates/mr_candidate_1s_v2 (the 2026-10-25 marker-free rebuild, $1.6M dv_0945 floor), 2020-01-02..2026-08-21
# (production's span), one run for the main book and the young trades (--min-barnum 11). No whitelist: the v17 whitelist
# was the old full-universe run's trip-producing ticker-days, a speedup that is stale on the marker-free bars.
# The candidate file: data/equity/flushfader/v24/cands_b11.parquet (1,183,355 ticker-days, written 2026-11-04).
cd /home/mrakgr/Trading-Edge/research
DOTNET_GCHeapHardLimitPercent=55 /usr/bin/time -v ./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
  --candidates data/equity/flushfader/v24/cands_b11.parquet --base-run --min-barnum 11 \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2025-04-15 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800 \
  --out-dir data/equity/flushfader/base_v24_tail
echo "ENGINE_EXIT=$?"
