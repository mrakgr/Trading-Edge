#!/usr/bin/env python3
"""FORWARD-VOL SLOPE for the short-dated shorts (user 2026-11-13: VV's term feature, as in the first system).
A = the traded expiry (1-10 trading days), B = the expiry nearest 30 calendar days (20-45) of the same day. Total variance
w = iv^2 x T (T = trading days / 252, as the IVs were computed); forward variance A->B = (wB - wA) / (TB - TA);
fslope = iv_A / sqrt(fwd var) - 1 (> 0 = the front is rich vs the forward period; NaN when fwd var <= 0, counted).
Short sold at the bid, DAILY delta hedge minus 2bp of S per share traded (hedge_daily.py), % of the mid premium.
STR = AP + AC (same strike): premiums, settles, hedges and turnover summed. Earnings in the hold excluded; eB = a release
between A's expiry and B's (inflates the forward: an event, not a mispricing) is reported apart.
    cd research && python3 scripts/options/short_dated_fwd.py > logs/options_short_dated_fwd.log
"""
import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250)
T = pd.read_parquet('data/options/short_dated/trades.parquet')
from sd_common import drop_bad; T = drop_bad(T)   # SH-36: vanished contracts (corporate actions)
T = T[T.inst.isin(['AP', 'AC', 'P25', 'C25']) & ~T.earn]
H = pd.read_parquet('data/options/short_dated/hedge.parquet')
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
keys = ['root', 'date', 'exp']
A_ = D[D.inst == 'AP'].set_index(keys); C_ = D[D.inst == 'AC'].set_index(keys)
j = A_.index.intersection(C_.index)
S_ = A_.loc[j].copy()
for c in ['bid', 'ask', 'settle', 'hd', 'turn']: S_[c] = A_.loc[j, c] + C_.loc[j, c]
S_['inst'] = 'STR'
D = pd.concat([D, S_.reset_index()], ignore_index=True)
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['pl'] = (D.mid * (1 - D.hs) - D.settle + D.hd) / D.mid - D.turn * D.S * 2e-4 / D.mid
# forward vol
TD = np.array(sorted(duckdb.connect('data/trading.db', read_only=True).sql(
    "select distinct date from daily_adjusted where ticker = 'SPY' and date >= '2010-06-01'").df().date), dtype='datetime64[D]')
ti = np.searchsorted(TD, D.date.values.astype('datetime64[D]'), 'right') - 1
expB = (D.date + pd.to_timedelta(D.cdte30, unit='D')).values.astype('datetime64[D]')
tB = np.searchsorted(TD, expB, 'right') - 1 - ti
D['TA'] = D.tdte / 252; D['TB'] = np.where(np.isfinite(D.cdte30), tB, np.nan) / 252
fv = (D.iv30 ** 2 * D.TB - D.iv_atm ** 2 * D.TA) / (D.TB - D.TA)
D['fwd'] = np.sqrt(fv.where(fv > 0))
D['fslope'] = D.iv_atm / D.fwd - 1
D['rslope'] = D.iv_atm / D.iv30 - 1
# a release between A's expiry and B's
u = duckdb.sql("select distinct root, underlying from 'data/options/universe_daily_full.parquet'").df().groupby('root').underlying.first()
E = pd.read_parquet('data/options/earnings_sched_full.parquet')
E = E[E.is_release | E['items'].fillna('').str.contains('2.02')]
ei = np.searchsorted(TD, E.et_date.values.astype('datetime64[D]'), 'left')
E['R'] = TD[np.minimum(ei + (E.session == 'AMC').astype(int).values, len(TD) - 1)]
ER = {t: np.unique(g.R.values.astype('datetime64[D]')) for t, g in E.groupby('ticker')}
D['eB'] = False
for r, idx in D[D.kind == 'stock'].groupby('root').groups.items():
    e = ER.get(u.get(r, r))
    if e is None: continue
    x = D.loc[idx]
    a0 = x.xd.values.astype('datetime64[D]'); b0 = expB[D.index.get_indexer(idx)]
    D.loc[idx, 'eB'] = (np.searchsorted(e, b0, 'right') - np.searchsorted(e, a0, 'right')) > 0
print(f'trades {len(D):,}; forward var <= 0 (A richer than all of B): {100 * (fv <= 0).mean():.1f}%; no B expiry: {100 * D.cdte30.isna().mean():.1f}%; '
      f'release between A and B: {100 * D.eB[D.kind == "stock"].mean():.1f}% of stock trades')
print(f"spearman(fslope, rslope) = {D[['fslope', 'rslope']].corr('spearman').iloc[0, 1]:.2f}")


def st(v, x):
    if len(v) < 50: return '-'
    dm = x.groupby('date').pl.mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    return f'n {len(v):6,d} {100 * v.mean():6.1f}% PF {w / l:4.2f} t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}'


for eb in [False, True]:
    X = D[(D.eB == eb) & (D.hs <= .05)].dropna(subset=['fslope'])
    print(f'\n===== {"NO release" if not eb else "a release"} between A and B; half-spread <= 5%; daily hedge - 2bp, sold at the bid')
    for feat in ['fslope', 'rslope']:
        print(f'-- deciles of {feat} (in-sample per kind x inst); decile 10 = front richest vs the forward')
        for (k, i), g in X.groupby(['kind', 'inst']):
            if eb and k == 'etf': continue
            g = g.dropna(subset=[feat])
            if len(g) < 2000: continue
            dec = pd.qcut(g[feat].rank(method='first'), 10, labels=False) + 1
            row = '  '.join(f'{100 * g.pl[dec == d].mean():+5.1f}' for d in range(1, 11))
            pf = lambda v: v[v > 0].sum() / -v[v < 0].sum()
            print(f'   {k:5s} {i:4s} mean% by decile: {row}   | d1 PF {pf(g.pl[dec == 1]):.2f}  d10 PF {pf(g.pl[dec == 10]):.2f}  '
                  f'| d10 range {g[feat][dec == 10].min():.2f}..  d1 ..{g[feat][dec == 1].max():.2f}')
# expanding top / bottom decile of fslope, no release between
X = D[~D.eB & (D.hs <= .05)].dropna(subset=['fslope']).copy()
print('\n===== fslope top / bottom decile with EXPANDING thresholds (prior years), no release A->B, hs <= 5%')
for (k, i), g in X.groupby(['kind', 'inst']):
    top = pd.Series(False, index=g.index); bot = top.copy()
    for y in sorted(g.year.unique()):
        p = g.loc[g.year < y, 'fslope']
        if len(p) < 2000: continue
        m = g.year == y
        top |= m & (g.fslope >= p.quantile(.9)); bot |= m & (g.fslope <= p.quantile(.1))
    print(f'   {k:5s} {i:4s} top: {st(g.pl[top], g[top])} | bottom: {st(g.pl[bot], g[bot])} | rest: {st(g.pl[~top & ~bot], g[~top & ~bot])}')
    if i in ('STR', 'P25'):
        yr = g[top].groupby('year').pl.mean() * 100
        print('        top by year: ' + '  '.join(f'{y}:{v:+.0f}' for y, v in yr.items()))
# does fslope add to the put-skew signal?
P = X[(X.inst == 'P25')].copy(); P['skewp'] = P.iv25p / P.iv_atm
print('\n===== P25: put skew (top 10% in-sample per kind) x fslope tercile')
for k, g in P.groupby('kind'):
    s = g.skewp >= g.skewp.quantile(.9)
    t3 = pd.qcut(g.fslope.rank(method='first'), 3, labels=['low', 'mid', 'high'])
    print(f'   {k}: ' + ' | '.join(f'skew top & fslope {t}: {st(g.pl[s & (t3 == t)], g[s & (t3 == t)])}' for t in ['low', 'mid', 'high']))
