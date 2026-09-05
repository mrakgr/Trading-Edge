#!/bin/bash
# LowFader EXPANDED-UNIVERSE pilot (2026-09-05, user): the WHOLE 1s tape for 2025-01..2025-06, NO liquidity floors
# (dv_0945_tape 0, n_bars 0) -> table lowfader_alltape_cand. Liquidity moves to a post-hoc time-clock dollar_vol_60 band.
cd /home/mrakgr/Trading-Edge/research
dotnet fsi scripts/equity/build_mr_candidate_1s.fsx -- --min-dv 0 --min-bars 0 --min-neff 0 --pattern '2025-0[1-6]-*.parquet' -t lowfader_alltape_cand
echo "BUILD_EXIT=$?"
