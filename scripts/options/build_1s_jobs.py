#!/usr/bin/env python3
"""The 1-SECOND 0DTE job list (user 2026-11-20): per (root, 0DTE day) the strikes the every-minute system actually used
(zdte_trades.py: the 25d put / call and the ATM strike at any minute) plus their neighbour strike on each side (from that
day's 1-minute 0DTE quotes). Writes ROOT<TAB>DATE<TAB>K1,K2,... for intraday_pull.py --interval 1s.
    cd research && python3 scripts/options/build_1s_jobs.py --roots SPY,QQQ,TSLA --out data/options/intraday_1s/jobs.tsv
"""
import argparse, os
import numpy as np, pandas as pd
ap = argparse.ArgumentParser()
ap.add_argument('--roots', default='SPY,QQQ,TSLA'); ap.add_argument('--out', required=True)
a = ap.parse_args()
lines = []
for r in a.roots.split(','):
    T = pd.read_parquet(f'data/options/zdte/trades/{r}.parquet', columns=['date', 'strike'])
    used = T.groupby('date').strike.unique()
    for d, ks in used.items():
        ds = pd.Timestamp(d).strftime('%Y%m%d')
        q = pd.read_parquet(f'data/options/intraday_0dte/{r}__{ds}.quote.parquet', columns=['expiration', 'strike'])
        allk = np.sort(q[q.expiration == pd.Timestamp(d).strftime('%Y-%m-%d')].strike.unique())
        idx = set()
        for k in ks:
            i = np.searchsorted(allk, k)
            idx |= {j for j in (i - 1, i, i + 1) if 0 <= j < len(allk)}
        sel = sorted(allk[sorted(idx)])
        lines.append(f"{r}\t{pd.Timestamp(d):%Y-%m-%d}\t{','.join(f'{k:g}' for k in sel)}")
    print(r, len(used), 'days', flush=True)
os.makedirs(os.path.dirname(a.out), exist_ok=True)
open(a.out, 'w').write('\n'.join(lines) + '\n')
n = [len(l.split('\t')[2].split(',')) for l in lines]
print(f'{len(lines):,} jobs, strikes per job median {np.median(n):.0f}, p90 {np.percentile(n, 90):.0f}, total {sum(n):,} strike-requests -> {a.out}')
