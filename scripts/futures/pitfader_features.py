"""PitFader PIT-11 — the z-score, efficiency and coil families on the FINANCIAL groups (user 2026-11-01: "I'd like to test
the zscore, eff and coil features on them [equity indexes, bonds, FX]. Are there any easy wins to be had using those
features?").

Each group on its PIT-9 core (FIT 2015–22 / HOLDOUT 2023–26, raw = next-bar vwap, −1 tick stress):
  equity = ES, MES, NQ · k300 ≥ 75 + the per-root absolute volat floor · exit 300
  bonds  = ZB, ZN, ZT  · k300 ≥ 25 · exit 600
  fx     = 6A 6B 6C 6E 6J 6N · k300 ≥ 52 + the absolute floor · exit 300 (6S, which failed PIT-9, and the thin micros out)
Features (directional ones SIDE-SIGNED: negative = into the extreme on both sides):
  z: z20 = side·(ln vwap − the 20m vw mean of ln vwap)/its sd · z_since_flow / z_since_opp (side-signed)
  eff: eff_20m, eff_10m, eff_since_flow, eff_since_opp, eff_ewma_20m / _10m (side-signed) · eff_9ema_20m / _10m,
       eff_rng_20m / _10m, |eff_20m| (unsigned)
  coil: consol_3m / 5m / 10m / 20m and their _lag1m twins
Per feature: the base split into its FIT quintiles; per quintile PF raw fit → holdout (holdout episodes); then the best
one-sided cut (keep ≥ / ≤ a fit quintile edge, keeping ≥ 40 % of the fit episodes) chosen on FIT, with its holdout.

  cd research && python -u scripts/futures/pitfader_features.py > data/futures/pitfader/features.md 2> logs/pitfader_features.log; echo exit $?
"""
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
GROUPS = {'equity': (['ES', 'MES', 'NQ'], 75, 300, True),
          'bonds': (['ZB', 'ZN', 'ZT'], 25, 600, False),
          'fx': (['6A', '6B', '6C', '6E', '6J', '6N'], 52, 300, True)}
SIGNED = ['z_since_flow', 'z_since_opp', 'eff_20m', 'eff_10m', 'eff_since_flow', 'eff_since_opp', 'eff_ewma_20m', 'eff_ewma_10m']
UNSIGNED = ['eff_9ema_20m', 'eff_9ema_10m', 'eff_rng_20m', 'eff_rng_10m'] + \
           [f'consol_{w}{l}' for w in ('3m', '5m', '10m', '20m') for l in ('', '_lag1m')]
cols = (['root', 'td', 'side', 'sig_t', 'entry_px', 'tick', 'volat_20m', 'k_300', 'exit_t', 'exit_px', 'exit_t_600', 'exit_px_600',
         'sess_vwap', 'dist_sess_vwap', 'dlv_1200', 'dlv2_1200', 'vol_1200_bar'] + SIGNED + UNSIGNED)


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


print('# PIT-11 — z-score, efficiency and coil features on the financial groups\n')
print('Per feature: its FIT quintiles of the group\'s core; cells = PF raw fit → **holdout** (holdout episodes), mc = 0. '
      '"best cut" = the one-sided cut (≥ / ≤ a fit quintile edge, ≥ 40 % of the fit episodes kept) with the highest FIT PF, '
      'and its holdout at mc = 0 and mc = 1.\n')
for gname, (roots, K, n, use_floor) in GROUPS.items():
    t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(columns=cols, filter=(ds.field('volat_20m') > 0) & ds.field('root').isin(roots))
    c = lambda x: t[x].to_numpy(zero_copy_only=False)
    root, td = c('root'), pd.to_datetime(c('td'))
    s, epx, v, k3 = c('side').astype(np.float64), c('entry_px'), c('volat_20m'), c('k_300')
    ok = np.abs(epx) > 0
    ext, xpx = (c('exit_t'), c('exit_px')) if n == 300 else (c(f'exit_t_{n}'), c(f'exit_px_{n}'))
    ext = ext.astype(np.int64)
    RAW = 1e4 * s * (xpx - epx) / np.abs(epx); TBP = 1e4 * c('tick') / np.abs(epx)
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
    if use_floor:                                   # PIT-7: per root a fixed volat floor chosen on fit
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
                sc = (RAW[m] - TBP[m]).sum()
                if best is None or sc > best[0]:
                    best = (sc, th)
            FL |= rr & (VBP >= best[1])
        base = FL
    F = {}
    with np.errstate(all='ignore'):
        lnv = np.log(c('sess_vwap')) + c('dist_sess_vwap')
        m1, m2 = c('dlv_1200') / c('vol_1200_bar'), c('dlv2_1200') / c('vol_1200_bar')
        F['z20'] = s * (lnv - m1) / np.sqrt(m2 - m1 ** 2)
        for x in SIGNED:
            F[x] = s * c(x)
        F['abs_eff_20m'] = np.abs(c('eff_20m'))
        for x in UNSIGNED:
            F[x] = c(x)
    b1 = mc1(base)
    bf, bh = base & FIT, base & ~FIT
    nfit_ep = len(np.unique(EP[bf]))
    print(f'## {gname} ({", ".join(roots)}) · k300 ≥ {K}{" + abs floor" if use_floor else ""} · exit {n}\n')
    print(f'The core alone: mc = 0 fit {f(pf(RAW[bf]))} → holdout **{f(pf(RAW[bh]))}** / {f(pf(RAW[bh] - TBP[bh]))} '
          f'({len(np.unique(EP[bh])):,} ep); mc = 1 fit {f(pf(RAW[b1 & FIT]))} → holdout **{f(pf(RAW[b1 & ~FIT]))}** / '
          f'{f(pf(RAW[b1 & ~FIT] - TBP[b1 & ~FIT]))}\n')
    print('| feature (quintile cells: PF raw fit → holdout (holdout episodes), mc = 0) | Q1 (lowest) | Q2 | Q3 | Q4 | Q5 (highest) | best cut (fit) | its holdout mc 0 / −1t (ep) | its holdout mc 1 / −1t |')
    print('|---|---|---|---|---|---|---|---|---|')
    for nm, x in F.items():
        q = np.nanquantile(x[bf], [0.2, 0.4, 0.6, 0.8])
        edges = [-np.inf, *q, np.inf]
        cells = []
        for k in range(5):
            m = base & (x >= edges[k]) & (x < edges[k + 1])
            cells.append(f'{f(pf(RAW[m & FIT]))} → **{f(pf(RAW[m & ~FIT]))}** ({len(np.unique(EP[m & ~FIT])):,})')
        best = None
        for th in q:
            for op in ('≥', '≤'):
                m = base & ((x >= th) if op == '≥' else (x <= th))
                if len(np.unique(EP[m & FIT])) < 0.4 * nfit_ep:
                    continue
                p = pf(RAW[m & FIT])
                if best is None or p > best[0]:
                    best = (p, op, th, m)
        p, op, th, m = best
        mm1 = mc1(m)
        hh, h1 = m & ~FIT, mm1 & ~FIT
        print(f'| {nm} | ' + ' | '.join(cells) + f' | {op} {th:+.3g} → {f(p)} | **{f(pf(RAW[hh]))}** / {f(pf(RAW[hh] - TBP[hh]))} '
              f'({len(np.unique(EP[hh])):,}) | **{f(pf(RAW[h1]))}** / {f(pf(RAW[h1] - TBP[h1]))} |')
    print()
    del t
    log(f'{gname} done')
