#!/usr/bin/env python3
"""WHEN does the put-skew signal fire on stocks (user 2026-11-13: only in market downturns, or in regular periods?).
The SH-28 signal: P25 on stocks, put skew (IV25p / IV_ATM) >= the 90th percentile of all PRIOR years, half-spread <= 5%,
no earnings in hold, sold at the bid, delta-hedged (static). Each trade tagged at entry with: SPY drawdown from its
252-day high, SPY 20-day return, the stock's own 20-day return minus SPY's (idiosyncratic move), all total-return.
    cd research && python3 scripts/options/short_dated_regime.py > logs/options_short_dated_regime.log
"""
import os, sys
import duckdb, numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tr_index import add_tr
pd.set_option('display.width', 250)

D = pd.read_parquet('data/options/short_dated/trades.parquet')
from sd_common import drop_bad; D = drop_bad(D)   # SH-36: vanished contracts (corporate actions)
D['mid'] = (D.bid + D.ask) / 2
D = D[(D.mid >= .05) & ~D.earn & (D.inst == 'P25') & (D.kind == 'stock')].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['pl'] = (D.mid * (1 - D.hs) - D.settle) / D.mid + D.dl * (D.Sx - D.S) / D.mid
D['skewp'] = D.iv25p / D.iv_atm; D['sig'] = False
for y in sorted(D.year.unique()):
    past = D.loc[D.year < y, 'skewp'].dropna()
    if len(past) < 2000: continue
    D.loc[(D.year == y) & (D.skewp >= past.quantile(.9)), 'sig'] = True
D = D[(D.hs <= .05) & (D.year > 2012)]

# market + stock state (total-return indexes)
c = duckdb.connect('data/trading.db', read_only=True)
u = duckdb.sql("select distinct root, underlying from 'data/options/universe_daily_full.parquet'").df()
und = u.groupby('root').underlying.first().to_dict()
tl = ','.join(repr(t) for t in set(und.get(r, r) for r in D.root.unique()) | {'SPY'})
da = c.sql(f"select ticker, date, close, n, cum_div from daily_adjusted where ticker in ({tl}) and date >= '2011-01-01' order by ticker, date").df()
da['date'] = pd.to_datetime(da.date); da = add_tr(da)
da['r20'] = da.groupby('ticker').V.pct_change(20)
spy = da[da.ticker == 'SPY'].set_index('date')
spy_dd = spy.V / spy.V.rolling(252, min_periods=60).max() - 1
D['spy_dd'] = D.date.map(spy_dd); D['spy_r20'] = D.date.map(spy.r20)
R20 = da.set_index(['ticker', 'date']).r20
D['und'] = D.root.map(lambda r: und.get(r, r))
D['own_r20'] = R20.reindex(pd.MultiIndex.from_arrays([D.und, D.date])).values
D['idio'] = D.own_r20 - D.spy_r20


def summ(x):
    v = x.pl
    if len(v) < 30: return pd.Series({'n': len(v)})
    w, l = v[v > 0].sum(), -v[v < 0].sum()
    return pd.Series({'n': len(v), 'days': x.date.nunique(), 'mean%': 100 * v.mean(), 'med%': 100 * v.median(), 'PF': w / l,
                      'win%': 100 * (v > 0).mean()})


S, A = D[D.sig], D
for lab, col, bins, labels in [
        ('SPY drawdown from its 1-year high', 'spy_dd', [-1, -.20, -.10, -.05, -.02, 1e-9], ['< -20%', '-20..-10%', '-10..-5%', '-5..-2%', '> -2% (near high)']),
        ('SPY 20-day return', 'spy_r20', [-1, -.05, -.02, 0, .02, 1], ['< -5%', '-5..-2%', '-2..0%', '0..+2%', '> +2%']),
        ("the stock's own 20-day return minus SPY's", 'idio', [-5, -.15, -.05, 0, .05, .15, 5], ['< -15%', '-15..-5%', '-5..0%', '0..+5%', '+5..+15%', '> +15%'])]:
    S_ = S.assign(b=pd.cut(S[col], bins, labels=labels)); A_ = A.assign(b=pd.cut(A[col], bins, labels=labels))
    t = S_.groupby('b', observed=False)[['pl', 'date']].apply(summ)
    t.insert(1, 'share of signal %', 100 * S_.b.value_counts(normalize=True).reindex(labels))
    t.insert(2, 'share of all P25 %', 100 * A_.b.value_counts(normalize=True).reindex(labels))
    t.insert(3, 'signal rate %', 100 * S_.b.value_counts().reindex(labels) / A_.b.value_counts().reindex(labels))
    print(f'\n== {lab} (signal trades, stocks, hedged, at the bid)'); print(t.round(2).to_string())

m = S.assign(ym=S.date.dt.to_period('Q')).groupby('ym').agg(n=('pl', 'size'), names=('root', 'nunique'), days=('date', 'nunique'), mean=('pl', 'mean'))
m['mean'] *= 100
print('\n== signal trades per quarter (n, distinct names, distinct days, mean %)')
print(m.round(1).T.to_string())
nm = S.groupby('root').size().sort_values(ascending=False)
print(f'\n== concentration: {S.root.nunique()} names; top 10 = {100 * nm.head(10).sum() / len(S):.0f}% of trades: ' + ', '.join(f'{r} {n}' for r, n in nm.head(15).items()))
d = S.groupby('date').size()
print(f'== days with a signal: {len(d)} of {A.date.nunique()} trading days; median trades on a signal day {d.median():.0f}, '
      f'top 5% of days hold {100 * d.sort_values(ascending=False).head(int(len(d) * .05)).sum() / len(S):.0f}% of trades')
