#!/usr/bin/env python3
"""Per (root, date): option volume, trade count, contracts quoted - from the whole-market ThetaData EOD pull
(data/options/market_full, 2012-06 -> 2026-11). The input of the point-in-time universe screen for the full history
(SH-5 used Massive's options day aggregates, which start 2022-11).
    cd research && python3 scripts/options/root_volume_full.py > logs/options_root_volume_full.log 2>&1
writes data/options/root_volume_full.parquet (root, date, volume, trades, contracts, oi-free)"""
import glob, os, time
import duckdb

t0 = time.time()
fs = sorted(glob.glob('data/options/market_full/*.parquet'))
print(f'{len(fs):,} chunk files', flush=True)
con = duckdb.connect()
con.execute("SET memory_limit='2GB'; SET threads=2; SET preserve_insertion_order=false")
parts = []
B = 2000
for i in range(0, len(fs), B):
    parts.append(con.sql(f"""select root, date, sum(volume)::BIGINT volume, sum(count)::BIGINT trades, count(*)::INT contracts
                         from read_parquet({fs[i:i + B]}) group by all""").df())
    print(f'{min(i + B, len(fs)):,}/{len(fs):,} files  {time.time() - t0:.0f}s', flush=True)
import pandas as pd
d = pd.concat(parts, ignore_index=True).groupby(['root', 'date'], as_index=False).sum()
d.to_parquet('data/options/root_volume_full.parquet', index=False)
print(f'wrote {len(d):,} rows, {d.root.nunique():,} roots, {d.date.min()} -> {d.date.max()}  {time.time() - t0:.0f}s')
