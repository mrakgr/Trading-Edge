#!/usr/bin/env python3
"""Report for skew_vert.py: the VV skew verticals. Debit spreads held to expiry, ret = payoff/debit - 1 (f = fill fraction
of the two legs' half-spreads). 'own skew' = call_skew for call spreads, put_skew for put spreads (more NEGATIVE =
steeper wing). VV gate: iv_long < RV (the ATM leg cheap) & iv_short > RV (the wing rich) & own skew steep.
    cd research && python3 scripts/options/skew_vert_report.py > logs/options_skew_vert_report.log
"""
import sys, numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
T = pd.read_parquet(sys.argv[1] if len(sys.argv) > 1 else 'data/options/skew_vert/trades.parquet')
T = T[~T.split_in_life & np.isfinite(T.ret_0) & (T.mid > 0.05)].copy()
T['own'] = np.where(T.side == 'C', T.call_skew, T.put_skew)
T['own_z'] = np.where(T.side == 'C', T.call_skew_z, T.put_skew_z)
T['own_pct'] = np.where(T.side == 'C', T.call_skew_pct, T.put_skew_pct)
T['hs_pct'] = T.hs / T.mid
T['yr'] = T.t0.dt.year
# momentum aligned with the spread's direction: + = the trend points the way the spread pays
for c in ['ret_63', 'ret_126', 'pslope_63', 'pslope_126']:
    T[c + '_al'] = np.where(T.side == 'C', 1, -1) * T[c]


def st(x, lab, w=34):
    if len(x) < 5: print(f'{lab:{w}s} n={len(x)}'); return
    r = x.ret_25; t = r.mean() / r.std() * np.sqrt(len(x))
    yrs = ' '.join(f'{100 * g.ret_25.mean():+.0f}' for _, g in x.groupby('yr'))
    print(f'{lab:{w}s} n={len(x):6d}  mid {100 * x.ret_0.mean():+6.1f}  f25 {100 * r.mean():+6.1f} (t {t:5.2f})  f50 '
          f'{100 * x.ret_50.mean():+6.1f}  f100 {100 * x.ret_100.mean():+6.1f}  win {100 * (x.payoff > x.mid).mean():3.0f}%  '
          f'hs/mid {100 * x.hs_pct.median():4.1f}%  yrs {yrs}')


def dec(x, col, lab, k=10):
    x = x.dropna(subset=[col])
    print(f'\n-- {lab}: deciles of {col} (spearman with ret_0 {x[col].corr(x.ret_0, method="spearman"):+.3f})')
    x = x.assign(d=pd.qcut(x[col].rank(method='first'), k, labels=False) + 1)
    for d_, g in x.groupby('d'): st(g, f'  d{d_:2d} [{g[col].min():+.3f}, {g[col].max():+.3f}]')


print(f'trades {len(T):,}, roots {T.root.nunique()}, t0 {T.t0.min().date()}..{T.t0.max().date()}; earn_in_life '
      f'{100 * T.earn_in_life.mean():.0f}%')
print('\n=== baseline by leg (all / no earnings in the life) ===')
for leg in ['c25', 'c15', 'p25', 'p15']:
    x = T[T.leg == leg]; st(x, f'{leg} all'); st(x[~x.earn_in_life], f'{leg} no earnings')
N = T[~T.earn_in_life]
print('\nmedians (no earnings): ' + ', '.join(f'{c} {N[c].median():.3f}' for c in ['atm', 'rv20', 'call_skew', 'put_skew', 'edge_rv20']))
for leg in ['c25', 'p25']:
    x = N[N.leg == leg]
    dec(x, 'own', f'{leg}, own skew level')
    dec(x, 'own_z', f'{leg}, own skew z (vs its 60d history)')
    dec(x, 'edge_rv20', f'{leg}, spread fair at RV20 / mid - 1')
    dec(x, 'iv_long_rv', f'{leg}, ATM IV / RV20') if 'iv_long_rv' in x else None
N = N.assign(iv_long_rv=N.iv_long / N.rv20, iv_short_rv=N.iv_short / N.rv20)
print('\n=== VV gate (no earnings in the life) ===')
for leg in ['c25', 'c15', 'p25', 'p15']:
    x = N[N.leg == leg]
    for rv in ['rv20', 'rv10']:
        g = x[(x.iv_long < x[rv]) & (x.iv_short > x[rv])]
        st(g, f'{leg} ATM<{rv}<wing')
        q = x.own.quantile(.2)
        st(g[g.own <= q], f'{leg} ATM<{rv}<wing & skew bottom 20%')
        st(g[g.own_z <= -1], f'{leg} ATM<{rv}<wing & skew z<=-1')
    st(x[x.own <= x.own.quantile(.1)], f'{leg} skew bottom 10% only')
    st(x[x.own_z <= -1.5], f'{leg} skew z<=-1.5 only')
print('\n=== momentum inside the gate (ATM<rv20<wing), aligned with the spread: + = trend toward the payoff ===')
G = N[(N.iv_long < N.rv20) & (N.iv_short > N.rv20)]
for leg in ['c25', 'p25']:
    x = G[G.leg == leg]
    for c in ['ret_63_al', 'ret_126_al', 'pslope_126_al', 'vslope_63', 'vslope_126']:
        x2 = x.dropna(subset=[c]); x2 = x2.assign(tq=pd.qcut(x2[c].rank(method='first'), 3, labels=['lo', 'mid', 'hi']))
        print(f'  {leg} {c} (spearman {x2[c].corr(x2.ret_0, method="spearman"):+.3f})')
        for tq, g in x2.groupby('tq', observed=True): st(g, f'    {tq}', 30)
