#!/usr/bin/env python3
"""SPXW in detail (2026-11-16): cash-settled on the 16:00 SPX close = no after-hours exercise risk (SH-41). Same trades
and P&L definitions as zdte_report.py v2 (sold at the bid; hedged = minute-rebalanced delta hedge with SPY at $0.06 per SPX
unit per unit traded; static = the entry delta held to the close). Day-clustered t. By year, by time of day, and the
features OUT OF SAMPLE (decile edges from 2023-11..2024-12, results 2025-01..2026-11) per time-of-day bucket.
    cd research && python3 scripts/options/zdte_spxw.py > logs/options_zdte_spxw.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
src = open('scripts/options/zdte_report.py').read()
src = src.split("def st(")[0].split('"""', 2)[2].replace("glob.glob('data/options/zdte/trades/*.parquet')", "['data/options/zdte/trades/SPXW.parquet']")
exec(src)
D['tod'] = pd.cut(D.m, [574, 660, 780, 900, 930, 956], labels=['09:35-11', '11-13', '13-15', '15-15:30', '15:30-55'])


def st(x, col='hedged'):
    v = x[col]
    if len(v) < 150: return f'n {len(v):6d}       -'
    dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    return f'n {len(v):7,d} {100 * v.mean():6.1f}% PF {w / l:4.2f} t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f} days+ {100 * (dm > 0).mean():3.0f}%'


print('== SPXW by year (hedged | static | naked)')
for i in ['P25', 'C25', 'STR']:
    for y, x in D[D.inst == i].groupby('year'):
        print(f'  {i:4s} {y}: hedged {st(x)} | static {st(x, "static")} | naked {st(x, "naked")}')
print('\n== SPXW by time of day')
for i in ['P25', 'C25', 'STR']:
    for t, x in D[D.inst == i].groupby('tod', observed=True):
        print(f'  {i:4s} {t:9s}: hedged {st(x)} | static {st(x, "static")} | naked {st(x, "naked")}  hs {100 * x.hs.median():.1f}%')
TR, TE = D[D.date < '2025-01-01'], D[D.date >= '2025-01-01']
print('\n== SPXW OOS: top / bottom decile (edges 2023-11..2024-12) in 2025-26, per time of day (hedged)')
for f in ['skew', 'fslope', 'ivrv30', 'ivrv_open', 'ret_open', 'from_hi', 'from_lo', 'gap', 'vol_put_share']:
    for i in ['P25', 'C25', 'STR']:
        tr = TR[TR.inst == i][f].dropna(); te = TE[TE.inst == i].dropna(subset=[f])
        lo, hi = tr.quantile(.1), tr.quantile(.9)
        print(f'-- {f} {i}: all-day top {st(te[te[f] >= hi])} | bottom {st(te[te[f] <= lo])} | all {st(te)}')
        for t, x in te.groupby('tod', observed=True):
            print(f'     {t:9s} top {st(x[x[f] >= hi])} | bottom {st(x[x[f] <= lo])} | mid {st(x[(x[f] > lo) & (x[f] < hi)])}')
