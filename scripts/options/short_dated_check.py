#!/usr/bin/env python3
"""Robustness of the short_dated.py signals (user 2026-11-13). Signals = the top decile of a feature per (kind, inst), with
the threshold from the PRIOR years only (expanding: year Y uses the 90th percentile of all trades before Y; 2012 skipped).
P&L of the short, delta-hedged (static, to expiry), % of the mid premium at f = 0 / .5 / 1 (1 = sold at the bid).
Cuts: all, half-spread <= 5%, the more liquid half of names that day (liq >= .5). By year at f=.5, hs <= 5%.
    cd research && python3 scripts/options/short_dated_check.py > logs/options_short_dated_check.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
D = pd.read_parquet('data/options/short_dated/trades.parquet')
D['mid'] = (D.bid + D.ask) / 2
D = D[(D.mid >= 0.05) & ~D.earn].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid
D['hedge'] = D.dl * (D.Sx - D.S) / D.mid
for f in (0, .5, 1):
    D[f'h{int(f * 100)}'] = (D.mid * (1 - f * D.hs) - D.settle) / D.mid + D.hedge
D['year'] = D.date.dt.year
D['ivrv'] = D.iv_atm / D.rv20; D['slope'] = D.iv_atm / D.iv30; D['skewp'] = D.iv25p / D.iv_atm
D['liq'] = D.groupby('date').vol20.rank(pct=True)


def expanding_top(d, feat, q=.9):
    """flag = feat >= the q-quantile of the same (kind, inst)'s trades in all PRIOR years"""
    flag = pd.Series(False, index=d.index)
    for (k, i), g in d.groupby(['kind', 'inst']):
        for y in sorted(g.year.unique()):
            past = g.loc[g.year < y, feat].dropna()
            if len(past) < 2000: continue
            th = past.quantile(q)
            idx = g.index[(g.year == y) & (g[feat] >= th)]
            flag[idx] = True
    return flag


def s(x, col):
    v = x[col].dropna()
    if len(v) < 50: return '        -'
    dm = x.groupby('date')[col].mean(); t = dm.mean() / dm.std() * np.sqrt(len(dm))
    w, l = v[v > 0].sum(), -v[v < 0].sum()
    return f'{100 * v.mean():6.1f}% PF {w / l:4.2f} t {t:5.1f}'


for feat, insts in [('skewp', ['P25', 'STR']), ('ivrv', ['STR', 'P25', 'C25']), ('slope', ['STR', 'P25'])]:
    D['sig'] = expanding_top(D, feat)
    print(f'\n===== {feat} top decile (expanding threshold), delta-hedged')
    for kind in ['stock', 'etf']:
        for inst in insts:
            base = D[(D.kind == kind) & (D.inst == inst) & (D.year > 2012)]
            for cut, m in [('all', base.hs >= 0), ('hs<=5%', base.hs <= .05), ('liq>=.5', base.liq >= .5),
                           ('hs<=5% & liq>=.5', (base.hs <= .05) & (base.liq >= .5))]:
                x = base[m]; sg = x[x.sig]
                print(f'{kind:5s} {inst:4s} {cut:17s} n={len(sg):7,d} ({100 * len(sg) / max(len(x), 1):4.1f}%) | mid {s(sg, "h0")} | f.5 {s(sg, "h50")} | bid {s(sg, "h100")} || rest at bid {s(x[~x.sig], "h100")}')
            x = base[(base.hs <= .05) & base.sig]
            yr = x.groupby('year').agg(n=('h50', 'size'), avg=('h50', 'mean'))
            yr['PF'] = x.groupby('year').h50.apply(lambda v: v[v > 0].sum() / -v[v < 0].sum())
            print('      by year f=.5 hs<=5%: ' + '  '.join(f'{y}:{100 * r.avg:+.0f}%/{r.PF:.2f}({int(r.n)})' for y, r in yr.iterrows()))
