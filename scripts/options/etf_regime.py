#!/usr/bin/env python3
"""WHEN do the ETF put-skew signals fire (user 2026-11-13: only in downturns or in regular trading too?). P25 on the 20
ETFs, sold at the bid, daily hedge - 2bp, hs <= 5%, no earnings, 2013+; XS = skew >= the prior years' 90th pct (top 5%
also shown), TS = the ETF's z vs its own previous 252 days >= the prior 90th pct. Each trade tagged with SPY's drawdown
from its 252-day high and SPY's 20-day total return at entry. Per ETF: trades, days, mean.
    cd research && python3 scripts/options/etf_regime.py > logs/options_etf_regime.log
"""
import duckdb, numpy as np, pandas as pd, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tr_index import add_tr
pd.set_option('display.width', 250)
T = pd.read_parquet('data/options/short_dated/trades.parquet')
T = T[(T.inst == 'P25') & ~T.earn & (T.kind == 'etf')]
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
for col, nm in [('skewp', 'p_xs'), ('z', 'p_ts')]:
    X[nm] = np.nan
    for y in sorted(X.year.unique()):
        p = np.sort(D.loc[D.year < y, col].dropna().values)
        if len(p) < 2000: continue
        m = (X.year == y) & X[col].notna()
        X.loc[m, nm] = np.searchsorted(p, X.loc[m, col].values, 'right') / len(p)
c = duckdb.connect('data/trading.db', read_only=True)
spy = c.sql("select ticker, date, close, n, cum_div from daily_adjusted where ticker = 'SPY' and date >= '2011-01-01' order by date").df()
spy['date'] = pd.to_datetime(spy.date); spy = add_tr(spy).set_index('date')
X['spy_dd'] = X.date.map(spy.V / spy.V.rolling(252, min_periods=60).max() - 1)
X['spy_r20'] = X.date.map(spy.V.pct_change(20))


def summ(x):
    v = x.pl
    if len(v) < 30: return pd.Series({'n': len(v), 'days': x.date.nunique(), 'mean%': np.nan, 'med%': np.nan, 'PF': np.nan})
    w, l = v[v > 0].sum(), -v[v < 0].sum()
    return pd.Series({'n': len(v), 'days': x.date.nunique(), 'mean%': 100 * v.mean(), 'med%': 100 * v.median(), 'PF': w / l})


sigs = {'XS top 10%': X.p_xs >= .9, 'XS top 5%': X.p_xs >= .95, 'TS top 10%': X.p_ts >= .9, 'XS or TS top 10%': (X.p_xs >= .9) | (X.p_ts >= .9)}
for lab, col, bins, labels in [
        ('SPY drawdown from its 1-year high', 'spy_dd', [-1, -.20, -.10, -.05, -.02, 1e-9], ['< -20%', '-20..-10%', '-10..-5%', '-5..-2%', '> -2% (near high)']),
        ('SPY 20-day return', 'spy_r20', [-1, -.05, -.02, 0, .02, 1], ['< -5%', '-5..-2%', '-2..0%', '0..+2%', '> +2%'])]:
    allb = pd.cut(X[col], bins, labels=labels)
    print(f'\n===== {lab}: share of ALL ETF P25 trading days: ' + '  '.join(f'{l} {100 * v:.0f}%' for l, v in X.groupby(allb, observed=False).date.nunique().div(X.date.nunique()).items()))
    for sl, m in sigs.items():
        x = X[m]; b = pd.cut(x[col], bins, labels=labels)
        t = x.groupby(b, observed=False)[['pl', 'date']].apply(summ)
        t.insert(1, 'share %', 100 * t.n / t.n.sum())
        print(f'-- {sl}'); print(t.round(2).to_string())
print('\n===== per ETF (XS or TS top 10%): trades, signal days, mean, PF; and the share of its trading days with a signal')
m = sigs['XS or TS top 10%']
rows = []
for r, x in X.groupby('root'):
    s = x[m.loc[x.index]]
    v = s.pl; w, l = v[v > 0].sum(), -v[v < 0].sum()
    rows.append({'etf': r, 'from': x.date.min().date(), 'trades': len(s), 'signal days': s.date.nunique(),
                 'share of days %': 100 * s.date.nunique() / x.date.nunique(), 'mean%': 100 * v.mean() if len(v) else np.nan,
                 'PF': w / l if l > 0 else np.nan, 'all trades mean%': 100 * x.pl.mean()})
print(pd.DataFrame(rows).sort_values('trades', ascending=False).round(2).to_string(index=False))
d = X[m].groupby('date').size()
print(f'\nsignal days {len(d)} of {X.date.nunique()} ({100 * len(d) / X.date.nunique():.0f}%); median trades per signal day {d.median():.0f}; '
      f'top 5% of signal days hold {100 * d.sort_values(ascending=False).head(int(len(d) * .05)).sum() / d.sum():.0f}% of the trades')
q = X[m].assign(yq=X.date.dt.to_period('Y')).groupby('yq').agg(n=('pl', 'size'), days=('date', 'nunique'), mean=('pl', 'mean'))
q['mean'] *= 100
print(q.round(1).T.to_string())
