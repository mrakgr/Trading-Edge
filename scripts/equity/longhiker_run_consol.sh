#!/usr/bin/env bash
# LongHiker CONSOL base pass (2026-09-07): the default v7 both-sides 20m-extremes
# sampler, full period, with the consol_{3,5,10,20}m(_lag1m) slot-variance-ratio
# columns. Corpus data/longhiker_trips_consol/. Live log + exit code.
cd /home/mrakgr/Trading-Edge/research
DOTNET_GCHeapHardLimitPercent=55 TradingEdge.LongHiker/bin/Release/net10.0/TradingEdge.LongHiker \
  --start-date 2020-01-02 --end-date 2026-09-04 \
  -o data/longhiker_trips_consol
echo "EXIT CODE: $?"
