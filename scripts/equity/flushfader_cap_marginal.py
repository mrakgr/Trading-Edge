"""§S49ak-7 (user 2026-11-02): the tick caps by where the signal vwap sits inside its tick (phi), and the MARGINAL tick.

Every cap of flushfader_cap_sim.py is floored to the tick, so "signal" = the signal vwap rounded DOWN and "+1 tick" = it
rounded UP wherever the vwap is off the grid. Reads a sim.parquet:
    cd research && python scripts/equity/flushfader_cap_marginal.py data/equity/flushfader_cap_sim_all/sim.parquet
"""
import sys, math, numpy as np, pandas as pd
p = sys.argv[1]
K = ['signal'] + [f'signal +{k} tick' for k in (1,2,3,4)]
cols = ['symbol','trade_date','year','signal_vwap','entry_px','exit_px','n','err'] + [f'{a}_{r}|{fm}' for r in ['none']+K for fm in ('lit','queue','hidden') for a in ('pl','q')]
R = pd.read_parquet(p, columns=cols)
R = R[R.err.isna()].copy()
tk = np.where(R.signal_vwap >= 1, 0.01, 0.0001)
phi = R.signal_vwap / tk - np.floor(R.signal_vwap / tk + 1e-6)
phi = np.where(phi < 1e-6, 0.0, phi)
R['phi'] = phi
on = phi == 0
print(f'{len(R):,} trips; signal vwap ON the tick grid: {on.sum():,} ({on.mean():.1%}) -> there +1 tick != ceil(signal)')
print(f'phi (where the signal vwap sits inside its tick, 0 = on the grid): quartiles {np.quantile(phi,[.25,.5,.75]).round(2)}')
print(f'"signal" cap below the signal vwap by phi ticks: mean {phi.mean():.2f} tick; room above the vwap of +k = k - phi\n')
for fm in ('queue','lit','hidden'):
    base = R[f'pl_none|{fm}']
    print(f'### {fm}: Δ $ vs none by phi bucket (trips)')
    bk = pd.cut(R.phi, [-0.01, 1e-9, 0.25, 0.5, 0.75, 1.0], labels=['on grid', '(0,.25]', '(.25,.5]', '(.5,.75]', '(.75,1)'])
    cnt = bk.value_counts().reindex(bk.cat.categories)
    print('| cap | ' + ' | '.join(f'{c} ({n:,})' for c, n in cnt.items()) + ' | all | unfilled |')
    print('|---|' + '---|' * (len(cnt) + 2))
    for r in K:
        d = R[f'pl_{r}|{fm}'] - base
        g = d.groupby(bk, observed=False).sum()
        print(f'| {r} | ' + ' | '.join(f'{g[c]:+,.0f}' for c in cnt.index) + f' | **{d.sum():+,.0f}** | {(R[f"q_{r}|{fm}"] <= 0).sum():,} |')
    print()
# the marginal tick: what each extra tick adds, by price bucket (queue)
pb = pd.cut(R.signal_vwap, [0, 2, 5, 10, 20, 1e9], labels=['< $2', '$2-5', '$5-10', '$10-20', '> $20'])
print('### queue: the MARGINAL tick, Δ $ of cap k vs cap k−1, by price')
cnt = pb.value_counts().reindex(pb.cat.categories)
print('| step | ' + ' | '.join(f'{c} ({n:,})' for c, n in cnt.items()) + ' | all | years > 0 |')
print('|---|' + '---|' * (len(cnt) + 2))
prev = 'none'
for a, b in [('none','signal'), ('signal','signal +1 tick'), ('signal +1 tick','signal +2 tick'), ('signal +2 tick','signal +3 tick'), ('signal +3 tick','signal +4 tick')]:
    d = R[f'pl_{b}|queue'] - R[f'pl_{a}|queue']
    g = d.groupby(pb, observed=False).sum(); y = d.groupby(R.year).sum()
    print(f'| {a} → {b} | ' + ' | '.join(f'{g[c]:+,.0f}' for c in cnt.index) + f' | **{d.sum():+,.0f}** | {(y>0).sum()} / {len(y)} |')

# every fill model: each step's Δ, t clustered by day, years > 0; and the signal cap ROUNDED TO NEAREST
# (= the floored signal cap where phi < 0.5, the ceiling = +1 tick where phi >= 0.5; on the grid = floor)
print('\n### every fill model: the marginal step (Δ $, t by day, years > 0)')
print('| step | lit | queue | hidden |\n|---|---|---|---|')
def tday(d):
    bd = d.groupby(R.trade_date).sum(); return bd.mean() / (bd.std(ddof=1) / math.sqrt(len(bd)))
near = lambda fm: np.where(R.phi >= 0.5, R[f'pl_signal +1 tick|{fm}'], R[f'pl_signal|{fm}'])
steps = [('none','signal'), ('signal','signal +1 tick'), ('signal +1 tick','signal +2 tick'), ('signal +2 tick','signal +3 tick'), ('signal +3 tick','signal +4 tick')]
for a, b in steps + [('none', 'NEAREST'), ('none', 'signal +1 tick'), ('none', 'signal +3 tick'), ('signal +1 tick', 'signal +3 tick')]:
    cells = []
    for fm in ('lit','queue','hidden'):
        A = R[f'pl_{a}|{fm}']; Bv = pd.Series(near(fm), index=R.index) if b == 'NEAREST' else R[f'pl_{b}|{fm}']
        d = Bv - A; y = d.groupby(R.year).sum()
        cells.append(f'{d.sum():+,.0f} (t {tday(d):+.2f}, {(y>0).sum()}/{len(y)})')
    print(f'| {a} → {"signal, rounded to nearest" if b == "NEAREST" else b} | ' + ' | '.join(cells) + ' |')
