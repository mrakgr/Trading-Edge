#!/usr/bin/env python3
"""Event variance, BOTH sides (user 2026-11-11): the SHORT straddle when the implied earnings move is rich vs the
stock's history (entry at R-1's close = the VV short's entry, k=1), against the VV gate; and the LONG side (k=10)
with the 'no event priced' events (se2 <= 0 -> ratio 0) put back in.
ratio = imp_move / hist_rms (scripts/options/event_vol.py). Exits: B = R close (crush), C = expiry.

    cd research && python3 scripts/options/event_vol_short.py | tee logs/options_event_vol_short.log
"""
import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
import sys
T = pd.read_parquet(sys.argv[1] if len(sys.argv) > 1 else 'data/options/event_vol/trades.parquet')
T = T[~T.split_in_hold & T.E1_settled & (T.cost_0 > 0) & T.x_bid_B.notna() & np.isfinite(T.hist_rms) & T.iv2.notna()].copy()
T['ratio'] = np.where(T.se2 <= 0, 0.0, T.ratio0)
T['fwd_ratio'] = T.iv1 / T.amb_vol
F = pd.read_parquet('data/options/vv/features.parquet', columns=['root', 'date', 'ts_slope_0_45', 'iv30_rv30', 'avg_vol30'])
T = T.merge(F.rename(columns={'date': 't0'}), on=['root', 't0'], how='left')
T['vv_gate'] = (T.avg_vol30 >= 1.5e6) & (T.iv30_rv30 >= 1.25) & (T.ts_slope_0_45 <= -0.00406)

def s_stats(x, side='s'):
    r = {'n': len(x), 'ratio': x.ratio.median(), 'imp%': 100 * x.imp_move.median(), 'real_rms%': 100 * np.sqrt((x.real_move ** 2).mean())}
    for X in 'BC':
        for f in [0, 25, 50]:
            p = x[f'{side}pnl_{X}_{f}']; base = x[f'cred_{f}'] if side == 's' else x[f'cost_{f}']
            r[f'{X}{f}'] = 100 * (p / base).mean()
        r[f'{X}win'] = 100 * (x[f'{side}pnl_{X}_25'] > 0).mean()
    base = x.cred_25 if side == 's' else x.cost_25
    r['B25_t'] = (x[f'{side}pnl_B_25'] / base).mean() / (x[f'{side}pnl_B_25'] / base).std() * np.sqrt(len(x))
    r['B25_worst'] = 100 * (x[f'{side}pnl_B_25'] / base).min()
    return pd.Series(r)

S = T[T.k == 1].copy()
print(f'SHORT, k=1 (entry R-1 close): {len(S)} events, {S.root.nunique()} names, {S.R.min().date()} .. {S.R.max().date()}; '
      f'ratio=0 (no event priced) {(S.ratio == 0).sum()}')
S['dec'] = pd.qcut(S.ratio.rank(method='first'), 10, labels=False) + 1
print('\n=== SHORT straddle by ratio decile, % of credit; B = crush exit at R close, C = expiry; f = 0/.25/.5 ===')
print(S.groupby('dec').apply(s_stats, include_groups=False).round(2).to_string())
print('\n=== SHORT: gates compared (B/C, % of credit) ===')
g = {'all': S.ratio.notna(), 'VV gate': S.vv_gate, 'ratio top 20%': S.dec >= 9, 'ratio top 10%': S.dec == 10,
     'fwd_ratio top 20%': S.fwd_ratio >= S.fwd_ratio.quantile(.8), 'VV gate & ratio top 20%': S.vv_gate & (S.dec >= 9),
     'VV gate & ratio bottom 50%': S.vv_gate & (S.dec <= 5), 'ratio >= 1.5': S.ratio >= 1.5}
print(pd.DataFrame({k: s_stats(S[m]) for k, m in g.items()}).T.round(2).to_string())
print('\ncorr at k=1 (spearman):'); print(S[['ratio', 'fwd_ratio', 'ts_slope_0_45', 'iv30_rv30']].corr('spearman').round(2).to_string())

L = T[T.k == 10].copy()
L['dec'] = pd.qcut(L.ratio.rank(method='first'), 10, labels=False) + 1
print(f'\n=== LONG straddle, k=10, by ratio decile incl. ratio=0 ({(L.ratio == 0).sum()} events), % of premium ===')
print(L.groupby('dec').apply(lambda x: s_stats(x, 'l' if False else ''), include_groups=False).round(2).to_string())
L0 = L[L.ratio == 0]; L0y = L0.groupby(L0.R.dt.year)
print('\nratio = 0 (front prices no event over the back), LONG k=10 by year (B, C at f=.25, % premium):')
print(L0y.apply(lambda x: pd.Series({'n': len(x), 'B25': 100 * (x.pnl_B_25 / x.cost_25).mean(), 'C25': 100 * (x.pnl_C_25 / x.cost_25).mean(),
      'iv1': x.iv1.median(), 'iv2': x.iv2.median(), 'T1': x.T1.median(), 'T2': x.T2.median()}), include_groups=False).round(2).to_string())
