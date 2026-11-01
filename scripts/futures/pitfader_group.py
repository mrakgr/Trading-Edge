"""PitFader PIT-9 — one market group through the configuration grid (user 2026-11-01: "I'd like to split bond futures from
commodities. Besides equity indices, bonds are what I am most interested in. How does this system perform on them?").

Groups (the split): bonds = ZB, ZN, ZT (SR3 = SOFR, a short-rate future, reported on its own); equity = ES, MES, NQ;
energy = CL, MCL, NG, RB; metals = HG, MGC; ags = ZC, ZS, HE; fx = the currency roots; vx = VX.
The grid, each at mc = 0 and its mc = 1 greedy replay, FIT 2015–22 / HOLDOUT 2023–26, RAW = next-bar vwap (−1 tick stress):
  depth k300 ≥ K (K = 0, 25, 52, 75, 100) × exit (300, 600, 900 bars);
  + the per-root ABSOLUTE volat floor (PIT-7: a fixed bp chosen on fit, max Σ(raw − 1 tick), ≥ MIN_EP fit episodes;
    fewer → unfloored);
  + the DELTA DIVERGENCE (PIT-8): dimb_60 ≥ 0 or dimb_180 ≥ 0 (the new extreme printed without aggression behind it).
Then per root and per year for the base configurations.

  cd research && python -u scripts/futures/pitfader_group.py --group bonds > data/futures/pitfader/group_bonds.md 2> logs/pitfader_group_bonds.log; echo exit $?
"""
import argparse
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

GROUPS = {'bonds': ['ZB', 'ZN', 'ZT'], 'sofr': ['SR3'], 'equity': ['ES', 'MES', 'NQ'], 'energy': ['CL', 'MCL', 'NG', 'RB'],
          'metals': ['HG', 'MGC'], 'ags': ['ZC', 'ZS', 'HE'], 'fx': ['6A', '6B', '6C', '6E', '6J', '6N', '6S', 'M6A', 'M6E', 'E7'],
          'vx': ['VX'], 'commodities': ['CL', 'MCL', 'NG', 'RB', 'HG', 'MGC', 'ZC', 'ZS', 'HE']}
ap = argparse.ArgumentParser()
ap.add_argument('--group', required=True, choices=list(GROUPS))
ap.add_argument('--min-ep', type=int, default=20)
a = ap.parse_args()
ROOTS = GROUPS[a.group]
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
X = [300, 600, 900]
cols = (['root', 'td', 'side', 'sig_t', 'entry_px', 'tick', 'volat_20m', 'k_300', 'exit_t', 'exit_px', 'delta_60_bar', 'vol_60_bar',
         'delta_180_bar', 'vol_180_bar'] + [f'{p}_{n}' for n in X[1:] for p in ('exit_t', 'exit_px')])
t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(
    columns=cols, filter=(ds.field('volat_20m') > 0) & ds.field('root').isin(ROOTS))
c = lambda n: t[n].to_numpy(zero_copy_only=False)
root, td = c('root'), pd.to_datetime(c('td'))
s, epx, v, K3 = c('side').astype(np.float64), c('entry_px'), c('volat_20m'), c('k_300')
ok = np.abs(epx) > 0
TBP = 1e4 * c('tick') / np.abs(epx)
SIG = c('sig_t').astype(np.int64)
EX = {300: (c('exit_t').astype(np.int64), c('exit_px'))}
for n in X[1:]:
    EX[n] = (c(f'exit_t_{n}').astype(np.int64), c(f'exit_px_{n}'))
RAW = {n: 1e4 * s * (EX[n][1] - epx) / np.abs(epx) for n in X}
with np.errstate(all='ignore'):
    D60 = s * c('delta_60_bar') / c('vol_60_bar')
    D180 = s * c('delta_180_bar') / c('vol_180_bar')
FIT = np.asarray(td <= pd.Timestamp('2022-12-31'))
YR = td.year.values
VBP = 1e4 * v
rc = pd.Categorical(root).codes.astype(np.int64)
day = pd.factorize(td)[0].astype(np.int64)
key = (rc * 100_000 + day) * 2 + (s > 0)
EP = {n: pd.factorize(pd.Series(key * 100_000 + EX[n][0]))[0] for n in X}
order = np.lexsort((SIG, s, td.values.astype('int64'), rc))
ko = key[order]
grp = np.r_[0, np.cumsum(ko[1:] != ko[:-1])].astype(np.int64)
del t
log(f'{a.group}: {int(ok.sum()):,} trips, roots {sorted(set(root))}')


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


def mc1(mask, n):
    tk = replay(grp, SIG[order], EX[n][0][order], mask[order])
    out = np.zeros(len(tk), np.bool_); out[order] = tk
    return out


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else (g / l if g > 0 else 0.0)


def f(x): return 'inf' if np.isinf(x) else f'{x:.2f}'


def floor(base, n):
    FL = np.zeros(len(base), np.bool_)
    for r in sorted(set(root)):
        rr = base & (root == r); rf = rr & FIT
        if len(np.unique(EP[n][rf])) < a.min_ep:
            FL |= rr; continue
        best = None
        for q in (0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95):
            th = np.quantile(VBP[rf], q); m = rf & (VBP >= th)
            if len(np.unique(EP[n][m])) < a.min_ep:
                break
            sc = (RAW[n][m] - TBP[m]).sum()
            if best is None or sc > best[0]:
                best = (sc, th)
        FL |= rr & (VBP >= best[1])
    return FL


def row(lab, m, n):
    m1 = mc1(m, n)
    out = [lab]
    for mm in (m, m1):
        for part in (FIT, ~FIT):
            x = mm & part
            ne = len(np.unique(EP[n][x]))
            out.append(f'{f(pf(RAW[n][x]))} / {f(pf(RAW[n][x] - TBP[x]))} ({ne:,})' if ne else '·')
    return '| ' + ' | '.join(out) + ' |'


print(f'# PIT-9 — {a.group} ({", ".join(sorted(set(root)))}) through the configuration grid\n')
print(f'{int(ok.sum()):,} mc = 0 trips. Each cell: PF raw / PF −1 tick (episodes). FIT 2015–22 / HOLDOUT 2023–26. '
      f'1 tick = {np.median(TBP[ok]):.2f} bp (median).\n')
print('| configuration | mc 0 fit | **mc 0 holdout** | mc 1 fit | **mc 1 holdout** |\n|---|---|---|---|---|')
for n in X:
    for K in (0, 25, 52, 75, 100):
        base = ok & (K3 >= K)
        print(row(f'exit {n} · k300 ≥ {K}', base, n))
    for K in (52, 75, 100):
        base = ok & (K3 >= K); fl = floor(base, n)
        print(row(f'exit {n} · k300 ≥ {K} + abs floor', fl, n))
        print(row(f'exit {n} · k300 ≥ {K} + abs floor + dimb60 ≥ 0', fl & (D60 >= 0), n))
        print(row(f'exit {n} · k300 ≥ {K} + abs floor + dimb180 ≥ 0', fl & (D180 >= 0), n))
    for K in (25, 52):
        base = ok & (K3 >= K)
        print(row(f'exit {n} · k300 ≥ {K} + dimb60 ≥ 0 (no floor)', base & (D60 >= 0), n))
        print(row(f'exit {n} · k300 ≥ {K} + dimb180 ≥ 0 (no floor)', base & (D180 >= 0), n))
print('\n## Per root (exit 300): PF raw / −1 tick (episodes), fit → holdout\n')
print('| root | bare | k300 ≥ 25 | k300 ≥ 52 | k300 ≥ 52 + dimb180 ≥ 0 |\n|---|---|---|---|---|')
for r in sorted(set(root)):
    rr = ok & (root == r)
    cells = []
    for m in (rr, rr & (K3 >= 25), rr & (K3 >= 52), rr & (K3 >= 52) & (D180 >= 0)):
        fi, ho = m & FIT, m & ~FIT
        cells.append(f'{f(pf(RAW[300][fi]))} → **{f(pf(RAW[300][ho]))}** / {f(pf(RAW[300][ho] - TBP[ho]))} ({len(np.unique(EP[300][ho])):,})')
    print(f'| {r} | ' + ' | '.join(cells) + ' |')
print('\n## By year (exit 300, mc = 0): PF raw (episodes)\n')
YS = sorted(set(YR))
print('| configuration | ' + ' | '.join(str(y) for y in YS) + ' |\n|---|' + '---|' * len(YS))
for lab, m in (('bare', ok), ('k300 ≥ 25', ok & (K3 >= 25)), ('k300 ≥ 52', ok & (K3 >= 52)),
               ('k300 ≥ 25 + dimb180 ≥ 0', ok & (K3 >= 25) & (D180 >= 0))):
    print(f'| {lab} | ' + ' | '.join(f'{f(pf(RAW[300][m & (YR == y)]))} ({len(np.unique(EP[300][m & (YR == y)])):,})' for y in YS) + ' |')
log('done')
