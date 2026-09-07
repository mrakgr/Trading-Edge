#!/usr/bin/env bash
# LongHiker CONSOL smoke (2026-09-07): one week, default (v7 both-sides 20m-extremes)
# sampler, the new consol_* columns. Live log + exit code.
cd /home/mrakgr/Trading-Edge/research
DOTNET_GCHeapHardLimitPercent=55 TradingEdge.LongHiker/bin/Release/net10.0/TradingEdge.LongHiker \
  --start-date 2026-08-24 --end-date 2026-08-28 -o data/longhiker_consol_smoke
echo "EXIT CODE: $?"
