#!/usr/bin/env python3
"""IS THE PUT-SKEW EDGE TIMING OR NAME SELECTION? (user 2026-11-13). P25 shorts from short_dated.py, sold at the BID,
DAILY delta hedge - 2bp per share traded (hedge_daily.py), half-spread <= 5%, no earnings in hold, % of the mid premium.

Per root-day: s = the mean put skew (IV25p / IV_ATM) over that day's 1-10 day expiries (all P25 trades, any spread).
  lvl = the mean of s over the PREVIOUS 252 trading rows of the root (min 60)       -> 'puts are always steep here'
  z   = (s - lvl) / sd of s over those same previous rows                          -> 'puts are unusually steep today'
Signals (thresholds = quantiles of ALL PRIOR YEARS' trades of the same kind; 2012 and the first year of each name skipped):
  XS  = the trade's own skew >= the prior 90th pct (SH-28)   LVL = lvl >= prior 90th   TS = z >= prior 90th
plus a 3x3 grid of lvl terciles x z terciles (prior-year terciles), and the forward slope (SH-30) terciles inside.
    cd research && python3 scripts/options/skew_split.py > logs/options_skew_split.log
"""
import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250)
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
# forward slope (SH-30)
TD = np.array(sorted(duckdb.connect('data/trading.db', read_only=True).sql(
    "select distinct date from daily_adjusted where ticker = 'SPY' and date >= '2010-06-01'").df().date), dtype='datetime64[D]')
ti = np.searchsorted(TD, D.date.values.astype('datetime64[D]'), 'right') - 1
expB = (D.date + pd.to_timedelta(D.cdte30, unit='D')).values.astype('datetime64[D]')
TB = np.where(np.isfinite(D.cdte30), np.searchsorted(TD, expB, 'right') - 1 - ti, np.nan) / 252
fv = (D.iv30 ** 2 * TB - D.iv_atm ** 2 * D.tdte / 252) / (TB - D.tdte / 252)
D['fslope'] = D.iv_atm / np.sqrt(fv.where(fv > 0)) - 1
# root-day skew series: level and z vs the root's own previous 252 rows
S = D.dropna(subset=['skewp']).groupby(['root', 'date']).skewp.mean().rename('s').reset_index().sort_values(['root', 'date'])
g = S.groupby('root').s
S['lvl'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=60).mean())
S['sd'] = g.transform(lambda x: x.shift(1).rolling(252, min_periods=60).std())
S['z'] = (S.s - S.lvl) / S.sd
D = D.merge(S[['root', 'date', 'lvl', 'z']], on=['root', 'date'], how='left')
X = D[(D.hs <= .05) & (D.year > 2012)].copy()
print(f'P25 trades hs<=5%, 2013+: {len(X):,}; with lvl/z: {X.z.notna().sum():,}')


def prior_q(d, col, qs):
    """per kind, per year: the quantiles of col over all PRIOR years' trades (of D, any spread, same kind)"""
    out = {q: pd.Series(np.nan, index=d.index) for q in qs}
    for k in d.kind.unique():
        base = D[D.kind == k]
        for y in sorted(d.year.unique()):
            p = base.loc[base.year < y, col].dropna()
            if len(p) < 2000: continue
            m = (d.kind == k) & (d.year == y)
            for q in qs: out[q][m] = p.quantile(q)
    return out


for col in ['skewp', 'lvl', 'z', 'fslope']:
    q = prior_q(X, col, [1 / 3, 2 / 3, .9])
    X[f'{col}_hi10'] = X[col] >= q[.9]
    X[f'{col}_t'] = np.select([X[col] < q[1 / 3], X[col] < q[2 / 3], X[col] >= q[2 / 3]], [1, 2, 3], 0)
    X.loc[X[col].isna() | q[.9].isna(), f'{col}_t'] = 0


def st(x):
    v = x.pl
    if len(v) < 50: return f'n {len(v):5d}  -'
    dm = x.groupby('date').pl.mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    yp = (x.groupby('year').pl.mean() > 0)
    return (f'n {len(v):6,d} {100 * v.mean():6.1f}% med {100 * v.median():5.1f}% PF {w / l:4.2f} t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f} '
            f'yrs+ {yp.sum()}/{len(yp)}')


for k in ['stock', 'etf']:
    x = X[(X.kind == k) & X.z.notna()]
    print(f'\n=================== {k} (rows with a name history: {len(x):,})')
    print(f'  all P25                 {st(x)}')
    print(f'  XS  (skew top 10%)      {st(x[x.skewp_hi10])}')
    print(f'  LVL (level top 10%)     {st(x[x.lvl_hi10])}')
    print(f'  TS  (z top 10%)         {st(x[x.z_hi10])}')
    print(f'  XS & TS                 {st(x[x.skewp_hi10 & x.z_hi10])}')
    print(f'  XS & not TS             {st(x[x.skewp_hi10 & ~x.z_hi10])}')
    print(f'  TS & not XS             {st(x[x.z_hi10 & ~x.skewp_hi10])}')
    print(f'  XS & LVL                {st(x[x.skewp_hi10 & x.lvl_hi10])}')
    print(f'  XS & not LVL            {st(x[x.skewp_hi10 & ~x.lvl_hi10])}')
    xs = x[x.skewp_hi10]
    print(f'  of the XS trades: {100 * xs.lvl_hi10.mean():.0f}% have a top-10% level, {100 * xs.z_hi10.mean():.0f}% a top-10% z; '
          f'median z {xs.z.median():.2f} (all: {x.z.median():.2f})')
    print('  -- grid: rows = level tercile (1 = flattest names), cols = z tercile (1 = flatter than usual today)')
    for lt in [1, 2, 3]:
        print(f'     lvl {lt}: ' + ' | '.join(f'z{zt}: {st(x[(x.lvl_t == lt) & (x.z_t == zt)])}' for zt in [1, 2, 3]))
    print('  -- forward slope tercile inside: XS / TS / LVL top 10%')
    for lab, m in [('XS', x.skewp_hi10), ('TS', x.z_hi10), ('LVL', x.lvl_hi10)]:
        print(f'     {lab:3s}: ' + ' | '.join(f'fs{t}: {st(x[m & (x.fslope_t == t)])}' for t in [1, 2, 3]))
    for lab, m in [('XS', x.skewp_hi10), ('TS', x.z_hi10), ('LVL', x.lvl_hi10), ('XS & fs3', x.skewp_hi10 & (x.fslope_t == 3))]:
        yr = x[m].groupby('year').pl.mean() * 100
        print(f'  {lab:8s} by year: ' + '  '.join(f'{y}:{v:+.0f}' for y, v in yr.items()))
    nm = x[x.lvl_hi10].groupby('root').size().sort_values(ascending=False).head(12)
    print('  LVL names: ' + ', '.join(f'{r} {n}' for r, n in nm.items()))
    nm = x[x.z_hi10].groupby('root').size().sort_values(ascending=False)
    print(f'  TS names: {x[x.z_hi10].root.nunique()}, top 12 = {100 * nm.head(12).sum() / max(nm.sum(), 1):.0f}%: ' + ', '.join(f'{r} {n}' for r, n in nm.head(12).items()))
