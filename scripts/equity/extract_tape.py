"""Extract a WHITELIST's 1s bars into a mini-tape directory (2026-09-04, user: "why do
2,455 tkd take an hour?"). The engine reads ONE query per day file and each scans the
whole ~96MB day (every ticker) to pull one or two names: 1,114 day files x ~3s = the
hour; the engine's own fold was ~87s of it. This pass extracts only the whitelisted
tickers into small day files with the IDENTICAL schema (ticker, bucket, vwap, volume,
trade_count), so `--sec-dir <out>` makes every rerun IO-trivial. One pass here costs
what one engine run cost; every rerun after it is minutes.
usage: python3 scripts/equity/extract_tape.py <whitelist table> <out dir>
"""
import duckdb, os, sys, time
table, out = sys.argv[1], sys.argv[2]
SEC = "data/intraday_1s_slim"
os.makedirs(out, exist_ok=True)
c = duckdb.connect("data/trading.db", read_only=True); c.execute("SET memory_limit='8GB'; SET threads=8")
days = c.execute(f"SELECT date, list(ticker) FROM {table} GROUP BY date ORDER BY date").fetchall()
t0 = time.time(); rows = 0; missing = 0
for i, (d, tickers) in enumerate(days):
    src = f"{SEC}/{d}.parquet"; dst = f"{out}/{d}.parquet"
    if not os.path.exists(src): missing += 1; continue
    tl = ",".join("'" + t.replace("'", "''") + "'" for t in tickers)
    c.execute(f"COPY (SELECT ticker, bucket, vwap, volume, trade_count FROM read_parquet('{src}') WHERE ticker IN ({tl}) ORDER BY ticker, bucket) TO '{dst}' (FORMAT PARQUET, COMPRESSION 'zstd')")
    rows += c.execute(f"SELECT count(*) FROM read_parquet('{dst}')").fetchone()[0]
    if (i + 1) % 100 == 0 or i + 1 == len(days):
        el = time.time() - t0; print(f"  {d}  {i+1}/{len(days)} days  {rows:,} rows  {el:.0f}s  ~{el/(i+1)*(len(days)-i-1)/60:.0f}m left", flush=True)
print(f"done: {len(days)-missing} day files, {rows:,} rows, {missing} missing sources, {time.time()-t0:.0f}s", flush=True)
