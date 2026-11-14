#!/usr/bin/env python3
"""ROBUST IV/RV VARIANTS (user 2026-11-14), on ivrv.py's cache (data/options/short_dated/{ivrv_trades,rv_daily}.parquet).
  medRV_W  = the rolling MEDIAN of the daily yz20x over the last W trading days (W = 60 / 120 / 252)  -> iv_atm / medRV_W
  ivh_W    = iv30 (ATM IV of the ~30-day expiry, one consistent tenor) / its rolling median over the PREVIOUS W rows
  vrph_W   = the name's own variance-premium history: median over W rows of log(iv30_d / rv21_d), rv21_d = the
             close-to-close RV of the 21 trading days AFTER d - each term known only at d+21, so the window is lagged 21
All windows end at t (no lookahead). Tested: (a) inside the skew top 10% (prior years), IV-feature terciles by prior
years' quantiles: spread = (PF_t3 - 1) / (PF_t1 - 1); (b) standalone deciles on the STR (the pure VRP trade).
Trades: sold at the bid, daily hedge - 2bp, hs <= 5%, no earnings in hold, 2013+ (as ivrv.py).
    cd research && python3 scripts/options/ivrv2.py > logs/options_ivrv2.log 2>&1
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250)
D = pd.read_parquet('data/options/short_dated/ivrv_trades.parquet')
R = pd.read_parquet('data/options/short_dated/rv_daily.parquet').sort_values(['ticker', 'date'])
W = (60, 120, 252)
g = R.groupby('ticker')
for w in W:
    R[f'medRV{w}'] = g.yz20x.transform(lambda x: x.rolling(w, min_periods=int(w * .6)).median())
# forward 21-day close-to-close RV (known at d+21)
R['c2'] = R.r_cc.fillna(0) ** 2
R['f21'] = g.c2.transform(lambda x: x[::-1].rolling(21, min_periods=21).sum()[::-1].shift(-1))
R['rv21f'] = np.sqrt(R.f21 * 252 / 21)
# root-day iv30 series (all trades, any spread)
T = pd.read_parquet('data/options/short_dated/trades.parquet', columns=['root', 'date', 'iv30'])
I = T.dropna(subset=['iv30']).groupby(['root', 'date']).iv30.first().reset_index().sort_values(['root', 'date'])
del T
u = D.groupby('root').und.first()
I['und'] = I.root.map(u)
I = I.dropna(subset=['und']).merge(R[['ticker', 'date', 'rv21f']].rename(columns={'ticker': 'und'}), on=['und', 'date'], how='left')
gi = I.groupby('root')
for w in W:
    I[f'medIV{w}'] = gi.iv30.transform(lambda x: x.shift(1).rolling(w, min_periods=int(w * .6)).median())
I['vrp'] = np.log(I.iv30 / I.rv21f)
for w in (120, 252):
    I[f'vrph{w}'] = gi.vrp.transform(lambda x: x.shift(21).rolling(w, min_periods=int(w * .6)).median())
D = D.merge(R[['ticker', 'date'] + [f'medRV{w}' for w in W]].rename(columns={'ticker': 'und'}), on=['und', 'date'], how='left')
D = D.merge(I[['root', 'date'] + [f'medIV{w}' for w in W] + ['vrph120', 'vrph252']], on=['root', 'date'], how='left')
F = {}
for w in W:
    F[f'IV/medRV{w}'] = D.iv_atm / D[f'medRV{w}']
    F[f'iv30/medIV{w}'] = D.iv30 / D[f'medIV{w}']
F['vrph120'] = D.vrph120; F['vrph252'] = D.vrph252
F['IV/yz20x (ref)'] = D.iv_atm / D.yz20x
for k, v in F.items(): D[k] = v
D['skw'] = np.where(D.inst == 'P25', D.iv25p, np.where(D.inst == 'C25', D.iv25c, np.nan)) / D.iv_atm


def prior_pct(col):
    out = pd.Series(np.nan, index=D.index)
    for (k_, i), gg in D.groupby(['kind', 'inst']):
        for y in sorted(gg.year.unique()):
            p = np.sort(gg.loc[gg.year < y, col].dropna().values)
            if len(p) < 2000: continue
            m = gg.index[(gg.year == y) & gg[col].notna()]
            out[m] = np.searchsorted(p, D.loc[m, col].values, 'right') / len(p)
    return out


def pf(v):
    w, l = v[v > 0].sum(), -v[v < 0].sum()
    return w / l if l > 0 else np.nan


def st(x):
    v = x.pl
    if len(v) < 30: return f'n {len(v):5d}  -'
    dm = v.groupby(x.date).mean(); yp = v.groupby(x.year).mean()
    return f'n {len(v):6,d} {100 * v.mean():5.1f}% PF {pf(v):4.2f} t {dm.mean() / dm.std() * np.sqrt(len(dm)):4.1f} yrs+ {(yp > 0).sum()}/{len(yp)}'


D['p_s'] = prior_pct('skw')
D = D[D.year > 2012]
print(f'trades {len(D):,}; coverage: ' + ', '.join(f'{k} {100 * D[k].notna().mean():.0f}%' for k in F))
print('\n== (a) inside the skew top 10%: feature terciles (prior years). spread = (PF_t3 - 1) / (PF_t1 - 1); t3 = highest')
rows = []
for k in F:
    D['p_f'] = prior_pct(k) if False else np.nan
for k in F:
    pp = pd.Series(np.nan, index=D.index)
    # prior-year quantiles need the pre-2013 rows: recompute on the full cache
    rows.append(k)
FULL = pd.read_parquet('data/options/short_dated/ivrv_trades.parquet', columns=['root', 'date', 'exp', 'inst', 'kind', 'year'])
print('   (quantiles from prior years incl. 2012)')
tab = {}
for k in F:
    col = k
    base = D  # 2013+ rows carry the feature; thresholds from strictly earlier years within D (2013 itself uses 2012 = none)
    out = pd.Series(np.nan, index=D.index)
    for (k_, i), gg in D.groupby(['kind', 'inst']):
        for y in sorted(gg.year.unique()):
            p = np.sort(gg.loc[gg.year < y, col].dropna().values)
            if len(p) < 2000: continue
            m = gg.index[(gg.year == y) & gg[col].notna()]
            out[m] = np.searchsorted(p, D.loc[m, col].values, 'right') / len(p)
    D['p_f'] = out
    line = []
    for k_ in ['stock', 'etf']:
        for i in ['P25', 'C25']:
            x = D[(D.kind == k_) & (D.inst == i) & (D.p_s >= .9) & D.p_f.notna()]
            t1, t3 = x[x.p_f < 1 / 3], x[x.p_f >= 2 / 3]
            p1, p3 = pf(t1.pl), pf(t3.pl)
            tab[(k, f'{k_} {i}')] = (p3 - 1) / (p1 - 1) if p1 > 1 else np.nan
            line.append(f'{k_} {i}: t1 {st(t1)} | t3 {st(t3)}')
    print(f'-- {k}'); [print('   ' + l) for l in line]
    if k.startswith('vrph') or k.startswith('iv30/medIV'):
        x = D[(D.inst == 'STR') & D.p_f.notna()]
        for k_ in ['stock', 'etf']:
            xx = x[x.kind == k_]
            dec = np.minimum((xx.p_f * 10).astype(int), 9) + 1
            print(f'   STR {k_} by decile (mean %): ' + '  '.join(f'{100 * xx.pl[dec == d].mean():+.1f}' for d in range(1, 11))
                  + f'   d10 {st(xx[dec == 10])}  d1 {st(xx[dec == 1])}')
print('\n== spread summary (PF_t3 - 1) / (PF_t1 - 1) inside the skew top 10%:')
S = pd.Series(tab).unstack().round(2)
print(S.to_string())
