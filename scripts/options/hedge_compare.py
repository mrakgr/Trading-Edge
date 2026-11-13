#!/usr/bin/env python3
"""Static vs daily-rebalanced delta hedge (hedge_daily.py) vs naked, for the short_dated.py single-leg shorts (user
2026-11-13). Short sold at the BID (f=1), held to expiry; % of the mid premium. daily_c = daily minus the stock-side cost:
turn (shares traded per share of option) x S x 2bp / mid (2bp = a liquid name's half-spread + fees per share traded).
Signal = SH-28: put skew (IV25p / IV_ATM) >= the 90th percentile of all prior years (per kind), half-spread <= 5%.
    cd research && python3 scripts/options/hedge_compare.py > logs/options_hedge_compare.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250)
T = pd.read_parquet('data/options/short_dated/trades.parquet')
T = T[T.inst.isin(['AP', 'AC', 'P25', 'C25'])]
H = pd.read_parquet('data/options/short_dated/hedge.parquet')
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
print(f'trades {len(T):,}, with a daily hedge {len(D):,}')
D['mid'] = (D.bid + D.ask) / 2
D = D[(D.mid >= .05) & ~D.earn].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['naked'] = (D.mid * (1 - D.hs) - D.settle) / D.mid
D['static'] = D.naked + D.dl * (D.Sx - D.S) / D.mid
D['daily'] = D.naked + D.hd / D.mid
D['daily_c'] = D.daily - D.turn * D.S * 2e-4 / D.mid
D['skewp'] = D.iv25p / D.iv_atm; D['sig'] = False
P = D.inst == 'P25'
for k in ['stock', 'etf']:
    g = D[P & (D.kind == k)]
    for y in sorted(g.year.unique()):
        past = g.loc[g.year < y, 'skewp'].dropna()
        if len(past) < 2000: continue
        D.loc[g.index[(g.year == y) & (g.skewp >= past.quantile(.9))], 'sig'] = True


def st(x, c):
    v = x[c]; dm = x.groupby('date')[c].mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    return (f'{c:8s} mean {100 * v.mean():6.1f}%  med {100 * v.median():6.1f}%  PF {w / l:4.2f}  t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}  '
            f'win {100 * (v > 0).mean():4.1f}%  sd {100 * v.std():4.0f}%  p5 {100 * v.quantile(.05):6.0f}%  p1 {100 * v.quantile(.01):6.0f}%  worst {100 * v.min():6.0f}%')


sets = [('SIGNAL: P25 steep put skew, hs<=5%', lambda k: P & D.sig & (D.hs <= .05) & (D.year > 2012) & (D.kind == k)),
        ('all P25, hs<=5%', lambda k: P & (D.hs <= .05) & (D.kind == k)),
        ('all AP (ATM put), hs<=5%', lambda k: (D.inst == 'AP') & (D.hs <= .05) & (D.kind == k)),
        ('all C25, hs<=5%', lambda k: (D.inst == 'C25') & (D.hs <= .05) & (D.kind == k))]
for lab, f in sets:
    for k in ['stock', 'etf']:
        x = D[f(k)]
        print(f'\n== {lab} - {k}: n={len(x):,}  turnover {x.turn.mean():.2f} shares/share, cost {100 * (x.turn * x.S * 2e-4 / x.mid).mean():.1f}% of premium')
        for c in ['naked', 'static', 'daily', 'daily_c']: print('  ' + st(x, c))
        if lab.startswith('SIGNAL'):
            yr = x.groupby('year')[['naked', 'static', 'daily_c']].mean() * 100
            print('   by year: ' + '  '.join(f'{y}: {r.naked:+.0f}/{r.static:+.0f}/{r.daily_c:+.0f}' for y, r in yr.iterrows()) + '   (naked/static/daily_c %)')
