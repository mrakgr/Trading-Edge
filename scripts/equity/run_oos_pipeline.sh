#!/usr/bin/env bash
# S43be — the OOS steps: rebuild mr_candidate_1s over the FULL slim dir, then run
# the FROZEN spec on both OOS ranges. No re-tuning anywhere.
#
# ⚠ This script no longer waits for the 1s conversion. The wait loop used
# `pgrep -f build_all_1s_bars`, which ALSO matches any shell whose command line
# contains that string — including a monitoring command — so it could spin
# forever after the conversion had finished. Run this only once the conversion
# has exited (check: tail logs/build_1s_oos.log for "Processed N days").
set -u
cd /home/mrakgr/Trading-Edge

echo "[$(date +%H:%M:%S)] slim days available: $(ls data/intraday_1s_slim/*.parquet | wc -l)"

# --- barnum-shift check: the candidate table's episode warmup (barnum >= 22) is
# computed over the slim history. Adding 2016-2019 BEFORE 2020 gives early-2020
# tickers real prior history, so some rows that were warmup-excluded become
# eligible. Snapshot the old table's in-sample row count so the shift is
# measurable rather than silent.
python3 -c "
import duckdb
con = duckdb.connect('data/trading.db', read_only=True)
try:
    n = con.execute(\"SELECT count(*) FROM mr_candidate_1s WHERE date >= '2020-01-02' AND date <= '2026-07-17' AND barnum >= 22\").fetchone()[0]
    print(f'OLD candidate table, in-sample rows passing barnum>=22: {n:,}')
except Exception as e:
    print('old-table snapshot failed:', e)
" 2>&1 | tee logs/oos_barnum_before.txt

echo "[$(date +%H:%M:%S)] rebuilding mr_candidate_1s over the full slim dir..."
dotnet fsi scripts/equity/build_mr_candidate_1s.fsx > logs/mr_candidate_1s_rebuild.log 2>&1
RC=$?
tail -5 logs/mr_candidate_1s_rebuild.log
if [ $RC -ne 0 ]; then echo "REBUILD FAILED (rc=$RC) — stopping"; exit 1; fi

python3 -c "
import duckdb
con = duckdb.connect('data/trading.db', read_only=True)
n = con.execute(\"SELECT count(*) FROM mr_candidate_1s WHERE date >= '2020-01-02' AND date <= '2026-07-17' AND barnum >= 22\").fetchone()[0]
tot = con.execute('SELECT count(*), min(date), max(date) FROM mr_candidate_1s').fetchone()
print(f'NEW candidate table: {tot[0]:,} rows, {tot[1]} -> {tot[2]}')
print(f'NEW in-sample rows passing barnum>=22: {n:,}')
" 2>&1 | tee logs/oos_barnum_after.txt

echo "[$(date +%H:%M:%S)] OOS run 1/2 — backward 2016-08-08 -> 2019-12-31"
TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
  --start-date 2016-08-08 --end-date 2019-12-31 \
  -o data/equity/flushfader/oos_back > logs/flushfader_oos_back.log 2>&1
tail -12 logs/flushfader_oos_back.log

echo "[$(date +%H:%M:%S)] OOS run 2/2 — forward 2026-07-18 -> 2026-08-07"
TradingEdge.FlushFader/bin/Release/net10.0/TradingEdge.FlushFader \
  --start-date 2026-07-18 --end-date 2026-08-07 \
  -o data/equity/flushfader/oos_fwd > logs/flushfader_oos_fwd.log 2>&1
tail -12 logs/flushfader_oos_fwd.log

echo "[$(date +%H:%M:%S)] OOS PIPELINE COMPLETE"
