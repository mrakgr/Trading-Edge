#!/usr/bin/env python3
"""0DTE portfolio: keep the daily base and BOOST on strong days (user 2026-11-19, after portfolio_filter.py showed every
score bucket is profitable, so skipping days loses money). Same trades / features / expanding score as portfolio_filter.py;
FIXED base counts as in portfolio_test.py (50% of capital / 3 per root / the MEDIAN margin of that root and time);
variants: base every day; x2 when score >= .8; x2 when score >= .5; x3 when score >= .9 (x2 when .8-.9).
    cd research && python3 scripts/options/portfolio_boost.py > logs/options_portfolio_boost.log
"""
import numpy as np, pandas as pd
src = open('scripts/options/portfolio_filter.py').read()
exec(src.split("def tier(p):")[0].split('"""', 2)[2])       # builds D (trades, margins, expanding score) as there
V = {'base': lambda s: np.ones(len(s)), 'x2 if >=.8': lambda s: np.where(s >= .8, 2.0, 1.0),
     'x2 if >=.5': lambda s: np.where(s >= .5, 2.0, 1.0), 'x3 >=.9, x2 >=.8': lambda s: np.select([s >= .9, s >= .8], [3.0, 2.0], 1.0)}
for regime, cap in [('regt', 100_000), ('pm', 150_000)]:
    for t, m in TIMES.items():
        x = D[D.m == m]
        days = pd.bdate_range(x.date.min(), x.date.max())
        base = {r: max(1, int(np.floor(cap * 0.5 / 3 / g[regime].median()))) for r, g in x.groupby('root')}
        for vl, vf in V.items():
            usd = pd.Series(0.0, index=days); peak = pd.Series(0.0, index=days)
            for r, g in x.groupby('root'):
                n = base[r] * vf(g.score.values)
                usd = usd.add(pd.Series(n * g.pl.values, index=g.date.values).groupby(level=0).sum(), fill_value=0)
                peak = peak.add(pd.Series(n * g[regime].values, index=g.date.values).groupby(level=0).sum(), fill_value=0)
            eq = cap + usd.cumsum(); yrs = (days.max() - days.min()).days / 365.25
            dd = float((eq - eq.cummax()).min()); mon = usd.groupby(usd.index.to_period('M')).sum(); yr = usd.groupby(usd.index.year).sum()
            sh = usd.mean() / usd.std() * np.sqrt(252)
            print(f'{regime.upper():4s} ${cap / 1e3:.0f}k {t} base {base} {vl:16s}: {100 * usd.sum() / yrs / cap:+5.1f}%/yr  maxDD {100 * dd / cap:6.1f}%  '
                  f'worst day {100 * usd.min() / cap:+5.1f}%  Sharpe {sh:4.2f}  months+ {100 * (mon > 0).mean():3.0f}%  max margin {100 * peak.max() / cap:4.0f}%  '
                  + ' '.join(f'{y}:{100 * v / cap:+.1f}%' for y, v in yr.items()))
        print()
