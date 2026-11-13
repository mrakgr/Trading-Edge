#!/bin/bash
# cron wrapper for pc_pull.sh (user 2026-11-12): crontab runs it hourly 12:30-21:30 local Mon-Fri, so the PC's
# trading-hours half of the options download starts by itself whenever the PC is on. Skips when a PC runner is already
# up; the runner itself exits on non-trading days, outside the window, and when <name>/DONE exists.
#   30 12-21 * * 1-5  /home/mrakgr/Trading-Edge/research/scripts/options/pc_pull_cron.sh
export PATH="$HOME/.venv/bin:/usr/local/bin:/usr/bin:/bin"
cd "$(dirname "$0")/../.."                       # research/
pgrep -f 'options_runner.py --mode pc' > /dev/null && exit 0
echo "$(date '+%F %T %Z')  cron: starting pc_pull.sh" >> logs/options_runner_pc.log
exec bash scripts/options/pc_pull.sh "${1:-market_full}" "${2:-}" "${3:-eod}"   # 2026-11-13 cron: oi_full 60 open_interest
