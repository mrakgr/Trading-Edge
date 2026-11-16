#!/usr/bin/env python3
"""Checks on the first 0DTE report (2026-11-16): (1) the hedge decomposed - naked, the minute hedge gross, its stock cost
(0.5bp of S per share traded), a STATIC hedge (entry delta held to the close, 2 x |delta| x 0.5bp); (2) the OOS top-decile
fslope / ivrv30 signals (edges from 2023-11..2024-12, results 2025+) BY TIME OF DAY and their mins_left mix.
    cd research && python3 scripts/options/zdte_check.py > logs/options_zdte_check.log
"""
import glob
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
exec(open('scripts/options/zdte_report.py').read().split("def st(")[0].split('"""', 2)[2])   # the same trade table
D['static'] = (D.bid - D.settle + D.dl * (D.close - D.S) - 2 * D.dl.abs() * D.S * 0.5e-4) / D.mid
D['hd_gross'] = D.hd / D.mid; D['hd_cost'] = D.turn * D.S * 0.5e-4 / D.mid
D['tod'] = pd.cut(D.m, [574, 660, 780, 900, 930, 956], labels=['09:35-11', '11-13', '13-15', '15-15:30', '15:30-55'])
print('== (1) hedge decomposition, mean % of premium (index / etf / stock) x inst')
g = D.groupby(['grp', 'inst'])
T = pd.DataFrame({'naked': g.naked.mean(), 'hedge_gross': g.hd_gross.mean(), 'hedge_cost': -g.hd_cost.mean(),
                  'minute_hedged': g.hedged.mean(), 'static_hedged': g.static.mean(), 'turn': g.turn.mean()}) * [100, 100, 100, 100, 100, 1]
print(T.round(2).to_string())
print('\n   by time of day (index only):')
x = D[D.grp == 'index']; g = x.groupby(['inst', 'tod'], observed=True)
T = pd.DataFrame({'naked': g.naked.mean(), 'hedge_gross': g.hd_gross.mean(), 'hedge_cost': -g.hd_cost.mean(),
                  'minute_hedged': g.hedged.mean(), 'static_hedged': g.static.mean(), 'turn': g.turn.mean()}) * [100, 100, 100, 100, 100, 1]
print(T.round(2).to_string())


def st(x, col='hedged'):
    v = x[col]
    if len(v) < 200: return f'n {len(v):6d}  -'
    dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    return f'n {len(v):7,d} {100 * v.mean():6.1f}% PF {w / l:4.2f} t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f} days+ {100 * (dm > 0).mean():3.0f}%'


TR, TE = D[D.date < '2025-01-01'], D[D.date >= '2025-01-01']
print('\n== (2) OOS top decile (edges from 2023-11..2024-12, results 2025+): by time of day; naked / minute-hedged / static')
for f in ['fslope', 'ivrv30', 'ivrv_open']:
    for (gp, i), te in TE.groupby(['grp', 'inst']):
        tr = TR[(TR.grp == gp) & (TR.inst == i)][f].dropna()
        if len(tr) < 5000: continue
        hi = tr.quantile(.9); top = te[te[f] >= hi]; rest = te[te[f] < hi]
        print(f'-- {f} {gp} {i}: top {len(top):,} trades; mins_left median top {top.mins_left.median():.0f} vs all {te.mins_left.median():.0f}')
        for tod, xx in top.groupby('tod', observed=True):
            rr = rest[rest.tod == tod]
            print(f'     {tod:9s} TOP hedged {st(xx)} | static {st(xx, "static")} | naked {st(xx, "naked")} || REST hedged {st(rr)}')
