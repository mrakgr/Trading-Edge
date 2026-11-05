#!/bin/bash
# 2026-11-04 (S49ap, user: "This k that we're using is quite aggressive"): base_v21 + the raw CUSUM at k = 0.25 / 0.5 / 1 / 2
# bp per bar (cusr_{up,dn,up_age,dn_age}_k{025,05,1,2}), appended after the Kalman columns; then the young splice (barnum
# 11..21, flushfader_run_base_v19_young.sh's command) as base_v22_young. Control: the 391 base_v21 columns are
# bit-identical on 2024-03-04..08 (21,441 = 21,441); the new values = a Python recomputation exactly (200 trips x 4 k).
cd /home/mrakgr/Trading-Edge/research
ENG=./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader
COMMON="--base-run --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800"
DOTNET_GCHeapHardLimitPercent=55 /usr/bin/time -v $ENG --candidates flushfader_v17tkd_cand $COMMON --out-dir data/equity/flushfader/base_v22
echo "ENGINE_EXIT=$?"
DOTNET_GCHeapHardLimitPercent=55 $ENG --candidates data/equity/flushfader/young/cands_b11_21.parquet $COMMON --min-barnum 11 --out-dir data/equity/flushfader/young/base_v22_young
echo "YOUNG_EXIT=$?"
