#!/usr/bin/env python3
"""Report for vv_long.py: baseline, VV's four features (deciles, spearman/pearson), by fill f.
    cd research && python3 scripts/options/vv_long_report.py > logs/options_vv_long_report.log
"""
import sys, numpy as np, pandas as pd
T = pd.read_parquet(sys.argv[1] if len(sys.argv) > 1 else 'data/options/vv_long/trades.parquet')
T = T[~T.split_in_hold & np.isfinite(T.ret_0) & (T.ent_ask > 0)].copy()
T['hs_pct'] = (T.ent_ask - T.ent_bid) / (T.ent_ask + T.ent_bid)
print(f'events {len(T)}, tickers {T.ticker.nunique()}, R {T.R.min().date()}..{T.R.max().date()}, '
      f'median td held {T.n_td.median():.0f}, median half-spread {100 * T.hs_pct.median():.1f}% of mid')
for f in [0, 25, 50, 100]:
    r = T[f'ret_{f}']
    print(f'  all f={f / 100:.2f}: mean {100 * r.mean():+.2f}%  median {100 * r.median():+.2f}%  t {r.mean() / r.std() * np.sqrt(len(r)):.2f}')
print('implied vs realized (calibration): mean iem %.3f  mean real %.3f  corr %.2f  P(real>iem) %.0f%%' % (
    T.iem.mean(), T.real_move.mean(), T[['iem', 'real_move']].corr().iloc[0, 1], 100 * (T.real_move > T.iem).mean()))
print('iem_pre vs iem_fwd: corr %.2f, median ratio %.2f, pre available %.0f%%' % (
    T[['iem_pre', 'iem_fwd']].corr().iloc[0, 1], (T.iem_pre / T.iem_fwd).median(), 100 * T.iv_pre.notna().mean()))
FEAT = ['implied_hist_avg_implied_ratio', 'gap_implied_hist_avg_realized', 'implied_to_last_implied_ratio',
        'gap_implied_last_realized', 'iem', 'iv1']
for ft in FEAT:
    d = T[np.isfinite(T[ft])].copy()
    if len(d) < 100: print(f'\n{ft}: n={len(d)} too few'); continue
    print(f'\n== {ft}: n={len(d)}  spearman(ret_0) {d[ft].corr(d.ret_0, method="spearman"):+.3f}  '
          f'pearson {d[ft].corr(d.ret_0):+.3f}  pearson(ret_25) {d[ft].corr(d.ret_25):+.3f}')
    d['dec'] = pd.qcut(d[ft].rank(method='first'), 10, labels=False) + 1
    g = d.groupby('dec')
    out = pd.DataFrame({'lo': g[ft].min(), 'hi': g[ft].max(), 'n': g.size(),
                        'mid%': 100 * g.ret_0.mean(), 'med%': 100 * g.ret_0.median(),
                        'f25%': 100 * g.ret_25.mean(), 'f50%': 100 * g.ret_50.mean(),
                        't25': g.ret_25.mean() / g.ret_25.std() * np.sqrt(g.size()),
                        'win%': 100 * g.ret_25.apply(lambda x: (x > 0).mean())})
    print(out.round(3).to_string())
    # by year for the best (lowest) two deciles
    lo = d[d.dec <= 2]
    print('   deciles 1-2 by year f=.25:', ', '.join(f'{y}: {100 * x.ret_25.mean():+.1f} (n{len(x)})' for y, x in lo.groupby(lo.R.dt.year)))
