#!/usr/bin/env python3
"""EXTREME 0DTE SIGNALS, minute by minute (user 2026-11-20): are the tails of the features worth much more, and can a
signal-triggered entry (the first minute a feature crosses its threshold) beat the fixed 10:00 entry?
Trades: zdte_trades.py, 25d put / call at every minute 09:35-15:55, sold at the bid, minute-hedged ($0.006/share; SPXW $0.06),
SPXW held to its cash close, ETFs / stocks to 17:30 with the hedge carried. $ per contract (x100) and % of premium.
Features per minute: ivrv_open = iv_atm / rv_open, ivrv30 = iv_atm / rv30, fs = iv_atm / fwd - 1, skew = the leg's own
(P25: iv25p / iv_atm, C25: iv25c / iv_atm); strangle features = the minute's (skew: the mean of the two).
THRESHOLDS: per root and feature, the q-quantile of ALL minutes of all EARLIER days (>= 60 days of warm-up), q = .95 / .98 /
.99 / .995; a minute 'fires' at level q when its value >= that day's threshold.
Part 1: per leg, every firing minute and the FIRST firing minute of each day: n, days, mean $, mean % of premium, PF, t (day).
Part 2: accounts (Reg T $100k, PM $150k; margin as portfolio_test.py; 50% of capital / 3 roots SPY QQQ TSLA, fixed counts by
the median margin): base = a strangle at 10:00 every day; trig = a strangle at the first firing minute (ivrv30 / fs / ivrv_open),
none otherwise; base+trig = both.
    cd research && python3 scripts/options/zdte_extreme.py > logs/options_zdte_extreme.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_rows', 400)
ROOTS = {'SPY': (0.15, 0.08, 0.006), 'QQQ': (0.15, 0.08, 0.006), 'TSLA': (0.20, 0.15, 0.006), 'SPXW': (0.15, 0.08, 0.06)}
QS = [.95, .98, .99, .995]
legs, strs = [], []
for r, (rt, mv, cps) in ROOTS.items():
    T = pd.read_parquet(f'data/options/zdte/trades/{r}.parquet')
    T = T[T.inst.isin(['P25', 'C25'])].copy()
    T['mid'] = (T.bid + T.ask) / 2; T = T[T.mid >= .05]
    s_end = T.close if r == 'SPXW' else T.s1730
    settle = np.where(T.right == 'C', np.maximum(s_end - T.strike, 0), np.maximum(T.strike - s_end, 0))
    T['pl'] = 100 * (T.bid - settle + T.hd + T.dlast.fillna(0) * (s_end - T.close) - T.turn * cps)
    T['prem'] = 100 * T.mid
    T['ivrv_open'] = T.iv_atm / T.rv_open; T['ivrv30'] = T.iv_atm / T.rv30; T['fs'] = T.iv_atm / T.fwd - 1
    T['skew'] = np.where(T.inst == 'P25', T.skew_p, T.skew_c)
    T = T.replace([np.inf, -np.inf], np.nan)
    legs.append(T[['root', 'date', 'm', 'inst', 'pl', 'prem', 'ivrv_open', 'ivrv30', 'fs', 'skew']])
    P = T[T.inst == 'P25'].set_index(['date', 'm']); C = T[T.inst == 'C25'].set_index(['date', 'm'])
    j = P.index.intersection(C.index); P, C = P.loc[j], C.loc[j]
    S = P.S
    regt = np.maximum(100 * (P.bid + np.maximum(rt * S - np.maximum(S - P.strike, 0), 0.10 * P.strike)) + 100 * C.bid,
                      100 * (C.bid + np.maximum(rt * S - np.maximum(C.strike - S, 0), 0.10 * S)) + 100 * P.bid)
    hedge = -(P.dl + C.dl); worst = pd.Series(0.0, index=j)
    for m in np.arange(-mv, mv + 1e-9, 0.01):
        Sx = S * (1 + m)
        worst = np.maximum(worst, np.maximum(P.strike - Sx, 0) + np.maximum(Sx - C.strike, 0) - (P.bid + C.bid) - hedge * (Sx - S))
    strs.append(pd.DataFrame({'root': r, 'pl': P.pl + C.pl, 'prem': P.prem + C.prem, 'regt': regt, 'pm': 100 * worst,
                              'ivrv_open': P.ivrv_open, 'ivrv30': P.ivrv30, 'fs': P.fs, 'skew': (P['skew'] + C['skew']) / 2}).reset_index())
L = pd.concat(legs, ignore_index=True); X = pd.concat(strs, ignore_index=True)
for d in (L, X): d['date'] = pd.to_datetime(d.date)


def thresholds(df, feat, key):
    """per root (and leg): the day's q-quantiles of `feat` over all minutes of EARLIER days (>= 60 days) -> columns thr_q"""
    out = []
    for kk, g in df.groupby(key):
        days = np.sort(g.date.unique()); vals = {d: g.loc[g.date == d, feat].dropna().values for d in days}
        hist = np.array([]); rows = []
        for i, d in enumerate(days):
            if i >= 60: rows.append((d, *np.quantile(hist, QS)))
            hist = np.concatenate([hist, vals[d]])
        t = pd.DataFrame(rows, columns=['date'] + [f'thr{q}' for q in QS])
        for c, v in zip(key if isinstance(key, list) else [key], kk if isinstance(kk, tuple) else [kk]): t[c] = v
        out.append(t)
    return pd.concat(out, ignore_index=True)


def st(x):
    v = x.pl; dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    if len(v) < 20: return f'n {len(v):6d}  -'
    t = dm.mean() / dm.std() * np.sqrt(len(dm)) if len(dm) > 2 and dm.std() > 0 else np.nan
    return f'n {len(v):7,d} days {len(dm):4d} mean ${v.mean():7.1f} {100 * v.sum() / x.prem.sum():+6.1f}% prem PF {w / l if l > 0 else np.inf:5.2f} t {t:5.1f}'


print('== PART 1: per leg; ALL firing minutes | the FIRST firing minute of each day   (base = every minute of the same days span)')
FE = ['ivrv_open', 'ivrv30', 'fs', 'skew']
for feat in FE:
    th = thresholds(L, feat, ['root', 'inst'])
    Z = L.merge(th, on=['root', 'inst', 'date'])
    print(f'\n-- {feat}')
    for (r, i), z in Z.groupby(['root', 'inst']):
        print(f'   {r:4s} {i}  all minutes: {st(z)}')
        for q in QS:
            f = z[z[feat] >= z[f'thr{q}']]
            first = f.sort_values('m').drop_duplicates('date')
            print(f'      top {100 * (1 - q):4.1f}%  all: {st(f)} | first/day: {st(first)}  (median entry {int(first.m.median()) // 60 if len(first) else 0}:{int(first.m.median()) % 60 if len(first) else 0:02d})')

print('\n== PART 2: accounts (strangles; SPY QQQ TSLA)')
Y = X[X.root.isin(['SPY', 'QQQ', 'TSLA'])]
for regime, cap in [('regt', 100_000), ('pm', 150_000)]:
    base = Y[Y.m == 600]
    n = {r: max(1, int(np.floor(cap * 0.5 / 3 / g[regime].median()))) for r, g in base.groupby('root')}
    for feat in ['ivrv30', 'ivrv_open', 'fs']:
        th = thresholds(Y, feat, 'root'); Z = Y.merge(th, on=['root', 'date'])
        bdays = base[base.date.isin(Z.date.unique())]
        for q in QS:
            f = Z[Z[feat] >= Z[f'thr{q}']].sort_values('m').drop_duplicates(['root', 'date'])
            for lab, parts in [('base 10:00', [bdays]), (f'trig {feat} top {100 * (1 - q):.1f}%', [f]), ('base+trig', [bdays, f])]:
                if lab == 'base 10:00' and (q != QS[0] or feat != 'ivrv30'): continue
                if lab == 'base+trig' and q not in (.98, .99): continue
                P_ = pd.concat(parts); usd = P_.assign(u=P_.pl * P_.root.map(n)).groupby('date').u.sum()
                days = pd.bdate_range(Z.date.min(), Z.date.max()); usd = usd.reindex(days, fill_value=0.0)
                eq = cap + usd.cumsum(); yrs = (days.max() - days.min()).days / 365.25
                dd = float((eq - eq.cummax()).min()); mon = usd.groupby(usd.index.to_period('M')).sum()
                sh = usd.mean() / usd.std() * np.sqrt(252) if usd.std() > 0 else np.nan
                print(f'{regime.upper():4s} ${cap / 1e3:.0f}k {n} {lab:28s}: {100 * usd.sum() / yrs / cap:+5.1f}%/yr  maxDD {100 * dd / cap:6.1f}%  '
                      f'worst day {100 * usd.min() / cap:+5.1f}%  Sharpe {sh:4.2f}  months+ {100 * (mon > 0).mean():3.0f}%  entries {len(P_):4d}  '
                      + ' '.join(f'{y}:{100 * v / cap:+.1f}%' for y, v in usd.groupby(usd.index.year).sum().items()))
    print()
