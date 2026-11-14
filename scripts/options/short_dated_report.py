#!/usr/bin/env python3
"""Report for short_dated.py (data/options/short_dated/trades.parquet). P&L of the SHORT in % of the option's own mid
premium (user 2026-11-13: PF on the premium, not notional): (mid - f x half-spread - settle) / mid, f on the opening
trade only (held to expiry = intrinsic). 'hedged' adds a static delta hedge held to expiry: + dl x (Sx - S) / mid.
t = date-clustered (the mean per entry day, then t over days). PF = sum of wins / sum of losses. Earnings-in-hold trades
are reported apart. Deciles are IN-SAMPLE (the whole period) - exploration, not a gate yet.
    cd research && python3 scripts/options/short_dated_report.py > logs/options_short_dated_report.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30); pd.set_option('display.max_rows', 300)
D = pd.read_parquet('data/options/short_dated/trades.parquet')
from sd_common import drop_bad; D = drop_bad(D)   # SH-36: vanished contracts (corporate actions)
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= 0.05]
D['hs'] = (D.ask - D.bid) / 2 / D.mid
for f in (0, .25, .5):
    D[f'n{int(f * 100)}'] = (D.mid * (1 - f * D.hs) - D.settle) / D.mid
D['h25'] = D.n25 + D.dl * (D.Sx - D.S) / D.mid
D['h0'] = D.n0 + D.dl * (D.Sx - D.S) / D.mid
D['year'] = D.date.dt.year
D['td_b'] = pd.cut(D.tdte, [0, 1, 3, 5, 10], labels=['1', '2-3', '4-5', '6-10'])
D['ivrv'] = D.iv_atm / D.rv20
D['slope'] = D.iv_atm / D.iv30
D['skewp'] = D.iv25p / D.iv_atm
D['skewc'] = D.iv25c / D.iv_atm
D['abn'] = D.vol / D.vol20
D['abn5'] = D.vol5 / D.vol20
D['liq'] = D.groupby('date').vol20.rank(pct=True)          # cross-sectional liquidity rank that day


def stats(x, col):
    v = x[col]
    if len(v) < 30: return None
    dm = x.groupby('date')[col].mean()
    t = dm.mean() / dm.std() * np.sqrt(len(dm)) if len(dm) > 2 and dm.std() > 0 else np.nan
    w, l = v[v > 0].sum(), -v[v < 0].sum()
    return dict(n=len(v), days=len(dm), mean=100 * v.mean(), med=100 * v.median(), win=100 * (v > 0).mean(),
                PF=w / l if l > 0 else np.nan, t=t)


def table(d, by, cols=('n0', 'n25', 'h25')):
    rows = []
    for k, g in d.groupby(by, observed=True):
        r = {'key': k}
        for c in cols:
            s = stats(g, c)
            if s is None: break
            if c == cols[0]: r.update(n=s['n'], days=s['days'])
            r.update({f'{c}': s['mean'], f'{c}_PF': s['PF'], f'{c}_t': s['t']})
            if c == 'h25': r.update(h25_med=s['med'], h25_win=s['win'])
        else:
            rows.append(r)
    return pd.DataFrame(rows).set_index('key').round(2) if rows else None


X = D[~D.earn]
print('== baseline (no earnings in hold), mean % of premium; n0 = naked at mid, n25 = naked f=.25, h25 = delta-hedged f=.25')
print(table(X, ['kind', 'inst']))
print('\n== earnings IN the hold (stocks)')
print(table(D[D.earn], ['inst']))
print('\n== by trading days to expiry')
print(table(X, ['kind', 'inst', 'td_b']))
print('\n== by half-spread at entry (% of mid)')
X = X.assign(hs_b=pd.cut(X.hs, [0, .02, .05, .10, .20, 10]))
print(table(X, ['inst', 'hs_b']))
for feat in ['ivrv', 'slope', 'skewp', 'skewc', 'abn', 'abn5', 'liq']:
    Y = X.dropna(subset=[feat]).copy()
    Y['dec'] = Y.groupby(['kind', 'inst'])[feat].transform(lambda s: pd.qcut(s.rank(method='first'), 10, labels=False) + 1)
    print(f'\n== deciles of {feat} (in-sample, per kind x inst), ranges for STR stock:')
    rng = Y[(Y.inst == 'STR') & (Y.kind == 'stock')].groupby('dec')[feat].agg(['min', 'max']).round(3).T
    print(rng.to_string())
    for inst in ['STR', 'P25', 'C25']:
        for kind in ['stock', 'etf']:
            t = table(Y[(Y.inst == inst) & (Y.kind == kind)], ['dec'], cols=('n25', 'h25'))
            if t is not None:
                print(f'  -- {kind} {inst}'); print(t[['n', 'n25', 'n25_PF', 'h25', 'h25_PF', 'h25_t', 'h25_win']].T.to_string())
# by year: short_dated_check.py (expanding-threshold signals)
