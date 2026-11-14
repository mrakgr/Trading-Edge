#!/usr/bin/env python3
"""IV / RV (user 2026-11-14: the variance risk premium; which RV estimate - Yang-Zhang?).

RV estimators per underlying-day t (window ENDING at t, no lookahead), on split/dividend-continuous prices (x n):
  cc10, cc20          close-to-close (total-return log returns)
  yz5..yz60           Yang-Zhang: sigma^2 = s_on^2 + k s_oc^2 + (1-k) s_rs^2, k = 0.34 / (1.34 + (w+1)/(w-1))
  yz10x, yz20x        Yang-Zhang with the earnings reaction days MASKED (stocks)
  har                 sqrt((yz5^2 + yz20^2 + yz60^2) / 3)
Annualised x sqrt(252). Future RV over a trade's hold = close-to-close over (t, expiry] (what a daily-hedged short
experiences): sqrt(252 / h x sum r^2).
1) forecast quality on the STR trades: Spearman(log est, log future) and the median log(future / est) (bias), by DTE.
2) IV/RV (iv = the expiry's ATM IV) as a signal: top / bottom decile by prior years' quantiles (per kind x inst), for
   STR (the pure variance-premium trade), P25, C25 - sold at the bid, daily hedge - 2bp (SH-29), hs <= 5%, no earnings.
3) IV/RV terciles inside the put / call skew signals (top 10%).
    cd research && python3 scripts/options/ivrv.py > logs/options_ivrv.log 2>&1
"""
import os, sys
import duckdb, numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sd_common import drop_bad
pd.set_option('display.width', 250)

# ---- trades: STR (from AP + AC), P25, C25, with the daily hedge
T = pd.read_parquet('data/options/short_dated/trades.parquet')
T = drop_bad(T)
T = T[T.inst.isin(['AP', 'AC', 'P25', 'C25']) & ~T.earn]
H = pd.read_parquet('data/options/short_dated/hedge.parquet', columns=['root', 'date', 'exp', 'K', 'r', 'inst', 'hd', 'turn'])
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
del T, H
keys = ['root', 'date', 'exp']
A_ = D[D.inst == 'AP'].set_index(keys); C_ = D[D.inst == 'AC'].set_index(keys)
j = A_.index.intersection(C_.index)
S_ = A_.loc[j].copy()
for c in ['bid', 'ask', 'settle', 'hd', 'turn']: S_[c] = A_.loc[j, c] + C_.loc[j, c]
S_['inst'] = 'STR'
D = pd.concat([D[D.inst.isin(['P25', 'C25'])], S_.reset_index()], ignore_index=True)
del A_, C_, S_
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid
D = D[D.hs <= .05]
D['year'] = D.date.dt.year
D['pl'] = (D.mid * (1 - D.hs) - D.settle + D.hd) / D.mid - D.turn * D.S * 2e-4 / D.mid

# ---- RV estimators per underlying
u = duckdb.sql("select distinct root, underlying from 'data/options/universe_daily_full.parquet'").df().groupby('root').underlying.first()
D['und'] = D.root.map(lambda r: u.get(r, r))
c = duckdb.connect('data/trading.db', read_only=True)
tl = ','.join(repr(t) for t in D.und.unique())
da = c.sql(f"""select ticker, date, open, high, low, close, n, cum_div from daily_adjusted where ticker in ({tl})
               and date >= '2011-01-01' order by ticker, date""").df()
da['date'] = pd.to_datetime(da.date)
da['seg'] = (da.groupby('ticker').date.diff().dt.days > 45).groupby(da.ticker).cumsum()
g = da.groupby(['ticker', 'seg'])
pc1 = g.close.shift(1) * g.n.shift(1); dC = (da.cum_div - g.cum_div.shift(1)).fillna(0)
o, h, l, cl = da.open * da.n, da.high * da.n, da.low * da.n, da.close * da.n
da['r_on'] = np.log((o + dC) / pc1)
da['r_oc'] = np.log(cl / o)
da['r_cc'] = np.log((cl + dC) / pc1)
da['rs'] = np.log(h / cl) * np.log(h / o) + np.log(l / cl) * np.log(l / o)
bad = ~np.isfinite(da.r_on) | ~np.isfinite(da.r_oc) | (o <= 0) | (h <= 0) | (l <= 0)
da.loc[bad, ['r_on', 'r_oc', 'rs']] = np.nan
# earnings reaction days (stocks), to mask
E = pd.read_parquet('data/options/earnings_sched_full.parquet')
E = E[E.is_release | E['items'].fillna('').str.contains('2.02')]
TD = np.array(sorted(da[da.ticker == 'SPY'].date.values.astype('datetime64[D]')))
ei = np.searchsorted(TD, E.et_date.values.astype('datetime64[D]'), 'left')
E['R'] = pd.to_datetime(TD[np.minimum(ei + (E.session == 'AMC').astype(int).values, len(TD) - 1)])
er = set(zip(E.ticker, E.R))
da['earn'] = [x in er for x in zip(da.ticker, da.date)]


def yz(df, w, mask=None):
    on, oc, rs = df.r_on, df.r_oc, df.rs
    if mask is not None: on, oc, rs = on.where(~mask), oc.where(~mask), rs.where(~mask)
    gg = df[['ticker', 'seg']]
    mp = max(3, int(w * .7))
    v_on = on.groupby([gg.ticker, gg.seg]).transform(lambda x: x.rolling(w, min_periods=mp).var())
    v_oc = oc.groupby([gg.ticker, gg.seg]).transform(lambda x: x.rolling(w, min_periods=mp).var())
    v_rs = rs.groupby([gg.ticker, gg.seg]).transform(lambda x: x.rolling(w, min_periods=mp).mean())
    k = 0.34 / (1.34 + (w + 1) / (w - 1))
    return np.sqrt((v_on + k * v_oc + (1 - k) * v_rs).clip(lower=0) * 252)


def cc(df, w):
    return df.r_cc.groupby([df.ticker, df.seg]).transform(lambda x: x.rolling(w, min_periods=int(w * .7)).std()) * np.sqrt(252)


EST = {}
for w in (5, 10, 20, 30, 60): EST[f'yz{w}'] = yz(da, w)
EST['yz10x'] = yz(da, 10, da.earn); EST['yz20x'] = yz(da, 20, da.earn)
EST['cc10'] = cc(da, 10); EST['cc20'] = cc(da, 20)
EST['har'] = np.sqrt((EST['yz5'] ** 2 + EST['yz20'] ** 2 + EST['yz60'] ** 2) / 3)
for k_, v in EST.items(): da[k_] = v
# future close-to-close RV over (t, expiry]: cumulative sum of r_cc^2 per segment
da['c2'] = da.r_cc.fillna(0) ** 2
da['cum2'] = da.groupby(['ticker', 'seg']).c2.cumsum()
names = list(EST)
D = D.merge(da[['ticker', 'date', 'cum2'] + names].rename(columns={'ticker': 'und'}), on=['und', 'date'], how='left')
xc = da[['ticker', 'date', 'cum2']].rename(columns={'ticker': 'und', 'date': 'xd', 'cum2': 'cum2x'})
D['xd'] = pd.to_datetime(D.xd)
D = D.merge(xc, on=['und', 'xd'], how='left')
D['rv_fut'] = np.sqrt((D.cum2x - D.cum2).clip(lower=0) * 252 / D.tdte)
D = D[D.year > 2012]
print(f'trades {len(D):,} (STR {int((D.inst == "STR").sum()):,}, P25 {int((D.inst == "P25").sum()):,}, C25 {int((D.inst == "C25").sum()):,})')

# ---- 1) forecast quality
S = D[(D.inst == 'STR') & (D.rv_fut > 0)].copy()
S['td'] = pd.cut(S.tdte, [0, 1, 5, 10], labels=['1', '2-5', '6-10'])
print('\n== 1) forecasting the realized vol over the hold (STR trades, hs<=5%, no earnings): Spearman(est, future) / median log(future/est)')
rows = []
for nm in names + ['iv_atm']:
    r = {'est': nm}
    for k_, x in S.groupby(['kind']):
        ok = x[nm].notna() & (x[nm] > 0)
        r[f'{k_[0]} rho'] = x.loc[ok, [nm, 'rv_fut']].corr('spearman').iloc[0, 1]
        r[f'{k_[0]} bias'] = np.median(np.log(x.loc[ok, 'rv_fut'] / x.loc[ok, nm]))
    for td, x in S[S.kind == 'stock'].groupby('td', observed=True):
        ok = x[nm].notna() & (x[nm] > 0)
        r[f'stock rho DTE {td}'] = x.loc[ok, [nm, 'rv_fut']].corr('spearman').iloc[0, 1]
    rows.append(r)
print(pd.DataFrame(rows).set_index('est').round(3).to_string())


# ---- 2) IV/RV deciles (prior-year thresholds)
def prior_pct(d, col):
    out = pd.Series(np.nan, index=d.index)
    for (k_, i), gg in d.groupby(['kind', 'inst']):
        for y in sorted(gg.year.unique()):
            p = np.sort(D.loc[(D.kind == k_) & (D.inst == i) & (D.year < y), col].dropna().values)
            if len(p) < 2000: continue
            m = gg.index[(gg.year == y) & gg[col].notna()]
            out[m] = np.searchsorted(p, d.loc[m, col].values, 'right') / len(p)
    return out


def st(x):
    v = x.pl
    if len(v) < 30: return f'n {len(v):6d}  -'
    dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum(); yp = v.groupby(x.year).mean()
    return (f'n {len(v):7,d}  mean {100 * v.mean():6.1f}%  PF {w / l:4.2f}  t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}  '
            f'yrs+ {(yp > 0).sum()}/{len(yp)}  worst yr {100 * yp.min():+.0f}%')


print('\n== 2) IV/RV: top / bottom decile (prior-year quantiles per kind x inst); sold at the bid, daily hedge - 2bp')
for nm in ['cc20', 'yz10', 'yz20', 'yz20x', 'har', 'yz60']:
    D['ivrv'] = D.iv_atm / D[nm]
    D['p'] = prior_pct(D, 'ivrv')
    print(f'-- IV / {nm}')
    for k_ in ['stock', 'etf']:
        for i in ['STR', 'P25', 'C25']:
            x = D[(D.kind == k_) & (D.inst == i) & D.p.notna()]
            print(f'   {k_:5s} {i:4s} top10%: {st(x[x.p >= .9])} | bottom10%: {st(x[x.p < .1])} | all: {st(x)}')

# ---- 3) inside the skew signals
nm = 'yz20x'
D['ivrv'] = D.iv_atm / D[nm]; D['p_v'] = prior_pct(D, 'ivrv')
D['skw'] = np.where(D.inst == 'P25', D.iv25p, np.where(D.inst == 'C25', D.iv25c, np.nan)) / D.iv_atm
D['p_s'] = prior_pct(D, 'skw')
print(f'\n== 3) IV/{nm} terciles (prior years) inside the skew top 10%')
for k_ in ['stock', 'etf']:
    for i in ['P25', 'C25']:
        x = D[(D.kind == k_) & (D.inst == i) & (D.p_s >= .9) & D.p_v.notna()]
        print(f'   {k_:5s} {i}: ' + ' | '.join(f'ivrv t{t + 1}: {st(x[(x.p_v >= t / 3) & (x.p_v < (t + 1) / 3)])}' for t in range(3)))
