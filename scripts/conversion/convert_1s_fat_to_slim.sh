#!/usr/bin/env bash
# One-shot migration: project the old 11-column data/intraday_1s/{date}.parquet
# files down to the slim 5-column schema in data/intraday_1s_slim/ (see
# build_all_1s_bars.fsx for the schema rationale — SurgeRider F1-F7).
# A pure column projection: vwap/volume/trade_count are bit-identical to the fat
# file's (verified on 2026-07-17, all 26.7M rows), so days already built fat do
# NOT need re-aggregation from the trades tape (~0.7s/day vs ~70s/day).
# Resume-aware: skips dates already present in the slim dir. 2020-2022 (never
# built fat) still needs the full builder run.
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p data/intraday_1s_slim
n=0; skipped=0
for f in data/intraday_1s/*.parquet; do
  d=$(basename "$f" .parquet)
  out="data/intraday_1s_slim/$d.parquet"
  if [ -s "$out" ]; then skipped=$((skipped+1)); continue; fi
  duckdb -c "SET memory_limit='8GB'; SET threads=8;
    COPY (SELECT ticker, bucket, vwap, volume, trade_count
          FROM read_parquet('$f') ORDER BY ticker, bucket)
    TO '$out.tmp' (FORMAT PARQUET, COMPRESSION 'zstd', COMPRESSION_LEVEL 9);"
  mv "$out.tmp" "$out"
  n=$((n+1))
  if [ $((n % 50)) -eq 0 ]; then echo "converted $n (latest: $d)"; fi
done
echo "DONE: converted $n, skipped $skipped (already present)"
