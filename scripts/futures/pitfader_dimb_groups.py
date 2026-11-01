"""PitFader PIT-14 — the delta imbalance on EVERY group's core, shown directly (user 2026-11-01: "Does the delta imbalance
feature help here? I haven't seen you use it either here or in the combined test group." — it was a candidate in both
stacks, picked only for equity (step 2) and ags; this table shows it on its own).

dimb_N = side × delta_N_bar / vol_N_bar (negative = the tape aggressing WITH the flush; ≥ 0 = the DIVERGENCE: a new
extreme printed without that aggression), N = 30, 60, 120, 180, 300. Each group on its core (PIT-12 / PIT-13), FIT 2015–22 /
HOLDOUT 2023–26, raw = next-bar vwap (−1 tick stress): per N, the fit quintiles (PF raw fit → holdout, holdout episodes,
mc = 0), and the two gates dimb_N ≥ 0 (divergence) and dimb_N ≤ the fit median (with the flush) at mc = 0 and mc = 1.

  cd research && python -u scripts/futures/pitfader_dimb_groups.py > data/futures/pitfader/dimb_groups.md 2> logs/pitfader_dimb_groups.log; echo exit $?
"""
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
W = [30, 60, 120, 180, 300]
CORES = {'equity': (['ES', 'MES', 'NQ'], 75, 300, True),
         'bonds': (['ZB', 'ZN', 'ZT'], 25, 600, False),
         'fx': (['6A', '6B', '6C', '6E', '6J', '6N'], 52, 300, True),
         'metals': (['HG', 'MGC'], 52, 300, True),
         'ngrb': (['NG', 'RB'], 75, 900, True),
         'energy': (['CL', 'MCL', 'NG', 'RB'], 75, 900, True),
         'ags': (['ZC', 'ZS', 'HE'], 52, 300, True)}
cols = (['root', 'td', 'side', 'sig_t', 'entry_px', 'tick', 'volat_20m', 'k_300', 'exit_t', 'exit_px', 'exit_t_600', 'exit_px_600',
         'exit_t_900', 'exit_px_900'] + [f'delta_{w}_bar' for w in W] + [f'vol_{w}_bar' for w in W])


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


def pf(r):
    r = r[np.isfinite(r)]
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else (g / l if g > 0 else 0.0)


def f(x): return 'inf' if np.isinf(x) else f'{x:.2f}'


print('# PIT-14 — the delta imbalance on every group\'s core (dimb_N ≥ 0 = the divergence)\n')
print('Quintile cells: PF raw fit → **holdout** (holdout episodes), mc = 0. Gate cells: PF raw / −1 tick (episodes); '
      'mc 1 = the greedy one-at-a-time book.\n')
for gname, (roots, K, n, use_floor) in CORES.items():
    t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(columns=cols, filter=(ds.field('volat_20m') > 0) & ds.field('root').isin(roots))
    c = lambda x: t[x].to_numpy(zero_copy_only=False)
    root, td = c('root'), pd.to_datetime(c('td'))
    s, epx, v, k3 = c('side').astype(np.float64), c('entry_px'), c('volat_20m'), c('k_300')
    ok = np.abs(epx) > 0
    ext, xpx = (c('exit_t'), c('exit_px')) if n == 300 else (c(f'exit_t_{n}'), c(f'exit_px_{n}'))
    ext = ext.astype(np.int64)
    RAW = 1e4 * s * (xpx - epx) / np.abs(epx); TBP = 1e4 * c('tick') / np.abs(epx); NET = RAW - TBP
    FIT = np.asarray(td <= pd.Timestamp('2022-12-31'))
    rc = pd.Categorical(root).codes.astype(np.int64); day = pd.factorize(td)[0].astype(np.int64)
    key = (rc * 100_000 + day) * 2 + (s > 0)
    EP = pd.factorize(pd.Series(key * 100_000 + ext))[0]
    SIG = c('sig_t').astype(np.int64)
    order = np.lexsort((SIG, s, td.values.astype('int64'), rc)); ko = key[order]
    grp = np.r_[0, np.cumsum(ko[1:] != ko[:-1])].astype(np.int64)
    def mc1(m):
        tk = replay(grp, SIG[order], ext[order], m[order]); out = np.zeros(len(tk), np.bool_); out[order] = tk; return out
    base = ok & (k3 >= K)
    if use_floor:                                    # PIT-7: per root a fixed volat floor chosen on fit
        VBP = 1e4 * v; FL = np.zeros(len(base), np.bool_)
        for r in sorted(set(root)):
            rr = base & (root == r); rf = rr & FIT
            if len(np.unique(EP[rf])) < 30:
                FL |= rr; continue
            best = None
            for q in (0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95):
                th = np.quantile(VBP[rf], q); m = rf & (VBP >= th)
                if len(np.unique(EP[m])) < 30:
                    break
                sc = NET[m].sum()
                if best is None or sc > best[0]:
                    best = (sc, th)
            FL |= rr & (VBP >= best[1])
        base = FL
    def gcell(m):
        out = []
        for mm in (m, mc1(m)):
            for part in (FIT, ~FIT):
                x = mm & part
                out.append(f'{f(pf(RAW[x]))} / {f(pf(NET[x]))} ({len(np.unique(EP[x])):,})')
        return out
    print(f'## {gname} ({", ".join(roots)}) · k300 ≥ {K}{" + abs floor" if use_floor else ""} · exit {n}\n')
    print('| gate | mc 0 fit | **mc 0 holdout** | mc 1 fit | **mc 1 holdout** |\n|---|---|---|---|---|')
    print('| the core | ' + ' | '.join(gcell(base)) + ' |')
    Q = {}
    for w in W:
        with np.errstate(all='ignore'):
            x = s * c(f'delta_{w}_bar') / c(f'vol_{w}_bar')
        med = np.nanmedian(x[base & FIT])
        print(f'| dimb_{w} ≥ 0 (divergence) | ' + ' | '.join(gcell(base & (x >= 0))) + ' |')
        print(f'| dimb_{w} ≤ {med:+.2f} (fit median: with the flush) | ' + ' | '.join(gcell(base & (x <= med))) + ' |')
        q = np.nanquantile(x[base & FIT], [0.2, 0.4, 0.6, 0.8]); e = [-np.inf, *q, np.inf]
        Q[w] = [f'{f(pf(RAW[m & FIT]))} → **{f(pf(RAW[m & ~FIT]))}** ({len(np.unique(EP[m & ~FIT])):,})'
                for k in range(5) for m in [base & (x >= e[k]) & (x < e[k + 1])]]
    print('\n| window (cells: PF raw fit → holdout (holdout episodes), mc = 0) | Q1 (most with the flush) | Q2 | Q3 | Q4 | Q5 (most divergent) |\n|---|---|---|---|---|---|')
    for w in W:
        print(f'| dimb_{w} | ' + ' | '.join(Q[w]) + ' |')
    print()
    del t
    log(f'{gname} done')
