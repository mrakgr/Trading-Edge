#!/usr/bin/env bash
# LongHiker SHAKEOUT base pass (2026-09-06/07): the session-high-only sampler,
# full period, corpus data/longhiker_trips_shake/. ~24 trips/tkd on the smoke
# week (5x smaller than v7's both-sides 20m-extreme rung). Live log + exit code.
cd /home/mrakgr/Trading-Edge/research
DOTNET_GCHeapHardLimitPercent=55 TradingEdge.LongHiker/bin/Release/net10.0/TradingEdge.LongHiker \
  --start-date 2020-01-02 --end-date 2026-09-04 --signal-on-session-high-only true \
  -o data/longhiker_trips_shake
echo "EXIT CODE: $?"
