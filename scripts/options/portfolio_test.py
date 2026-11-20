#!/usr/bin/env python3
"""0DTE PORTFOLIO TEST (user 2026-11-19): what does selling 25-delta strangles return on an account, under Reg T and portfolio
margin, with a realistic number of contracts?

Per root (SPY, QQQ, TSLA, NVDA) and entry time (10:00 / 12:00 / 14:00 / 15:00, or 'avg' = every minute averaged), one
strangle (the 25d put + the 25d call of that minute) per 0DTE day: sold at the bid, minute delta hedge ($0.006/share), held
to 17:30 with the hedge carried (SH-41's conservative settlement). P&L in $ per contract (x100).
MARGIN per strangle at entry (estimates, the broker's own may be higher for 0DTE):
  Reg T: per short option premium + max(r x S - OTM, 10% x (S call / K put)), r = 15% broad-based (SPY, QQQ), 20% stocks;
         strangle = the larger side + the other side's premium.
  PM:    the worst loss of the strangle + its entry delta hedge at expiry over moves -8..+8% (SPY, QQQ) / -15..+15% (stocks),
         in 1% steps (a TIMS-like scan; the hedge is held).
ACCOUNTS: Reg T $100k and PM $150k; a margin budget of 50% of the STARTING capital split evenly over SPY, QQQ, TSLA
(contracts = floor(budget / the median margin of that root and time)); fixed contract counts (no compounding).
    cd research && python3 scripts/options/portfolio_test.py > logs/options_portfolio_test.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30)
ROOTS = {'SPY': (0.15, 0.08), 'QQQ': (0.15, 0.08), 'TSLA': (0.20, 0.15), 'NVDA': (0.20, 0.15)}
TIMES = {'10:00': 600, '12:00': 720, '14:00': 840, '15:00': 900, 'avg': None}
rows = []
for r, (rt, mv) in ROOTS.items():
    T = pd.read_parquet(f'data/options/zdte/trades/{r}.parquet')
    T = T[T.inst.isin(['P25', 'C25'])].copy()
    T['mid'] = (T.bid + T.ask) / 2
    settle = np.where(T.right == 'C', np.maximum(T.s1730 - T.strike, 0), np.maximum(T.strike - T.s1730, 0))
    T['pl'] = 100 * (T.bid - settle + T.hd + T.dlast.fillna(0) * (T.s1730 - T.close) - T.turn * 0.006)
    P = T[T.inst == 'P25'].set_index(['date', 'm']); C = T[T.inst == 'C25'].set_index(['date', 'm'])
    j = P.index.intersection(C.index)
    P, C = P.loc[j], C.loc[j]
    S = P.S
    otm_p = np.maximum(S - P.strike, 0); otm_c = np.maximum(C.strike - S, 0)
    req_p = 100 * (P.bid + np.maximum(rt * S - otm_p, 0.10 * P.strike))
    req_c = 100 * (C.bid + np.maximum(rt * S - otm_c, 0.10 * S))
    regt = np.maximum(req_p + 100 * C.bid, req_c + 100 * P.bid)
    hedge = -(P.dl + C.dl)                                            # shares per share of option held against the strangle
    worst = pd.Series(0.0, index=j)
    for m in np.arange(-mv, mv + 1e-9, 0.01):
        Sx = S * (1 + m)
        loss = (np.maximum(P.strike - Sx, 0) + np.maximum(Sx - C.strike, 0)) - (P.bid + C.bid) - hedge * (Sx - S)
        worst = np.maximum(worst, loss)
    pm = 100 * worst
    X = pd.DataFrame({'pl': P.pl + C.pl, 'regt': regt, 'pm': pm, 'prem': 100 * (P.bid + C.bid), 'S': S}).reset_index()
    for lab, mm in TIMES.items():
        x = X[X.m == mm] if mm else X.groupby('date')[['pl', 'regt', 'pm', 'prem', 'S']].mean().reset_index()
        x = x.assign(root=r, time=lab)
        rows.append(x[['root', 'time', 'date', 'pl', 'regt', 'pm', 'prem', 'S']])
D = pd.concat(rows, ignore_index=True); D['date'] = pd.to_datetime(D.date)


def mdd(eq):
    return float((eq - eq.cummax()).min())


print('== per contract: one strangle a 0DTE day (sold at the bid, minute-hedged, held to 17:30)')
tab = []
for (r, t), x in D.groupby(['root', 'time']):
    yrs = (x.date.max() - x.date.min()).days / 365.25
    eq = x.sort_values('date').pl.cumsum()
    tab.append({'root': r, 'time': t, 'days': len(x), 'premium $': x.prem.mean(), 'P&L $/yr': x.pl.sum() / yrs,
                'mean $/day': x.pl.mean(), 'median': x.pl.median(), 'days+%': 100 * (x.pl > 0).mean(), 'worst day': x.pl.min(),
                'maxDD $': mdd(eq), 'RegT margin': x.regt.median(), 'PM margin': x.pm.median(),
                'RoM RegT %/yr': 100 * x.pl.sum() / yrs / x.regt.median(), 'RoM PM %/yr': 100 * x.pl.sum() / yrs / x.pm.median()})
print(pd.DataFrame(tab).round(1).to_string(index=False))

print('\n== ACCOUNTS: margin budget 50% of starting capital split evenly over SPY, QQQ, TSLA; fixed contract counts')
for regime, cap in [('regt', 100_000), ('pm', 150_000)]:
    for t in TIMES:
        x = D[(D.time == t) & D.root.isin(['SPY', 'QQQ', 'TSLA'])]
        n = {r: int(np.floor(cap * 0.5 / 3 / x[x.root == r][regime].median())) for r in ['SPY', 'QQQ', 'TSLA']}
        day = x.assign(usd=x.pl * x.root.map(n)).groupby('date').usd.sum()
        day = day.reindex(pd.bdate_range(day.index.min(), day.index.max()), fill_value=0.0)
        eq = cap + day.cumsum()
        yrs = (day.index.max() - day.index.min()).days / 365.25
        mon = day.groupby(day.index.to_period('M')).sum(); yr = day.groupby(day.index.year).sum()
        sh = day.mean() / day.std() * np.sqrt(252) if day.std() > 0 else np.nan
        print(f'{regime.upper():4s} ${cap / 1e3:.0f}k {t:5s} contracts {n}: {100 * day.sum() / yrs / cap:+5.1f}%/yr  maxDD {100 * mdd(eq) / cap:6.1f}%  '
              f'worst day {100 * day.min() / cap:+5.1f}%  Sharpe {sh:4.2f}  months+ {100 * (mon > 0).mean():3.0f}%  '
              f'years: ' + ' '.join(f'{y}:{100 * v / cap:+.1f}%' for y, v in yr.items()))
