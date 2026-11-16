#!/usr/bin/env python3
"""How much does the bid-ask spread take from the 0DTE shorts (user 2026-11-16)? Entry at the bid or the mid, exit at
the 15:59 ask or mid (mid_x = (bid_x + ask_x) / 2; bid_x = 0 when there is no bid), minute-hedged to 15:59 ($0.006 per
share; SPXW $0.06 per unit). Also the hold-to-settlement variants for reference (SPXW: the 16:00 cash close; ETFs /
stocks: the 17:30 price with the 15:59 hedge carried). % of the entry mid, day-clustered t.
    cd research && python3 scripts/options/zdte_spread.py > logs/options_zdte_spread.log
"""
import glob
import numpy as np, pandas as pd
pd.set_option('display.width', 250)
D = pd.concat([pd.read_parquet(f) for f in glob.glob('data/options/zdte/trades/*.parquet')], ignore_index=True)
CPS = np.where(D.root == 'SPXW', 0.06, 0.006)
D['hcost'] = D.turn * CPS
D['bid_x'] = D.bid_x.fillna(0); D['ask_x'] = D.ask_x.fillna(0)
D['settle17'] = np.where(D.right == 'C', np.maximum(D.s1730 - D.strike, 0), np.maximum(D.strike - D.s1730, 0))
D['hd17'] = D.hd + D.dlast.fillna(0) * (D.s1730 - D.close)
D['mid'] = (D.bid + D.ask) / 2
k = ['root', 'date', 'm']
A = D[D.inst == 'AP'].set_index(k); C = D[D.inst == 'AC'].set_index(k)
j = A.index.intersection(C.index)
S = A.loc[j].copy()
for c in ['bid', 'ask', 'mid', 'bid_x', 'ask_x', 'hd_x', 'hcost', 'settle', 'settle17', 'hd', 'hd17']: S[c] = A.loc[j, c] + C.loc[j, c]
S['inst'] = 'STR'
D = pd.concat([D[D.inst.isin(['P25', 'C25'])], S.reset_index()], ignore_index=True)
D = D[D.mid >= .05].copy()
D['mid_x'] = (D.bid_x + D.ask_x) / 2
M = {
    'bid -> ask (as run)': (D.bid - D.ask_x + D.hd_x - D.hcost) / D.mid,
    'bid -> mid': (D.bid - D.mid_x + D.hd_x - D.hcost) / D.mid,
    'mid -> mid (no spread)': (D.mid - D.mid_x + D.hd_x - D.hcost) / D.mid,
    'bid -> held to settlement': np.where(D.root == 'SPXW', (D.bid - D.settle + D.hd - D.hcost) / D.mid,
                                          (D.bid - D.settle17 + D.hd17 - D.hcost) / D.mid),
}
for kk, v in M.items(): D[kk] = v
D['entry_hs'] = (D.ask - D.bid) / 2 / D.mid
D['exit_hs'] = (D.ask_x - D.bid_x) / 2 / D.mid


def st(x, col):
    v = x[col]; dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
    return f'{100 * v.mean():6.1f}% PF {w / l:4.2f} t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}'


print('mean % of the entry mid | PF | day-clustered t;  entry / exit half-spread in % of the entry mid')
for r, g in D.groupby('root'):
    for i, x in g.groupby('inst'):
        print(f'{r:5s} {i:4s} n {len(x):7,d}  half-spread entry {100 * x.entry_hs.mean():4.1f}% exit {100 * x.exit_hs.mean():5.1f}%  || '
              + ' | '.join(f'{kk}: {st(x, kk)}' for kk in M))
