#!/bin/bash
# The 2012-> survey of the three VV systems on market_full + the PIT universe (user 2026-11-13). One script at a time,
# 4 workers (RAM: the PC has 15 GB; a 14-year TSLA worker peaks ~1.5 GB over the ~1.2 GB of shared inputs).
#   cd research && nohup setsid bash scripts/options/survey_run.sh > logs/options_survey_run.log 2>&1 &
set -u
cd "$(dirname "$0")/../.."
EOD=data/options/market_full
run() { echo "$(date '+%F %T')  $*"; /usr/bin/time -f 'peakRSS(main) %MkB %es' python3 "$@"; echo "exit $?"; }
run scripts/options/event_vol.py --full --eod $EOD --releases-only --workers 4 --out data/options/full/event_vol > logs/options_full_event_vol.log 2>&1
run scripts/options/vv_long.py --full --eod $EOD --workers 4 --out data/options/full/vv_long > logs/options_full_vv_long.log 2>&1
run scripts/options/skew_vert.py --full --eod $EOD --workers 4 --out data/options/full/skew_vert > logs/options_full_skew_vert.log 2>&1
echo "$(date '+%F %T')  all done"
