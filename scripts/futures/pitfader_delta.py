"""PitFader PIT-8 — the VOLUME DELTA tested on its own (user 2026-11-01: "I am really surprised that the volume delta doesn't
help — have we tested various negative thresholds in the last 30s to 5m?").

The feature: the side-signed delta imbalance dimb_N = side × delta_N_bar / vol_N_bar over the last N present bars,
the signal bar included (delta = buyer- minus seller-aggressed volume; a cross fill counts 0): NEGATIVE = the tape
aggressed WITH the flush (selling into a long, buying into a short); −1 = all of it. N = 30 … 1200.
Thresholds: fixed values dimb_N ≤ t (t = −0.6 … 0) and, for contrast, ≥ t (t = 0, +0.2).
Bases: (B) the depth base k300 ≥ 52, the 300-bar exit; (C) the selective core k300 ≥ 75 + the per-root absolute volat
floor (PIT-7, chosen on fit), the 600-bar exit. Book: mc = 0 and its mc = 1 greedy replay; RAW = next-bar vwap; −1 tick
= stress; FIT 2015–2022 / HOLDOUT 2023–2026; episodes = the trips of one root-session-side exiting together.

  cd research && python -u scripts/futures/pitfader_delta.py > data/futures/pitfader/delta.md 2> logs/pitfader_delta.log; echo exit $?
"""
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
W = [30, 60, 120, 180, 300, 600, 1200]
cols = (['root', 'td', 'side', 'sig_t', 'entry_px', 'tick', 'volat_20m', 'k_300', 'exit_t', 'exit_px', 'exit_t_600', 'exit_px_600']
        + [f'delta_{w}_bar' for w in W] + [f'vol_{w}_bar' for w in W])
t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(columns=cols, filter=ds.field('volat_20m') > 0)
c = lambda n: t[n].to_numpy(zero_copy_only=False)
root, td = c('root'), pd.to_datetime(c('td'))
s, epx, v, K3 = c('side').astype(np.float64), c('entry_px'), c('volat_20m'), c('k_300')
ok = np.abs(epx) > 0
TBP = 1e4 * c('tick') / np.abs(epx)
SIG = c('sig_t').astype(np.int64)
EX = {300: (c('exit_t').astype(np.int64), c('exit_px')), 600: (c('exit_t_600').astype(np.int64), c('exit_px_600'))}
RAW = {n: 1e4 * s * (EX[n][1] - epx) / np.abs(epx) for n in EX}
with np.errstate(all='ignore'):
    DI = {w: s * c(f'delta_{w}_bar') / c(f'vol_{w}_bar') for w in W}
FIT = np.asarray(td <= pd.Timestamp('2022-12-31'))
VBP = 1e4 * v
rc = pd.Categorical(root).codes.astype(np.int64)
day = pd.factorize(td)[0].astype(np.int64)
key = (rc * 100_000 + day) * 2 + (s > 0)
EP = {n: pd.factorize(pd.Series(key * 100_000 + EX[n][0]))[0] for n in EX}
order = np.lexsort((SIG, s, td.values.astype('int64'), rc))
ko = key[order]
grp = np.r_[0, np.cumsum(ko[1:] != ko[:-1])].astype(np.int64)
del t
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


def mc1(mask, n):
    tk = replay(grp, SIG[order], EX[n][0][order], mask[order])
    out = np.zeros(len(tk), np.bool_); out[order] = tk
    return out


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else (g / l if g > 0 else 0.0)


def f(x): return 'inf' if np.isinf(x) else f'{x:.3f}'


def floor_book(base, n):
    """PIT-7: per root a fixed volat_20m floor (bp) chosen on fit, max Σ(raw − 1 tick), ≥ 30 fit episodes"""
    FL = np.zeros(len(base), np.bool_)
    for r in sorted(set(root)):
        rr = base & (root == r); rf = rr & FIT
        if len(np.unique(EP[n][rf])) < 30:
            FL |= rr; continue
        best = None
        for q in (0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95):
            th = np.quantile(VBP[rf], q); m = rf & (VBP >= th)
            if len(np.unique(EP[n][m])) < 30:
                break
            sc = (RAW[n][m] - TBP[m]).sum()
            if best is None or sc > best[0]:
                best = (sc, th)
        FL |= rr & (VBP >= best[1])
    return FL


BASES = [('B: k300 ≥ 52, exit 300', ok & (K3 >= 52), 300)]
BASES.append(('C: k300 ≥ 75 + the absolute volat floor, exit 600', floor_book(ok & (K3 >= 75), 600), 600))
print('# PIT-8 — the volume delta on its own: dimb_N = side × delta_N / vol_N (negative = the tape aggressing WITH the flush)\n')
print('RAW = next-bar vwap / −1 tick stress; FIT 2015–22, HOLDOUT 2023–26; mc = 0 cells: trips (episodes) · PF; mc = 1: PF (trips).\n')
for nm, base, n in BASES:
    print(f'## {nm}\n')
    print('The base\'s dimb quantiles (fit): ' + ' · '.join(
        f'N {w}: p10 {np.nanquantile(DI[w][base & FIT], .1):+.2f} / p50 {np.nanquantile(DI[w][base & FIT], .5):+.2f} / '
        f'p90 {np.nanquantile(DI[w][base & FIT], .9):+.2f}' for w in W) + '\n')
    b0f, b0h = base & FIT, base & ~FIT
    b1 = mc1(base, n)
    print(f'The base alone: mc = 0 fit {f(pf(RAW[n][b0f]))} · holdout **{f(pf(RAW[n][b0h]))}** / {f(pf(RAW[n][b0h] - TBP[b0h]))} '
          f'({b0h.sum():,}, {len(np.unique(EP[n][b0h])):,} ep) | mc = 1 holdout **{f(pf(RAW[n][b1 & ~FIT]))}** / '
          f'{f(pf(RAW[n][b1 & ~FIT] - TBP[b1 & ~FIT]))} ({(b1 & ~FIT).sum():,})\n')
    print('| window N | gate | kept (fit trips %) | mc = 0 fit raw | **mc = 0 holdout raw** / −1t (trips, ep) | mc = 1 fit raw | **mc = 1 holdout raw** / −1t (trips) |')
    print('|---|---|---|---|---|---|---|')
    for w in W:
        x = DI[w]
        for op, th in [('≤', -0.6), ('≤', -0.4), ('≤', -0.3), ('≤', -0.2), ('≤', -0.1), ('≤', 0.0), ('≥', 0.0), ('≥', 0.2)]:
            g = base & ((x <= th) if op == '≤' else (x >= th))
            gf, gh = g & FIT, g & ~FIT
            if len(np.unique(EP[n][gh])) < 20:
                continue
            g1 = mc1(g, n)
            print(f'| {w} | {op} {th:+.1f} | {100 * gf.sum() / max(1, b0f.sum()):.0f} % | {f(pf(RAW[n][gf]))} | '
                  f'**{f(pf(RAW[n][gh]))}** / {f(pf(RAW[n][gh] - TBP[gh]))} ({gh.sum():,}, {len(np.unique(EP[n][gh])):,}) | '
                  f'{f(pf(RAW[n][g1 & FIT]))} | **{f(pf(RAW[n][g1 & ~FIT]))}** / {f(pf(RAW[n][g1 & ~FIT] - TBP[g1 & ~FIT]))} ({(g1 & ~FIT).sum():,}) |')
    print()
log('done')
