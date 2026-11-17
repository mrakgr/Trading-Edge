r"""BUILDUP STUDY, step 5: the FlushFader / SpikeFader books by the pre-signal signed order flow (buildup_trips.py).
'Move flow' = the aggressor imbalance IN THE DIRECTION OF THE FADED MOVE: −imb for FlushFader (a flush down: selling), +imb
for SpikeFader (a spike up: buying). The buildup study's prediction (docs/buildup_results.md): flushes with the STRONGEST
selling and spikes with the WEAKEST buying revert best. PF = Σ wins / |Σ losses| of ret_exit; 'trim' = the bottom 5 % of
returns removed (feedback: trim when comparing). Per year: the PF of the predicted-best quintile vs the rest.
  cd research && python -u scripts/equity/buildup_trips_report.py [_lit] > data/equity/buildup/trips_report[_lit].md; echo exit $?
"""
import sys
import numpy as np, pandas as pd
SUF = sys.argv[1] if len(sys.argv) > 1 else ''   # '' = the 1 s-bar tick rule; '_lit' = the lit trade-by-trade tick rule

def pf(r):
    w = r[r > 0].sum(); l = -r[r < 0].sum()
    return w / l if l > 0 else np.inf
def pft(r):
    r = np.sort(r)[int(len(r) * 0.05):]
    return pf(r)

for tag, name, sgn, best in (('ff', 'FlushFader (long fades of flushes)', -1, 4), ('sf', 'SpikeFader (short fades of spikes)', 1, 0)):
    T = pd.read_parquet(f'data/equity/buildup/trips_{tag}{SUF}.parquet'); T['y'] = T.trade_date.str[:4]
    r_all = T.ret_exit.to_numpy()
    print(f'# {name} [{"lit trade tick rule" if SUF else "1 s-bar tick rule"}]: {len(T):,} trips, book PF {pf(r_all):.3f} (trim {pft(r_all):.3f}), mean {100 * r_all.mean():+.2f} %\n')
    for N in (60, 300, 600, 1200):
        T['flow'] = sgn * T[f'imb_{N}']
        T['q'] = pd.qcut(T.flow.rank(method='first'), 5, labels=False)
        T = T[T[f'imb_{N}'].notna()].copy() if SUF else T
        print(f'## {N} bars before the signal (median span {T[f"secs_{N}"].median():.0f} s); move flow quintile edges ' +
              ', '.join(f'{x:+.3f}' for x in np.quantile(T.flow, [.2, .4, .6, .8])) + '\n')
        print('| move-flow quintile | n | mean % | median % | PF | PF trim 5 % | win |\n|---|---|---|---|---|---|---|')
        for q in range(5):
            r = T.ret_exit[T.q == q].to_numpy()
            print(f'| q{q + 1}{" (weakest)" if q == 0 else " (strongest)" if q == 4 else ""} | {len(r):,} | {100 * r.mean():+.2f} | {100 * np.median(r):+.2f} | '
                  f'{pf(r):.3f} | {pft(r):.3f} | {np.mean(r > 0):.0%} |')
        print()
    T['flow'] = sgn * T['imb_1200']; T['q'] = pd.qcut(T.flow.rank(method='first'), 5, labels=False)
    print(f'## By year, 1,200 bars: the predicted-best quintile (q{best + 1}) vs the other four\n')
    print('| year | n best | PF best | PF rest | n rest |\n|---|---|---|---|---|')
    for y, g in T.groupby('y'):
        a = g.ret_exit[g.q == best].to_numpy(); b = g.ret_exit[g.q != best].to_numpy()
        print(f'| {y} | {len(a)} | {pf(a):.2f} | {pf(b):.2f} | {len(b)} |')
    print()
