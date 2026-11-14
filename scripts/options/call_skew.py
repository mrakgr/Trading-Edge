#!/usr/bin/env python3
"""THE CALL SIDE (user 2026-11-13: does selling 25-delta calls work when CALL skew and front-vs-forward are rich, now with
the daily delta hedge?). C25 shorts from short_dated.py, sold at the bid, daily hedge - 2bp (hedge_daily.py), hs <= 5%,
no earnings in hold, 2013+, % of the mid premium. Call skew c = IV25c / IV_ATM.
  XS = the trade's c >= the prior years' quantile (same kind): top 20 / 10 / 5 / 2%.
  TS = the name's root-day c vs its previous 252 rows, z >= the prior 90th pct.  fslope (SH-30) terciles inside.
Also the put-signal overlap: how often the same root-day carries a top-10% PUT skew too (a smile, not a call skew).
    cd research && python3 scripts/options/call_skew.py > logs/options_call_skew.log
"""
import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250)
T = pd.read_parquet('data/options/short_dated/trades.parquet')
from sd_common import drop_bad; T = drop_bad(T)   # SH-36: vanished contracts (corporate actions)
T = T[(T.inst == 'C25') & ~T.earn]
H = pd.read_parquet('data/options/short_dated/hedge.parquet', filters=[('inst', '==', 'C25')])
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['pl'] = (D.mid * (1 - D.hs) - D.settle + D.hd) / D.mid - D.turn * D.S * 2e-4 / D.mid
D['naked'] = (D.mid * (1 - D.hs) - D.settle) / D.mid
D['c'] = D.iv25c / D.iv_atm; D['p'] = D.iv25p / D.iv_atm
TD = np.array(sorted(duckdb.connect('data/trading.db', read_only=True).sql(
    "select distinct date from daily_adjusted where ticker = 'SPY' and date >= '2010-06-01'").df().date), dtype='datetime64[D]')
ti = np.searchsorted(TD, D.date.values.astype('datetime64[D]'), 'right') - 1
expB = (D.date + pd.to_timedelta(D.cdte30, unit='D')).values.astype('datetime64[D]')
TB = np.where(np.isfinite(D.cdte30), np.searchsorted(TD, expB, 'right') - 1 - ti, np.nan) / 252
fv = (D.iv30 ** 2 * TB - D.iv_atm ** 2 * D.tdte / 252) / (TB - D.tdte / 252)
D['fslope'] = D.iv_atm / np.sqrt(fv.where(fv > 0)) - 1
S = D.dropna(subset=['c']).groupby(['root', 'date']).c.mean().rename('s').reset_index().sort_values(['root', 'date'])
g = S.groupby('root').s
S['lvl'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=60).mean())
S['sd'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=60).std())
S['z'] = (S.s - S.lvl) / S.sd
D = D.merge(S[['root', 'date', 'z']], on=['root', 'date'], how='left')
X = D[(D.hs <= .05) & (D.year > 2012)].copy()


def pct_rank(col):
    out = pd.Series(np.nan, index=X.index)
    for k in X.kind.unique():
        base = D[D.kind == k]
        for y in sorted(X.year.unique()):
            p = np.sort(base.loc[base.year < y, col].dropna().values)
            if len(p) < 2000: continue
            m = (X.kind == k) & (X.year == y) & X[col].notna()
            out[m] = np.searchsorted(p, X.loc[m, col].values, 'right') / len(p)
    return out


X['p_c'] = pct_rank('c'); X['p_z'] = pct_rank('z'); X['p_f'] = pct_rank('fslope'); X['p_p'] = pct_rank('p')


def st(x, col='pl'):
    v = x[col]
    if len(v) < 30: return f'n {len(v):6d}  -'
    dm = x.groupby('date')[col].mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    yp = x.groupby('year')[col].mean()
    return (f'n {len(v):6,d} ({x.root.nunique():3d} names)  mean {100 * v.mean():6.1f}%  med {100 * v.median():5.1f}%  PF {w / l:4.2f}  '
            f't {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}  p1 {100 * v.quantile(.01):6.0f}%  yrs+ {(yp > 0).sum()}/{len(yp)}  worst yr {100 * yp.min():+.0f}%')


for k in ['stock', 'etf']:
    x = X[X.kind == k]
    print(f'\n=================== {k}: short 25-delta CALL')
    print(f'   all, daily hedge   {st(x)}')
    print(f'   all, naked         {st(x, "naked")}')
    for q in [.8, .9, .95, .98]:
        m = x.p_c >= q
        print(f'   call skew top {100 * (1 - q):3.0f}%  hedged {st(x[m])}')
        print(f'                      naked  {st(x[m], "naked")}')
    m = x.p_z >= .9
    print(f'   TS: z top 10%       {st(x[m])}')
    xs = x[x.p_c >= .9]
    print(f'   inside call-skew top 10%, fslope terciles (prior years): ' + ' | '.join(
        f'fs{i}: {st(xs[(xs.p_f >= lo) & (xs.p_f < hi)])}' for i, (lo, hi) in enumerate([(0, 1 / 3), (1 / 3, 2 / 3), (2 / 3, 1.01)], 1)))
    print(f'   of the call-skew top-10% trades, {100 * (xs.p_p >= .9).mean():.0f}% also have a top-10% put skew (smile); without them: {st(xs[xs.p_p < .9])}')
    yr = xs.groupby('year').pl.mean() * 100
    print('   call-skew top 10% by year: ' + '  '.join(f'{y}:{v:+.0f}' for y, v in yr.items()))
    nm = xs.groupby('root').size().sort_values(ascending=False)
    print(f'   names: {xs.root.nunique()}, top 10 = {100 * nm.head(10).sum() / max(len(xs), 1):.0f}%: ' + ', '.join(f'{r} {n}' for r, n in nm.head(15).items()))
    print(f'   median call skew in the top 10%: {xs.c.median():.3f} (all: {x.c.median():.3f})')
