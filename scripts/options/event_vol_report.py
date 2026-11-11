#!/usr/bin/env python3
"""Report for scripts/options/event_vol.py (data/options/event_vol/trades.parquet).

    cd research && python3 scripts/options/event_vol_report.py | tee logs/options_event_vol_report.log
"""
import numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
import sys
T = pd.read_parquet(sys.argv[1] if len(sys.argv) > 1 else 'data/options/event_vol/trades.parquet')
T = T[~T.split_in_hold & T.E1_settled & np.isfinite(T.ratio0) & (T.cost_0 > 0)
      & T.x_bid_A.notna() & T.x_bid_B.notna() & T.x_bid_D.notna() & T.x_bid_E.notna()].copy()
T['spread_pct'] = (T.ent_ask - T.ent_bid) / T.cost_0 * 100
print(f'trades {len(T)} | events {T[T.k == 10].shape[0]} at k=10 | names {T.root.nunique()} | R {T.R.min().date()} .. {T.R.max().date()}')
print('median entry spread % of mid by k:', T.groupby('k').spread_pct.median().round(1).to_dict())

K10 = T[T.k == 10].copy()
K10['dec'] = pd.qcut(K10.ratio0, 10, labels=False) + 1
print('\n=== CALIBRATION (k=10): implied earnings move vs history vs realized, deciles of ratio0 = imp_move / hist_rms ===')
print(K10.groupby('dec').apply(lambda x: pd.Series({
    'lo': x.ratio0.min(), 'hi': x.ratio0.max(), 'n': len(x), 'imp_move%': 100 * x.imp_move.mean(), 'hist_rms%': 100 * x.hist_rms.mean(),
    'real_rms%': 100 * np.sqrt((x.real_move ** 2).mean()), 'real/imp (rms)': np.sqrt((x.real_move ** 2).mean()) / np.sqrt((x.imp_move ** 2).mean()),
    'P(real>imp)%': 100 * (x.real_move > x.imp_move).mean()}), include_groups=False).round(3).to_string())

def pnl_table(D, by, f, hedged=False):
    tg = int(f * 100)
    def agg(x):
        r = {'n': len(x), 'ratio': x.ratio0.median()}
        for X in 'ABCDE':
            p = x[f'pnl_{X}_{tg}'] + (x[f'hedge_{X}'] if hedged else 0)
            r[f'{X}%prem'] = 100 * (p / x[f'cost_{tg}']).mean()
        r['B_win%'] = 100 * (x[f'pnl_B_{tg}'] > 0).mean()
        r['A%S'] = 100 * (x[f'pnl_A_{tg}'] / x.S0).mean(); r['B%S'] = 100 * (x[f'pnl_B_{tg}'] / x.S0).mean()
        return pd.Series(r)
    return D.groupby(by).apply(agg, include_groups=False).round(2)

for f in [0, 0.25, 0.5]:
    print(f'\n=== P&L (k=10, UNHEDGED, f={f}), % of premium paid; deciles of ratio0. A=R-1 close, B=R close, C=expiry, '
          f'D=sell when ratio>=1 else hold to R close, E=+25% target else R-1 ===')
    print(pnl_table(K10, 'dec', f).to_string())
print('\n=== P&L (k=10, DELTA-HEDGED, f=0.25) ===')
print(pnl_table(K10, 'dec', 0.25, hedged=True).to_string())

for kk in [5, 10, 15]:
    D = T[T.k == kk].copy(); D['q'] = pd.qcut(D.ratio0, 5, labels=False) + 1
    print(f'\n=== k={kk}: quintiles of ratio0, UNHEDGED f=0.25, % of premium ===')
    print(pnl_table(D, 'q', 0.25).to_string())

print('\n=== k=10, ALL events, by fill f (unhedged % of premium) ===')
for f in [0, 0.25, 0.5, 1]:
    tg = int(f * 100)
    print(f, {X: round(100 * (K10[f'pnl_{X}_{tg}'] / K10[f'cost_{tg}']).mean(), 2) for X in 'ABCDE'})
K10['yr'] = K10.R.dt.year
low = K10[K10.dec <= 2]
print('\n=== k=10, ratio0 deciles 1-2, by year: B and A, f=0.25, % premium ===')
print(low.groupby('yr').apply(lambda x: pd.Series({'n': len(x), 'A': 100 * (x.pnl_A_25 / x.cost_25).mean(),
      'B': 100 * (x.pnl_B_25 / x.cost_25).mean(), 'C': 100 * (x.pnl_C_25 / x.cost_25).mean()}), include_groups=False).round(1).to_string())
