#!/usr/bin/env python3
"""Does the put-skew edge grow with a stricter cutoff? (user 2026-11-13). Same trades as skew_split.py (P25, sold at
the bid, daily hedge - 2bp, hs <= 5%, no earnings, 2013+). Cutoffs = quantiles of ALL PRIOR YEARS' trades of the same
kind: top 20 / 10 / 5 / 2 / 1% of the trade's own skew (XS), and for ETFs also of the name's z (TS). Also the
non-overlapping bands (e.g. 90-95th pct) so each band's own expectancy shows.
    cd research && python3 scripts/options/skew_threshold.py > logs/options_skew_threshold.log
"""
import numpy as np, pandas as pd
T = pd.read_parquet('data/options/short_dated/trades.parquet')
from sd_common import drop_bad; T = drop_bad(T)   # SH-36: vanished contracts (corporate actions)
T = T[(T.inst == 'P25') & ~T.earn]
H = pd.read_parquet('data/options/short_dated/hedge.parquet', filters=[('inst', '==', 'P25')])
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['pl'] = (D.mid * (1 - D.hs) - D.settle + D.hd) / D.mid - D.turn * D.S * 2e-4 / D.mid
D['skewp'] = D.iv25p / D.iv_atm
S = D.dropna(subset=['skewp']).groupby(['root', 'date']).skewp.mean().rename('s').reset_index().sort_values(['root', 'date'])
g = S.groupby('root').s
S['lvl'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=60).mean())
S['sd'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=60).std())
S['z'] = (S.s - S.lvl) / S.sd
D = D.merge(S[['root', 'date', 'z']], on=['root', 'date'], how='left')
X = D[(D.hs <= .05) & (D.year > 2012)].copy()
QS = [.8, .9, .95, .98, .99]


def pct_rank(col):
    """the trade's percentile within all PRIOR years' values (same kind)"""
    out = pd.Series(np.nan, index=X.index)
    for k in X.kind.unique():
        base = D[D.kind == k]
        for y in sorted(X.year.unique()):
            p = np.sort(base.loc[base.year < y, col].dropna().values)
            if len(p) < 2000: continue
            m = (X.kind == k) & (X.year == y) & X[col].notna()
            out[m] = np.searchsorted(p, X.loc[m, col].values, 'right') / len(p)
    return out


X['p_xs'] = pct_rank('skewp'); X['p_ts'] = pct_rank('z')


def st(x):
    v = x.pl
    if len(v) < 30: return f'n {len(v):6d}  -'
    dm = x.groupby('date').pl.mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    yp = x.groupby('year').pl.mean()
    return (f'n {len(v):6,d} ({x.root.nunique():3d} names, {x.date.nunique():4d} days)  mean {100 * v.mean():6.1f}%  med {100 * v.median():5.1f}%  '
            f'PF {w / l:4.2f}  t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}  win {100 * (v > 0).mean():4.1f}%  p1 {100 * v.quantile(.01):6.0f}%  '
            f'yrs+ {(yp > 0).sum()}/{len(yp)}  worst yr {100 * yp.min():+.0f}%')


for k, cols in [('stock', ['p_xs']), ('etf', ['p_xs', 'p_ts'])]:
    x = X[X.kind == k]
    for c in cols:
        lab = 'skew (XS)' if c == 'p_xs' else 'z vs own history (TS)'
        print(f'\n== {k}: cutoff on {lab}')
        print(f'   all            {st(x[x[c].notna()])}')
        for q in QS: print(f'   top {100 * (1 - q):4.0f}%       {st(x[x[c] >= q])}')
        print('   bands:')
        for lo, hi in [(.8, .9), (.9, .95), (.95, .98), (.98, .99), (.99, 1.01)]:
            print(f'   {100 * lo:3.0f}-{min(100 * hi, 100):3.0f} pct     {st(x[(x[c] >= lo) & (x[c] < hi)])}')
