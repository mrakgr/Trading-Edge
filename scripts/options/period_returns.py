#!/usr/bin/env python3
"""How consistent are the option-selling systems (user 2026-11-19)? Day / week / month / year results: share of profitable
periods, mean, median, worst, best.

0DTE (zdte_trades.py): 25-delta put (P25), call (C25), both (STRANGLE = P25 + C25 at the same minute); sold at the bid,
minute delta hedge at $0.006/share ($0.06 per SPX unit); SPXW settled on its 16:00 cash close, ETFs / stocks held to 17:30
with the 15:59 hedge carried (the conservative case, SH-41). A DAY = the mean of all its minute entries ('one contract spread
over the day'); entries 09:35-15:55 ('all') or from 13:00 ('pm'). Units: $ per contract (x100 shares), % of the premium
(sum P&L / sum premium), bp of notional (per-share P&L / S).
Short-dated (short_dated.py + hedge_daily.py, SH-29/34): the 25-delta short with skew in the top 10% (prior years, per kind),
hs <= 5%, no earnings, sold at the bid, daily hedge - 2bp; grouped by ENTRY day; unit = % of premium, a period's result =
the mean over its trades (one premium unit per trade).
    cd research && python3 scripts/options/period_returns.py > logs/options_period_returns.log
"""
import os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
pd.set_option('display.width', 250); pd.set_option('display.max_rows', 300)
ROOTS = ['SPXW', 'SPY', 'QQQ', 'TSLA', 'IWM', 'NVDA']


def periods(daily, col, fmt):
    """daily: DataFrame indexed by date with column col (additive) -> one row per period kind"""
    out = []
    s = daily[col]
    for lab, key in [('day', s.index), ('week', s.index.to_period('W')), ('month', s.index.to_period('M')), ('year', s.index.year)]:
        p = s.groupby(key).sum() if lab != 'day' else s
        out.append({'period': lab, 'n': len(p), '% positive': 100 * (p > 0).mean(), 'mean': p.mean(), 'median': p.median(),
                    'worst': p.min(), 'best': p.max(), 'sd': p.std()})
    t = pd.DataFrame(out).set_index('period')
    return t


# ---------------- 0DTE
rows = []
for r in ROOTS:
    T = pd.read_parquet(f'data/options/zdte/trades/{r}.parquet')
    T = T[T.inst.isin(['P25', 'C25'])].copy()
    T['mid'] = (T.bid + T.ask) / 2
    T = T[T.mid >= .05]
    cps = 0.06 if r == 'SPXW' else 0.006
    s17 = T.close if r == 'SPXW' else T.s1730
    settle = np.where(T.right == 'C', np.maximum(s17 - T.strike, 0), np.maximum(T.strike - s17, 0))
    T['pl'] = T.bid - settle + T.hd + T.dlast.fillna(0) * (s17 - T.close) - T.turn * cps          # $ per share
    T['bp'] = 1e4 * T.pl / T.S
    for win, mlo in [('all', 0), ('pm', 780)]:
        x = T[T.m >= mlo]
        legs = {i: x[x.inst == i] for i in ['P25', 'C25']}
        st = x.groupby(['date', 'm'])[['pl', 'mid', 'bp']].sum().reset_index()                       # strangle per minute
        st = st[st.groupby(['date', 'm']).pl.transform('size') > 0]
        for lab, y in [('P25', legs['P25']), ('C25', legs['C25']), ('STRANGLE', st)]:
            d = y.groupby('date').agg(pl=('pl', 'mean'), prem=('mid', 'mean'), bp=('bp', 'mean'))
            d['usd'] = 100 * d.pl; d.index = pd.to_datetime(d.index)
            rows.append((r, win, lab, d))

print('=' * 120)
print('0DTE: one contract spread over the day; $ per contract (x100); a week / month / year = the sum of its days')
for r, win, lab, d in rows:
    if lab != 'STRANGLE' and win != 'all': continue
    tot_pct = 100 * d.pl.sum() / d.prem.sum()
    print(f'\n-- {r} {lab} ({win} minutes): {len(d)} days, {d.index.min():%Y-%m}..{d.index.max():%Y-%m}; '
          f'P&L = {tot_pct:+.1f}% of premium; mean premium ${100 * d.prem.mean():.0f}/contract; total ${d.usd.sum():,.0f} per contract-a-day')
    t = periods(d, 'usd', '$')
    t2 = periods(d, 'bp', 'bp')
    print(pd.concat({'$/contract': t[['n', '% positive', 'mean', 'median', 'worst', 'best']],
                     'bp of notional': t2[['mean', 'median', 'worst']]}, axis=1).round(1).to_string())
    yr = d.groupby(d.index.year).agg(days=('usd', 'size'), usd=('usd', 'sum'), pos=('usd', lambda v: 100 * (v > 0).mean()),
                                     pct=('pl', 'sum'), prem=('prem', 'sum'))
    yr['% of prem'] = 100 * yr.pct / yr.prem
    print('   by year: ' + '  '.join(f'{y}: ${v.usd:,.0f} ({v["% of prem"]:+.1f}% prem, {v.pos:.0f}% days+, {int(v.days)}d)' for y, v in yr.iterrows()))

# ---------------- short-dated skew system
from sd_common import drop_bad
T = pd.read_parquet('data/options/short_dated/trades.parquet')
T = drop_bad(T, quiet=True)
T = T[T.inst.isin(['P25', 'C25']) & ~T.earn]
H = pd.read_parquet('data/options/short_dated/hedge.parquet', columns=['root', 'date', 'exp', 'K', 'r', 'inst', 'hd', 'turn'])
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['pl'] = (D.mid * (1 - D.hs) - D.settle + D.hd) / D.mid - D.turn * D.S * 2e-4 / D.mid
D['skw'] = np.where(D.inst == 'P25', D.iv25p, D.iv25c) / D.iv_atm
D['sig'] = False
for (k, i), g in D.groupby(['kind', 'inst']):
    for y in sorted(g.year.unique()):
        p = g.loc[g.year < y, 'skw'].dropna()
        if len(p) < 2000: continue
        D.loc[g.index[(g.year == y) & (g.skw >= p.quantile(.9))], 'sig'] = True
X = D[D.sig & (D.hs <= .05) & (D.year > 2012)]
print('\n' + '=' * 120)
print('SHORT-DATED skew system (1-10 trading days to expiry): a period = the MEAN % of premium of the trades ENTERED in it')
for (k, i), x in X.groupby(['kind', 'inst']):
    print(f'\n-- {k} {i}: {len(x):,} trades on {x.date.nunique():,} entry days, {x.date.min():%Y}..{x.date.max():%Y}')
    out = []
    for lab, key in [('day', x.date), ('week', x.date.dt.to_period('W')), ('month', x.date.dt.to_period('M')), ('year', x.date.dt.year)]:
        p = x.groupby(key).pl.mean() * 100
        out.append({'period': lab, 'n': len(p), '% positive': 100 * (p > 0).mean(), 'mean': p.mean(), 'median': p.median(),
                    'worst': p.min(), 'best': p.max()})
    print(pd.DataFrame(out).set_index('period').round(1).to_string())
