"""PitFader PIT-7 — more SELECTIVE: the depth ladder pushed higher, and an ABSOLUTE volatility floor per instrument chosen on
the fit years (user 2026-11-01: "This is telling us that we need to be more selective in our trading. If needed, we
should push k300 higher and see where that leads us. Besides that, we need to find a better way of calibrating
volatility for each instrument. I am not sure that vrel in particular is doing a good job. We should find an absolute
value on the fit set and see if it works well on the holdout as well.").

The book: the mc = 0 sampler (data/futures/pitfader/trips_mc0, the 300-bar exit), and its mc = 1 greedy replay. RAW = the
next-bar-vwap return (no spread model); −1 tick = the stress line. FIT 2015–2022, HOLDOUT 2023–2026. Episodes = trips of
one (root, session, side) that exit together (mc = 0 trips are correlated; the episode count is the independent unit).
1. k300 ≥ K for K up to 300 (k300 = the fresh 20m extremes since the last 5m opposite one = the depth).
2. Per ROOT, an ABSOLUTE floor on volat_20m (bp per 30-bar slot): the candidates are the root's FIT-period quantiles
   (0, 10, …, 95 %) as fixed bp values; the floor = the one maximising the fit book's Σ (raw bp − 1 tick) — where the
   expectancy after a tick turns positive — on the k300 ≥ K base, with ≥ MIN_EP fit episodes left; then applied
   UNCHANGED to the holdout. A root with < MIN_EP fit episodes on the base gets NO floor (it stays in the book, so the
   three pooled books hold the same roots). Compared with vrel ≥ 0.91 (PIT-5) on the same base.

  cd research && python -u scripts/futures/pitfader_select.py > data/futures/pitfader/select.md 2> logs/pitfader_select.log; echo exit $?
"""
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
MIN_EP = 30
QS = [0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95]
cols = ['root', 'td', 'side', 'sig_t', 'exit_t', 'entry_px', 'exit_px', 'tick', 'volat_20m', 'k_300']
t = ds.dataset('data/futures/pitfader/trips_mc0').to_table(columns=cols, filter=ds.field('volat_20m') > 0)
c = lambda n: t[n].to_numpy(zero_copy_only=False)
root, td = c('root'), pd.to_datetime(c('td'))
s, epx, v, K3 = c('side').astype(np.float64), c('entry_px'), c('volat_20m'), c('k_300')
ok = np.abs(epx) > 0
RAW = 1e4 * s * (c('exit_px') - epx) / np.abs(epx)
TBP = 1e4 * c('tick') / np.abs(epx)
NET = RAW - TBP
VBP = 1e4 * v
SIG, EXT = c('sig_t').astype(np.int64), c('exit_t').astype(np.int64)
FIT = np.asarray(td <= pd.Timestamp('2022-12-31'))
rc = pd.Categorical(root).codes.astype(np.int64)
day = pd.factorize(td)[0].astype(np.int64)
key = (rc * 100_000 + day) * 2 + (s > 0)
EP = pd.factorize(pd.Series(key * 100_000 + EXT))[0]        # an episode = the trips of one group exiting together
M = pd.DataFrame({'root': root, 'td': td, 'v': v})
med = M.groupby(['root', 'td']).v.median().rename('m').reset_index().sort_values(['root', 'td'])
med['trail'] = med.groupby('root').m.transform(lambda x: x.shift(1).rolling(20, min_periods=10).median())
VREL = v / M.merge(med[['root', 'td', 'trail']], on=['root', 'td'], how='left').trail.values
del M, med, t
order = np.lexsort((SIG, s, td.values.astype('int64'), rc))
ko = key[order]
grp = np.r_[0, np.cumsum(ko[1:] != ko[:-1])].astype(np.int64)
log(f'{int(ok.sum()):,} trips')


@njit(cache=True)
def replay(grp, sig, ext, mask):
    take = np.zeros(len(grp), np.bool_)
    g0, last = -1, -1
    for i in range(len(grp)):
        if grp[i] != g0:
            g0, last = grp[i], -1
        if mask[i] and sig[i] >= last:
            take[i] = True
            last = ext[i]
    return take


def mc1(mask):
    tk = replay(grp, SIG[order], EXT[order], mask[order])
    out = np.zeros(len(tk), np.bool_); out[order] = tk
    return out


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else (g / l if g > 0 else 0.0)


def f(x): return 'inf' if np.isinf(x) else f'{x:.3f}'


def cell(m):
    return (f'{m.sum():,} ({len(np.unique(EP[m])):,}) | **{f(pf(RAW[m]))}** / {f(pf(NET[m]))} | {RAW[m].mean():+.2f}'
            if m.sum() else '0 | | ')


print('# PIT-7 — more selective: the depth ladder pushed up, and an absolute volatility floor per instrument\n')
print('RAW = next-bar vwap (no spread model) / −1 tick = the stress line; FIT 2015–2022, HOLDOUT 2023–2026; '
      'trips (episodes). Each cell: trips (episodes) | **PF raw** / PF −1 tick | mean bp raw.\n')
print('## 1. The depth ladder: k300 ≥ K\n')
print('| K | mc | fit | | | holdout | | |\n|---|---|---|---|---|---|---|---|')
for K in (0, 25, 52, 75, 100, 150, 200, 300):
    m = ok & (K3 >= K)
    for nm, mm in (('0', m), ('1', mc1(m))):
        print(f'| {K} | {nm} | {cell(mm & FIT)} | {cell(mm & ~FIT)} |')

for KB in (52, 100):
    base = ok & (K3 >= KB)
    print(f'\n## 2. The absolute volatility floor per root, chosen on FIT, on the base k300 ≥ {KB}\n')
    print('| root | floor bp (fit quantile) | fit: trips (ep) · PF raw / −1t | holdout: trips (ep) · PF raw / −1t | holdout, no floor: PF raw / −1t |')
    print('|---|---|---|---|---|')
    FLOOR = np.zeros(len(RAW), np.bool_)
    for r in sorted(set(root)):
        rr = base & (root == r)
        rf = rr & FIT
        if len(np.unique(EP[rf])) < MIN_EP:           # too few fit episodes to choose a floor: the root stays, unfloored
            FLOOR |= rr
            print(f'| {r} | — (< {MIN_EP} fit episodes: no floor) | | {(rr & ~FIT).sum():,} · {f(pf(RAW[rr & ~FIT]))} / {f(pf(NET[rr & ~FIT]))} | |')
            continue
        best = None
        for q in QS:
            th = np.quantile(VBP[rf], q)
            m = rf & (VBP >= th)
            if len(np.unique(EP[m])) < MIN_EP:
                break
            sc = NET[m].sum()
            if best is None or sc > best[0]:
                best = (sc, q, th)
        _, q, th = best
        m = rr & (VBP >= th)
        FLOOR |= m
        a, b = m & FIT, m & ~FIT
        nb = rr & ~FIT
        print(f'| {r} | {th:.2f} (p{int(100 * q)}) | {a.sum():,} ({len(np.unique(EP[a])):,}) · {f(pf(RAW[a]))} / {f(pf(NET[a]))} | '
              f'{b.sum():,} ({len(np.unique(EP[b])):,}) · **{f(pf(RAW[b]))}** / {f(pf(NET[b]))} | {f(pf(RAW[nb]))} / {f(pf(NET[nb]))} |')
    print(f'\n### Pooled, base k300 ≥ {KB}\n')
    print('| book | mc | fit | | | holdout | | |\n|---|---|---|---|---|---|---|---|')
    VR = base & (VREL >= 0.9083)
    for nm, m in (('base only', base), ('base + vrel ≥ 0.91', VR), ('base + the per-root absolute floor', FLOOR)):
        for mcn, mm in (('0', m), ('1', mc1(m))):
            print(f'| {nm} | {mcn} | {cell(mm & FIT)} | {cell(mm & ~FIT)} |')
    yr = td.year.values
    print(f'\n### The per-root-floor book by year (mc = 0): trips (episodes) · PF raw / −1 tick\n')
    print('| ' + ' | '.join(str(y) for y in sorted(set(yr))) + ' |\n|' + '---|' * len(set(yr)))
    print('| ' + ' | '.join(f'{(FLOOR & (yr == y)).sum():,} ({len(np.unique(EP[FLOOR & (yr == y)])):,}) · {f(pf(RAW[FLOOR & (yr == y)]))} / {f(pf(NET[FLOOR & (yr == y)]))}'
                            for y in sorted(set(yr))) + ' |')
log('done')
