"""§S49ak-7 (user 2026-11-02): FRACTIONAL tick caps, exact from the integer-tick columns of a sim.parquet.

A signal vwap s = (L + phi) ticks; the cap s + f ticks floored = L + floor(phi + f), so every fractional cap is, per trip, one
of the simulated integer caps (+1, +2, +3 rebuilt this way equal the simulated columns to the dollar).
    cd research && python scripts/equity/flushfader_cap_fractional.py data/equity/flushfader_cap_sim_all/sim.parquet
"""
import sys, math, numpy as np, pandas as pd
R = pd.read_parquet(sys.argv[1]); R = R[R.err.isna()].copy() if 'err' in R else R
tk = np.where(R.signal_vwap >= 1, 0.01, 0.0001)
x = R.signal_vwap.values / tk; L = np.floor(x + 1e-6); phi = np.clip(x - L, 0, None)
name = {0: 'signal', 1: 'signal +1 tick', 2: 'signal +2 tick', 3: 'signal +3 tick', 4: 'signal +4 tick'}
FR = [0, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4]
FM = ('lit', 'queue', 'hidden')
def col(f, fm, a='pl'):
    k = np.floor(phi + f + 1e-9).astype(int)
    out = np.zeros(len(R))
    for kk in np.unique(k): m = k == kk; out[m] = R[f'{a}_{name[kk]}|{fm}'].values[m]
    return out
def tday(d):
    bd = pd.Series(d).groupby(R.trade_date.values).sum(); return bd.mean() / (bd.std(ddof=1) / math.sqrt(len(bd)))
yr = R.year.values; Y = sorted(set(yr))
print(f'{len(R):,} trips\n')
print('| cap | lit Δ $ | queue Δ $ | hidden Δ $ | queue t (by day) | queue years > 0 | queue worst year | queue unfilled | mean room above the signal vwap (ticks) |')
print('|---|---|---|---|---|---|---|---|---|')
for f in FR:
    cells = []
    for fm in FM:
        cells.append(col(f, fm).sum() - R[f'pl_none|{fm}'].sum())
    d = col(f, 'queue') - R['pl_none|queue'].values
    y = pd.Series(d).groupby(yr).sum()
    un = int((col(f, 'queue', 'q') <= 0).sum())
    room = (np.floor(phi + f + 1e-9) - phi).mean()
    lab = 'signal (floored)' if f == 0 else f'signal +{f:g} tick'
    print(f'| {lab} | {cells[0]:+,.0f} | {cells[1]:+,.0f} | {cells[2]:+,.0f} | {tday(d):.2f} | {(y>0).sum()} / {len(y)} | {y.min():+,.0f} | {un:,} | {room:+.2f} |')
print('\nqueue, Δ $ by year:\n')
print('| cap | ' + ' | '.join(Y) + ' |\n|---|' + '---|' * len(Y))
for f in FR[1:]:
    y = pd.Series(col(f, 'queue') - R['pl_none|queue'].values).groupby(yr).sum()
    print(f'| +{f:g} | ' + ' | '.join(f'{y[v]:+,.0f}' for v in Y) + ' |')
