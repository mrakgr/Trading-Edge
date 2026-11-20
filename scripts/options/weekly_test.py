#!/usr/bin/env python3
"""WEEKLIES vs 0DTE (user 2026-11-20): is selling 25-delta weeklies a better use of margin? short_dated.py trades (entry at the
close, expiry 1-10 trading days out, sold at the BID, held to expiry, DAILY delta hedge - hedge_daily.py) for SPY, QQQ, TSLA;
hedge cost $0.006 per share traded (as the 0DTE tests). P&L $ per contract (x100).
Margin per contract at entry (estimates): Reg T premium + max(r x S - OTM, 10%), r = 15% SPY / QQQ, 20% TSLA; PM = the worst
loss of the option + its entry delta hedge at intrinsic over -8..+8% (SPY, QQQ) / -15..+15% (TSLA) (conservative: no time value).
Return on margin annualised by the margin-days the trade holds (tdte trading days).
Per leg and per strangle (P25 + C25 of the same root / day / expiry); 'signal' = the leg's skew (IV25/IV_ATM) >= the prior
years' 90th pct (SH-28/34; a strangle needs both legs' signal off -> reported per leg only).
LADDER: every day sell the strangle expiring ~5 trading days out (tdte 4-6, the nearest to 5); budget 50% of capital / 3 roots
spread over the ~5 open ladders -> contracts per daily entry = floor(budget / 5 / median margin).
    cd research && python3 scripts/options/weekly_test.py > logs/options_weekly_test.log
"""
import os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sd_common import drop_bad
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30)
R = {'SPY': (0.15, 0.08), 'QQQ': (0.15, 0.08), 'TSLA': (0.20, 0.15)}
T = pd.read_parquet('data/options/short_dated/trades.parquet')
T = T[T.root.isin(R) & T.inst.isin(['P25', 'C25'])]
T = drop_bad(T, quiet=True)
H = pd.read_parquet('data/options/short_dated/hedge.parquet', columns=['root', 'date', 'exp', 'K', 'r', 'inst', 'hd', 'turn'])
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
D['mid'] = (D.bid + D.ask) / 2
D = D[(D.mid >= .05) & ~D.earn].copy()
D['year'] = D.date.dt.year
D['pl'] = 100 * (D.bid - D.settle + D.hd - D.turn * 0.006)
rt = D.root.map(lambda r: R[r][0]); mv = D.root.map(lambda r: R[r][1])
otm = np.where(D.r == 'C', np.maximum(D.K - D.S, 0), np.maximum(D.S - D.K, 0))
D['regt'] = 100 * (D.bid + np.maximum(rt * D.S - otm, 0.10 * np.where(D.r == 'C', D.S, D.K)))
worst = np.zeros(len(D))
for m in np.arange(-0.15, 0.15 + 1e-9, 0.01):
    ok = np.abs(m) <= mv.values + 1e-9
    Sx = D.S.values * (1 + m)
    intr = np.where(D.r.values == 'C', np.maximum(Sx - D.K.values, 0), np.maximum(D.K.values - Sx, 0))
    loss = intr - D.bid.values - D.dl.values * (Sx - D.S.values)
    worst = np.where(ok, np.maximum(worst, loss), worst)
D['pm'] = 100 * worst
D['skw'] = np.where(D.inst == 'P25', D.iv25p, D.iv25c) / D.iv_atm
D['sig'] = False
for (r, i), g in D.groupby(['root', 'inst']):
    for y in sorted(g.year.unique()):
        p = g.loc[g.year < y, 'skw'].dropna()
        if len(p) < 500: continue
        D.loc[g.index[(g.year == y) & (g.skw >= p.quantile(.9))], 'sig'] = True
D['td'] = pd.cut(D.tdte, [0, 1, 3, 5, 10], labels=['1', '2-3', '4-5', '6-10'])


def stat(x):
    v = x.pl; w, l = v[v > 0].sum(), -v[v < 0].sum()
    md = (x.tdte).mean()
    return pd.Series({'n': len(v), 'premium $': 100 * x.bid.mean(), 'mean $': v.mean(), 'median $': v.median(), 'PF': w / l,
                      'win %': 100 * (v > 0).mean(), '$ per day held': v.mean() / md, 'RegT margin': x.regt.median(),
                      'PM margin': x.pm.median(), 'RoM RegT %/yr': 100 * v.mean() / (x.regt.median() * md / 252),
                      'RoM PM %/yr': 100 * v.mean() / (x.pm.median() * md / 252)})


X = D[D.year >= 2016]
print('== per contract, 2016-2026, unfiltered (sold at the bid, daily hedge $0.006/share, held to expiry)')
print(X.groupby(['root', 'inst', 'td'], observed=True)[X.columns.tolist()].apply(stat).round(1).to_string())
print('\n== per contract, the skew SIGNAL (top 10% vs prior years), 2016-2026')
print(X[X.sig].groupby(['root', 'inst', 'td'], observed=True)[X.columns.tolist()].apply(stat).round(1).to_string())

# ---- the ladder: one ~5-day strangle per day per root
L = X[X.tdte.between(4, 6)].copy()
L['d5'] = (L.tdte - 5).abs()
k = ['root', 'date', 'exp']
P = L[L.inst == 'P25'].set_index(k); C = L[L.inst == 'C25'].set_index(k)
j = P.index.intersection(C.index)
st = pd.DataFrame({'pl': P.loc[j, 'pl'] + C.loc[j, 'pl'], 'regt': np.maximum(P.loc[j, 'regt'] + 100 * C.loc[j, 'bid'], C.loc[j, 'regt'] + 100 * P.loc[j, 'bid']),
                   'pm': P.loc[j, 'pm'] + C.loc[j, 'pm'], 'd5': P.loc[j, 'd5'], 'tdte': P.loc[j, 'tdte'], 'xd': P.loc[j, 'xd']}).reset_index()
st = st.sort_values('d5').drop_duplicates(['root', 'date']).sort_values(['root', 'date'])
print('\n== the ~5-day strangle per contract (2016-2026): ' + '; '.join(
    f"{r}: {len(g)} entries, mean ${g.pl.mean():.0f}, median ${g.pl.median():.0f}, win {100 * (g.pl > 0).mean():.0f}%, "
    f"PF {g.pl[g.pl > 0].sum() / -g.pl[g.pl < 0].sum():.2f}, RegT ${g.regt.median():,.0f}, PM ${g.pm.median():,.0f}" for r, g in st.groupby('root')))
print('\n== LADDER accounts (P&L booked at expiry; fixed contract counts)')
for regime, cap in [('regt', 100_000), ('pm', 150_000)]:
    n = {r: int(np.floor(cap * 0.5 / 3 / 5 / g[regime].median())) for r, g in st.groupby('root')}
    usd = st.assign(u=st.pl * st.root.map(n)).groupby('xd').u.sum()
    usd.index = pd.to_datetime(usd.index)
    days = pd.bdate_range(usd.index.min(), usd.index.max()); usd = usd.reindex(days, fill_value=0.0)
    eq = cap + usd.cumsum(); yrs = (days.max() - days.min()).days / 365.25
    dd = float((eq - eq.cummax()).min()); mon = usd.groupby(usd.index.to_period('M')).sum(); yr = usd.groupby(usd.index.year).sum()
    print(f'{regime.upper():4s} ${cap / 1e3:.0f}k contracts per daily entry {n}: {100 * usd.sum() / yrs / cap:+5.1f}%/yr  maxDD {100 * dd / cap:6.1f}%  '
          f'worst day {100 * usd.min() / cap:+5.1f}%  Sharpe {usd.mean() / usd.std() * np.sqrt(252):4.2f}  months+ {100 * (mon > 0).mean():3.0f}%\n     '
          + ' '.join(f'{y}:{100 * v / cap:+.1f}%' for y, v in yr.items()))
