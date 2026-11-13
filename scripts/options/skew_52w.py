#!/usr/bin/env python3
"""52-WEEK HIGH as a filter on the skew shorts (user 2026-11-13: in breakout trading a close at a 52w high matters - does it
for the call-skew trades?). C25 and P25 shorts, sold at the bid, daily hedge - 2bp, hs <= 5%, no earnings, 2013+.
Signal = the trade's own skew (IV25c/IV_ATM for calls, IV25p/IV_ATM for puts) >= the prior years' 90th pct (same kind).
At the entry close t, on the underlying's SPLIT-adjusted close (close x n, daily_adjusted, causal):
  hi52 = close >= the max of the previous 252 closes;  lo52 = close <= their min;
  dist = close / max(previous 252) - 1  (bands: >= 0 at the high, 0..-5%, -5..-15%, -15..-30%, < -30%).
    cd research && python3 scripts/options/skew_52w.py > logs/options_skew_52w.log
"""
import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250)
T = pd.read_parquet('data/options/short_dated/trades.parquet')
T = T[T.inst.isin(['C25', 'P25']) & ~T.earn]
H = pd.read_parquet('data/options/short_dated/hedge.parquet', filters=[('inst', 'in', ['C25', 'P25'])])
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['pl'] = (D.mid * (1 - D.hs) - D.settle + D.hd) / D.mid - D.turn * D.S * 2e-4 / D.mid
D['sk'] = np.where(D.inst == 'C25', D.iv25c, D.iv25p) / D.iv_atm
# 52-week features of the underlying
u = duckdb.sql("select distinct root, underlying from 'data/options/universe_daily_full.parquet'").df().groupby('root').underlying.first()
D['und'] = D.root.map(lambda r: u.get(r, r))
c = duckdb.connect('data/trading.db', read_only=True)
tl = ','.join(repr(t) for t in D.und.unique())
da = c.sql(f"select ticker, date, close, n from daily_adjusted where ticker in ({tl}) and date >= '2011-01-01' order by ticker, date").df()
da['date'] = pd.to_datetime(da.date)
da['seg'] = (da.groupby('ticker').date.diff().dt.days > 45).groupby(da.ticker).cumsum()   # reused tickers: break at gaps
da['pa'] = da.close * da.n
g = da.groupby(['ticker', 'seg']).pa
da['mx'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=200).max())
da['mn'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=200).min())
da['dist'] = da.pa / da.mx - 1
da['hi52'] = da.pa >= da.mx; da['lo52'] = da.pa <= da.mn
D = D.merge(da[['ticker', 'date', 'dist', 'hi52', 'lo52', 'mx']].rename(columns={'ticker': 'und'}), on=['und', 'date'], how='left')
D[['hi52', 'lo52']] = D[['hi52', 'lo52']].astype(object); D.loc[D.mx.isna(), ['hi52', 'lo52']] = np.nan
X = D[(D.hs <= .05) & (D.year > 2012)].copy()
X['p_sk'] = np.nan
for (k, i), gg in X.groupby(['kind', 'inst']):
    base = D[(D.kind == k) & (D.inst == i)]
    for y in sorted(gg.year.unique()):
        p = np.sort(base.loc[base.year < y, 'sk'].dropna().values)
        if len(p) < 2000: continue
        m = gg.index[(gg.year == y) & gg.sk.notna()]
        X.loc[m, 'p_sk'] = np.searchsorted(p, X.loc[m, 'sk'].values, 'right') / len(p)
X['band'] = pd.cut(X.dist, [-1, -.30, -.15, -.05, -1e-12, 10], labels=['< -30%', '-15..-30%', '-5..-15%', '0..-5%', 'AT 52w high'])


def st(x):
    v = x.pl
    if len(v) < 30: return f'n {len(v):6d}  -'
    dm = x.groupby('date').pl.mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    yp = x.groupby('year').pl.mean()
    return (f'n {len(v):6,d} ({x.root.nunique():3d} names)  mean {100 * v.mean():6.1f}%  med {100 * v.median():5.1f}%  PF {w / l:4.2f}  '
            f't {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}  p1 {100 * v.quantile(.01):6.0f}%  yrs+ {(yp > 0).sum()}/{len(yp)}  worst yr {100 * yp.min():+.0f}%')


for i in ['C25', 'P25']:
    for k in ['stock', 'etf']:
        x = X[(X.kind == k) & (X.inst == i) & X.dist.notna()]
        side = 'CALL' if i == 'C25' else 'PUT'
        print(f'\n=================== {k}: short 25-delta {side}, daily hedge, at the bid')
        for lab, m in [('all', x.p_sk.notna()), ('skew top 10%', x.p_sk >= .9), ('skew top 5%', x.p_sk >= .95)]:
            xx = x[m]
            print(f'  -- {lab}: share at a 52w high {100 * (xx.hi52 == True).mean():.1f}%, at a 52w low {100 * (xx.lo52 == True).mean():.1f}%')
            print(f'     at 52w high     {st(xx[xx.hi52 == True])}')
            print(f'     not at the high {st(xx[xx.hi52 == False])}')
            print(f'     at 52w low      {st(xx[xx.lo52 == True])}')
            if lab != 'all':
                for b in xx.band.cat.categories:
                    print(f'       {b:12s}  {st(xx[xx.band == b])}')
        xs = x[(x.p_sk >= .9) & (x.hi52 == True)]
        if len(xs) >= 30:
            yr = xs.groupby('year').pl.agg(['size', 'mean'])
            print('     skew top 10% AND at the 52w high, by year: ' + '  '.join(f'{y}:{100 * r["mean"]:+.0f}%({int(r["size"])})' for y, r in yr.iterrows()))
            nm = xs.groupby('root').size().sort_values(ascending=False)
            print('     names: ' + ', '.join(f'{r} {n}' for r, n in nm.head(15).items()))
