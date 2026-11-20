#!/usr/bin/env python3
"""0DTE PORTFOLIO with the FEATURES (user 2026-11-19): capital only on the best days, sized by signal strength.
Same strangle and P&L as portfolio_test.py (25d put + call at a fixed entry time, sold at the bid, minute-hedged, held to
17:30 with the hedge carried; Reg T / PM margin estimates). Features at the entry minute (the 0DTE chain):
  ivrv  = iv_atm / rv_open (realized since 09:30)      fs = iv_atm / fwd - 1 (0DTE rich vs the forward to the next expiry)
  score = the mean of their two EXPANDING percentiles: each day ranked against all EARLIER days of that root and entry time
          (>= 120 prior days; earlier days are warm-up and excluded from every variant).
Sizing per root: a margin budget B (50% of starting capital / 3); contracts that day = floor(B x w / that day's margin):
  base  w = 1 every day          filter  w = 1 if score >= .7 else 0
  tier  w = 0 (< .5), 1/3 (.5-.8), 2/3 (.8-.9), 1 (>= .9)       (+ the same with ivrv alone / fs alone)
Fractional contracts are not allowed: a day whose budget buys < 1 contract is skipped.
    cd research && python3 scripts/options/portfolio_filter.py > logs/options_portfolio_filter.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30)
ROOTS = {'SPY': (0.15, 0.08), 'QQQ': (0.15, 0.08), 'TSLA': (0.20, 0.15)}
TIMES = {'10:00': 600, '12:00': 720, '14:00': 840}
rows = []
for r, (rt, mv) in ROOTS.items():
    T = pd.read_parquet(f'data/options/zdte/trades/{r}.parquet')
    T = T[T.inst.isin(['P25', 'C25']) & T.m.isin(TIMES.values())].copy()
    settle = np.where(T.right == 'C', np.maximum(T.s1730 - T.strike, 0), np.maximum(T.strike - T.s1730, 0))
    T['pl'] = 100 * (T.bid - settle + T.hd + T.dlast.fillna(0) * (T.s1730 - T.close) - T.turn * 0.006)
    P = T[T.inst == 'P25'].set_index(['date', 'm']); C = T[T.inst == 'C25'].set_index(['date', 'm'])
    j = P.index.intersection(C.index); P, C = P.loc[j], C.loc[j]
    S = P.S
    otm_p = np.maximum(S - P.strike, 0); otm_c = np.maximum(C.strike - S, 0)
    regt = np.maximum(100 * (P.bid + np.maximum(rt * S - otm_p, 0.10 * P.strike)) + 100 * C.bid,
                      100 * (C.bid + np.maximum(rt * S - otm_c, 0.10 * S)) + 100 * P.bid)
    hedge = -(P.dl + C.dl); worst = pd.Series(0.0, index=j)
    for m in np.arange(-mv, mv + 1e-9, 0.01):
        Sx = S * (1 + m)
        worst = np.maximum(worst, (np.maximum(P.strike - Sx, 0) + np.maximum(Sx - C.strike, 0)) - (P.bid + C.bid) - hedge * (Sx - S))
    X = pd.DataFrame({'pl': P.pl + C.pl, 'regt': regt, 'pm': 100 * worst, 'prem': 100 * (P.bid + C.bid),
                      'ivrv': P.iv_atm / P.rv_open, 'fs': P.iv_atm / P.fwd - 1}).reset_index()
    X['root'] = r; rows.append(X)
D = pd.concat(rows, ignore_index=True); D['date'] = pd.to_datetime(D.date)
D = D.replace([np.inf, -np.inf], np.nan)


def expanding_pct(s, warm=120):
    """rank of each value among the strictly earlier values (NaN until `warm` earlier values exist)"""
    v = s.values; out = np.full(len(v), np.nan); hist = []
    for i, x in enumerate(v):
        if len(hist) >= warm and np.isfinite(x): out[i] = np.searchsorted(np.sort(hist), x, 'right') / len(hist)
        if np.isfinite(x): hist.append(x)
    return out


parts = []
for (r, m), g in D.groupby(['root', 'm']):
    g = g.sort_values('date').copy()
    g['p_ivrv'] = expanding_pct(g.ivrv); g['p_fs'] = expanding_pct(g.fs)
    g['score'] = g[['p_ivrv', 'p_fs']].mean(axis=1, skipna=False)
    parts.append(g)
D = pd.concat(parts, ignore_index=True)
D = D[D.score.notna()]


def tier(p):
    return np.select([p >= .9, p >= .8, p >= .5], [1.0, 2 / 3, 1 / 3], 0.0)


print('== per contract by score bucket (expanding percentiles, after the warm-up): mean $ / PF / days+')
for (r, m), g in D.groupby(['root', 'm']):
    b = pd.cut(g.score, [0, .5, .8, .9, 1.01], right=False)
    s = g.groupby(b, observed=True).pl.agg(['size', 'mean', 'median', lambda v: v[v > 0].sum() / -v[v < 0].sum(), lambda v: 100 * (v > 0).mean()])
    s.columns = ['n', 'mean$', 'median$', 'PF', 'days+%']
    print(f'-- {r} {m // 60}:{m % 60:02d}\n' + s.round(2).to_string())

W = {'base': lambda g: np.ones(len(g)), 'filter .7': lambda g: (g.score >= .7).astype(float).values,
     'tier (score)': lambda g: tier(g.score.values), 'tier (ivrv)': lambda g: tier(g.p_ivrv.values),
     'tier (fs)': lambda g: tier(g.p_fs.values)}
print('\n== ACCOUNTS (margin budget 50% of capital / 3 per root; fixed budget, no compounding)')
for regime, cap in [('regt', 100_000), ('pm', 150_000)]:
    for t, m in TIMES.items():
        x = D[D.m == m]
        days = pd.bdate_range(x.date.min(), x.date.max())
        for wl, wf in W.items():
            usd = pd.Series(0.0, index=days); used = []; ntr = 0
            for r, g in x.groupby('root'):
                n = np.floor(cap * 0.5 / 3 * wf(g) / g[regime].values)
                usd = usd.add(pd.Series(n * g.pl.values, index=g.date.values).groupby(level=0).sum(), fill_value=0)
                ntr += int((n > 0).sum()); used.append((n * g[regime].values).sum())
            eq = cap + usd.cumsum(); yrs = (days.max() - days.min()).days / 365.25
            dd = float((eq - eq.cummax()).min()); mon = usd.groupby(usd.index.to_period('M')).sum()
            yr = usd.groupby(usd.index.year).sum()
            sh = usd.mean() / usd.std() * np.sqrt(252)
            print(f'{regime.upper():4s} ${cap / 1e3:.0f}k {t} {wl:13s}: {100 * usd.sum() / yrs / cap:+5.1f}%/yr  maxDD {100 * dd / cap:6.1f}%  '
                  f'worst day {100 * usd.min() / cap:+5.1f}%  Sharpe {sh:4.2f}  months+ {100 * (mon > 0).mean():3.0f}%  root-days traded {ntr:4d}  '
                  + ' '.join(f'{y}:{100 * v / cap:+.1f}%' for y, v in yr.items()))
        print()
