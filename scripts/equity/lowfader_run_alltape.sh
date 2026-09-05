#!/bin/bash
# LowFader EXPANDED-UNIVERSE base run (2026-09-05, user): whole tape 2020-01..2026-08, volatility mirror 20bp only,
# engine floors OFF (dv_0945_tape 0, barnum 0), volat floor 20bp. Liquidity studied post-hoc on time-clock dollar_vol_60.
cd /home/mrakgr/Trading-Edge/research; OUT=data/lowfader_alltape; rm -rf $OUT
FF_CANDIDATE_TABLE=lowfader_alltape_whitelist DOTNET_GCHeapHardLimitPercent=55 ./TradingEdge.LowFader/bin/Release/net10.0/TradingEdge.LowFader \
  --db-path /home/mrakgr/Trading-Edge/research/data/trading.db --sec-dir /home/mrakgr/Trading-Edge/research/data/intraday_1s_slim \
  --start-date 2020-01-02 --end-date 2026-08-21 --base-run --min-dv-0945-tape 0 --min-barnum 0 --min-volat-20m 0.002 --out-dir $OUT > data/lowfader_alltape.log 2>&1
echo "ENGINE_EXIT=$?" >> data/lowfader_alltape.log
