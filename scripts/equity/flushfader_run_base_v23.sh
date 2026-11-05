#!/bin/bash
# 2026-11-04 (S49aq, user: "an OLS slope feature that resets when the CUSUM does"): base_v22 + aslope_k{025,05,1,2} (the OLS
# slope of ln(vwap) since each low-k down CUSUM's last reset, bp per bar), appended at the end; then the young splice
# (barnum 11..21) as base_v23_young. Control: the 407 base_v22 columns are bit-identical on 2024-03-04..08; aslope =
# numpy polyfit over the bars since the reset (1,179 checked, worst 4.5e-12 relative).
cd /home/mrakgr/Trading-Edge/research
ENG=./TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader
COMMON="--base-run --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim --start-date 2020-01-02 --end-date 2026-08-21 --min-volat-20m 0.002 --entry-end-sec 57600 --entry-end-sec-short 46800"
DOTNET_GCHeapHardLimitPercent=55 /usr/bin/time -v $ENG --candidates flushfader_v17tkd_cand $COMMON --out-dir data/equity/flushfader/base_v23
echo "ENGINE_EXIT=$?"
DOTNET_GCHeapHardLimitPercent=55 $ENG --candidates data/equity/flushfader/young/cands_b11_21.parquet $COMMON --min-barnum 11 --out-dir data/equity/flushfader/young/base_v23_young
echo "YOUNG_EXIT=$?"
