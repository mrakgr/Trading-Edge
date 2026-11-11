#!/bin/bash
# The PC's half of the ThetaData options download (user 2026-11-11): pulls ONLY during trading hours (trading days
# 07:00–16:30 ET, 13:30 early closes) and EXITS when the window ends; the VPS (te-options.service) pulls outside them.
# Both share the job list and each other's progress through the storage box (te-vps/options/<name>/).
# Run order (user 2026-11-11): market_full = EVERY expiry, (1) 2022-11-09 → 2026-11-10 every root, then
# (2) 2012-06-01 → 2022-11-08 every root (a ≤ 90 DTE cap first would re-pull 56% of the rows later: no floor exists).
# Start it once a trading day while the PC is on (it waits for 07:03 ET if started earlier; START_NOW=1 skips the wait —
# only when the VPS is not pulling). Detached; log:
#   logs/options_runner_pc.log (runner) + logs/<name>.pc.pull.log (the pull)
# ⚠ One Theta Terminal per key: do not run ~/thetadata/start.sh at the same time.
set -euo pipefail
cd "$(dirname "$0")/../.."                       # research/
NAME=${1:-market_full}
CRED="$HOME/.config/hetzner-storage-box/credentials-share"   # the SHARE sub-account; its name stays out of the public repo
BOX="$(sed -n 's/^USERNAME=//p' "$CRED")@$(sed -n 's/^HOSTNAME=//p' "$CRED")"
MAXDTE=${2:-}                                    # "" = every expiry
nohup setsid python3 scripts/options/options_runner.py --mode pc --name "$NAME" \
    --dest "$BOX:options/$NAME/" --key "$HOME/.ssh/storagebox_pc_ed25519" \
    --out-root data/options --logs logs --thetadata-dir "$HOME/thetadata" --python python3 \
    --timezone-fs TradingEdge.Orb/Timezone.fs --reference data/options/massive_root_days.parquet \
    --calendar-file data/options/trading_days.txt ${MAXDTE:+--max-dte $MAXDTE} ${START_NOW:+--start-now} >> logs/options_runner_pc.log 2>&1 < /dev/null &
echo "started the PC options runner ($NAME), pid $!; log research/logs/options_runner_pc.log"
