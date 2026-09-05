#!/bin/bash
# LowFader EXPANDED UNIVERSE (2026-09-05, user): the WHOLE 1s tape, 2020-01..2026-08, NO liquidity floors, NO barnum.
# -> table lowfader_alltape_cand (candidate schema). Liquidity is studied post-hoc on the time-clock dollar_vol_60 column.
cd /home/mrakgr/Trading-Edge/research
dotnet fsi scripts/equity/build_mr_candidate_1s.fsx -- --min-dv 0 --min-bars 0 --min-neff 0 --pattern '202[0-6]-*.parquet' -t lowfader_alltape_cand
echo "BUILD_EXIT=$?"
