"""PitFader PIT-12 — the combined test per financial group (user 2026-11-01: "Let's try the combined test group.").

Each group's PIT-9 core (FIT 2015–22 / HOLDOUT 2023–26; raw = next-bar vwap, −1 tick stress), then a GREEDY STACK chosen on
FIT ONLY over the PIT-11 wins + the PIT-8/9 delta divergence, each in its found direction:
  the leg z (z_since_flow, z_since_opp, z20: ≤), eff_ewma_20m / 10m (≤), the coil (consol_3m, consol_5m, consol_3m_lag1m:
  ≤), the delta divergence (dimb_60, dimb_180: ≥, thresholds 0 and the fit quantiles).
Thresholds = the core's FIT quintile edges (20/40/60/80 %). Each step adds the gate that best raises the FIT objective at
mc = 0, keeping ≥ MIN_KEEP of the core's fit episodes: --objective pf (default) = the FIT PF raw, +0.03 a step, ≤ 4 steps;
--objective net = Σ(raw − 1 tick) (the first run — it adds nothing to a core already profitable per trip, because every
gate removes positive-expectancy trips). Every step is shown with
its holdout at mc = 0, mc = 1 and a CAPPED book mc = 3 (PIT-10: the edge sits in averaging in; up to 3 trips open per
root-session-side, greedy by signal time).

  cd research && python -u scripts/futures/pitfader_combo.py --objective pf > data/futures/pitfader/combo.md 2> logs/pitfader_combo.log; echo exit $?
"""
import argparse
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
ap = argparse.ArgumentParser()
ap.add_argument('--objective', choices=['net', 'pf', 'pfmin'], default='pf',
                help="'net' = the FIT Σ(raw − 1 tick) (the first run: no gate can raise it on a core that is already "
                     "profitable per trip); 'pf' = the FIT PF raw at mc = 0, +MIN_GAIN per step, at most MAX_STEPS; "
                     "'pfmin' = the WORSE of the two fit halves' PF (2015–18, 2019–22): a gate must work in both")
ap.add_argument('--min-keep', type=float, default=0.40)
ap.add_argument('--groups', default='equity,bonds,fx')
ap.add_argument('--both-dirs', action='store_true', help='every candidate in BOTH directions (commodities may invert)')
ap.add_argument('--side', choices=['both', 'long', 'short'], default='both',
                help="PIT-16 (user 2026-11-04: split the longs from the shorts): one side's trips only, and the CORE is "
                     "then chosen on fit for that side too (as PIT-13 does for the commodities)")
a = ap.parse_args()
SIDE = {'both': None, 'long': 1, 'short': -1}[a.side]
MIN_KEEP, MIN_GAIN, MAX_STEPS = a.min_keep, 0.03, 4
GROUPS = {'equity': (['ES', 'MES', 'NQ'], 75, 300, True),
          'bonds': (['ZB', 'ZN', 'ZT'], 25, 600, False),
          'fx': (['6A', '6B', '6C', '6E', '6J', '6N'], 52, 300, True),
          # PIT-13 (user 2026-11-01: "Can we really not find any edge in them?"): K = None → the CORE is chosen on FIT too
          'metals': (['HG', 'MGC'], None, None, None),
          'ags': (['ZC', 'ZS', 'HE'], None, None, None),
          'energy': (['CL', 'MCL', 'NG', 'RB'], None, None, None),
          'ngrb': (['NG', 'RB'], None, None, None)}
# the scheduled reports: EIA crude / products Wednesday 10:30 ET (CL, MCL, RB), EIA natural-gas storage Thursday 10:30 (NG)
REPORT_DOW = {'CL': 2, 'MCL': 2, 'RB': 2, 'NG': 3}
CANDS = [('z_since_flow', '≤'), ('z_since_opp', '≤'), ('z20', '≤'), ('eff_ewma_20m', '≤'), ('eff_ewma_10m', '≤'),
         ('consol_3m', '≤'), ('consol_5m', '≤'), ('consol_3m_lag1m', '≤'), ('dimb60', '≥'), ('dimb180', '≥')]
cols = ['root', 'td', 'side', 'sig_t', 'entry_px', 'tick', 'volat_20m', 'k_300', 'exit_t', 'exit_px', 'exit_t_600', 'exit_px_600',
        'exit_t_900', 'exit_px_900',
        'sess_vwap', 'dist_sess_vwap', 'dlv_1200', 'dlv2_1200', 'vol_1200_bar', 'z_since_flow', 'z_since_opp', 'eff_ewma_20m',
        'eff_ewma_10m', 'consol_3m', 'consol_5m', 'consol_3m_lag1m', 'delta_60_bar', 'vol_60_bar', 'delta_180_bar', 'vol_180_bar']


@njit(cache=True)
def replay_k(grp, sig, ext, mask, k):
    """greedy mc = k: within a group take a masked trip while fewer than k taken trips are still open at its signal"""
    take = np.zeros(len(grp), np.bool_)
    opn = np.empty(max(k, 1), np.int64)
    g0, m = -1, 0
    for i in range(len(grp)):
        if grp[i] != g0:
            g0, m = grp[i], 0
        w = 0
        for j in range(m):                      # drop the trips that have exited by this signal
            if opn[j] > sig[i]:
                opn[w] = opn[j]; w += 1
        m = w
        if mask[i] and m < k:
            take[i] = True
            opn[m] = ext[i]; m += 1
    return take


def pf(r):
    r = r[np.isfinite(r)]
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else (g / l if g > 0 else 0.0)


def f(x): return 'inf' if np.isinf(x) else f'{x:.2f}'


print('# PIT-12 — the combined test: a fit-only greedy stack of the PIT-11 wins + the delta divergence, per financial group\n')
print('Cells: PF raw / −1 tick (episodes or trips). Thresholds = the core\'s FIT quintile edges; each step maximises the FIT '
      f'{ {"pf": "PF raw", "net": "Σ(raw − 1 tick)", "pfmin": "min(PF raw 2015–18, PF raw 2019–22)"}[a.objective] } at mc = 0, keeping ≥ {int(100 * MIN_KEEP)} % of the core\'s fit '
      'episodes. mc 3 = up to 3 trips open per root-session-side.\n')
for gname in a.groups.split(','):
    roots, K, n, use_floor = GROUPS[gname]
    flt = (ds.field('volat_20m') > 0) & ds.field('root').isin(roots)
    if SIDE is not None:
        flt = flt & (ds.field('side') == SIDE)
        K, n, use_floor = None, None, None
    t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(columns=cols, filter=flt)
    c = lambda x: t[x].to_numpy(zero_copy_only=False)
    root, td = c('root'), pd.to_datetime(c('td'))
    s, epx, v, k3 = c('side').astype(np.float64), c('entry_px'), c('volat_20m'), c('k_300')
    ok = np.abs(epx) > 0
    EXS = {300: (c('exit_t'), c('exit_px')), 600: (c('exit_t_600'), c('exit_px_600')), 900: (c('exit_t_900'), c('exit_px_900'))}
    TBP = 1e4 * c('tick') / np.abs(epx)
    FIT = np.asarray(td <= pd.Timestamp('2022-12-31')); YR = td.year.values
    H1 = FIT & (YR <= 2018); H2 = FIT & (YR >= 2019)
    rc = pd.Categorical(root).codes.astype(np.int64); day = pd.factorize(td)[0].astype(np.int64)
    key = (rc * 100_000 + day) * 2 + (s > 0)
    SIG = c('sig_t').astype(np.int64)
    VBP = 1e4 * v

    def floor_of(base, RAWn, NETn, EPn):
        FL = np.zeros(len(base), np.bool_)
        for r in sorted(set(root)):
            rr = base & (root == r); rf = rr & FIT
            if len(np.unique(EPn[rf])) < 30:
                FL |= rr; continue
            best = None
            for q in (0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95):
                th = np.quantile(VBP[rf], q); m = rf & (VBP >= th)
                if len(np.unique(EPn[m])) < 30:
                    break
                sc = NETn[m].sum()
                if best is None or sc > best[0]:
                    best = (sc, th)
            FL |= rr & (VBP >= best[1])
        return FL
    core_note = ''
    if K is None:                                    # PIT-13: the core chosen on FIT by the worse fit half, ≥ 150 fit episodes
        best = None
        for nn in (300, 600, 900):
            ex_, px_ = EXS[nn]; RAWn = 1e4 * s * (px_ - epx) / np.abs(epx); EPn = pd.factorize(pd.Series(key * 100_000 + ex_))[0]
            for KK in (25, 52, 75):
                for fl in (False, True):
                    m = ok & (k3 >= KK)
                    if fl:
                        m = floor_of(m, RAWn, RAWn - TBP, EPn)
                    if len(np.unique(EPn[m & FIT])) < 150:
                        continue
                    sc = min(pf(RAWn[m & H1]), pf(RAWn[m & H2]))
                    if best is None or sc > best[0]:
                        best = (sc, nn, KK, fl)
        _, n, K, use_floor = best
        core_note = f' (chosen on fit: min fit-half PF {best[0]:.2f})'
    ext, xpx = EXS[n]
    ext = ext.astype(np.int64)
    RAW = 1e4 * s * (xpx - epx) / np.abs(epx); NET = RAW - TBP
    EP = pd.factorize(pd.Series(key * 100_000 + ext))[0]
    order = np.lexsort((SIG, s, td.values.astype('int64'), rc)); ko = key[order]
    grp = np.r_[0, np.cumsum(ko[1:] != ko[:-1])].astype(np.int64)
    def mck(m, k):
        tk = replay_k(grp, SIG[order], ext[order], m[order], k); out = np.zeros(len(tk), np.bool_); out[order] = tk; return out
    base = ok & (k3 >= K)
    if use_floor:
        base = floor_of(base, RAW, NET, EP)
    F = {}
    with np.errstate(all='ignore'):
        lnv = np.log(c('sess_vwap')) + c('dist_sess_vwap')
        m1, m2 = c('dlv_1200') / c('vol_1200_bar'), c('dlv2_1200') / c('vol_1200_bar')
        F['z20'] = s * (lnv - m1) / np.sqrt(m2 - m1 ** 2)
        for x in ('z_since_flow', 'z_since_opp', 'eff_ewma_20m', 'eff_ewma_10m'):
            F[x] = s * c(x)
        for x in ('consol_3m', 'consol_5m', 'consol_3m_lag1m'):
            F[x] = c(x)
        F['dimb60'] = s * c('delta_60_bar') / c('vol_60_bar')
        F['dimb180'] = s * c('delta_180_bar') / c('vol_180_bar')
        F['hour'] = (SIG // 3600).astype(np.float64)
        dow = td.dayofweek.values
        rd = np.array([REPORT_DOW.get(r, -1) for r in root])
        F['report_hour'] = ((dow == rd) & (SIG >= 37800) & (SIG < 41400)).astype(np.float64)   # 10:30–11:30 on the report day
    nfit = len(np.unique(EP[base & FIT]))
    gates = []
    cands = CANDS + [('hour', '≤'), ('hour', '≥')] + ([('report_hour', '≥'), ('report_hour', '≤')] if gname in ('energy', 'ngrb') else [])
    if a.both_dirs:
        cands = cands + [(nm, '≥' if op == '≤' else '≤') for nm, op in CANDS]
    for nm, op in cands:
        x = F[nm]
        ths = [1.0 if op == '≥' else 0.0] if nm == 'report_hour' else (
            [10.0, 11.0, 12.0, 13.0, 14.0] if nm == 'hour' else list(np.nanquantile(x[base & FIT], [0.2, 0.4, 0.6, 0.8])))
        if nm.startswith('dimb'):
            ths = [0.0] + ths
        for th in ths:
            gates.append((nm, op, th, (x <= th) if op == '≤' else (x >= th)))

    def show(step, lab, m):
        cells = [str(step), lab]
        for part in (FIT, ~FIT):
            x = m & part
            cells.append(f'{f(pf(RAW[x]))} / {f(pf(NET[x]))} ({len(np.unique(EP[x])):,})')
        for k in (1, 3):
            mk = mck(m, k)
            for part in (FIT, ~FIT):
                x = mk & part
                cells.append(f'{f(pf(RAW[x]))} / {f(pf(NET[x]))} ({x.sum():,})')
        print('| ' + ' | '.join(cells) + ' |')
    print(f'## {gname} ({", ".join(roots)}){"" if SIDE is None else " · " + a.side.upper() + " only"} · core = k300 ≥ {K}{" + abs floor" if use_floor else ""} · exit {n}{core_note}\n')
    print('| step | gate added | mc 0 fit | **mc 0 holdout** | mc 1 fit | **mc 1 holdout** | mc 3 fit | **mc 3 holdout** |')
    print('|---|---|---|---|---|---|---|---|')
    mask = base.copy(); show(0, 'the core', mask)
    used = set()
    score = {'net': lambda m: NET[m & FIT].sum(), 'pf': lambda m: pf(RAW[m & FIT]),
             'pfmin': lambda m: min(pf(RAW[m & H1]), pf(RAW[m & H2]))}[a.objective]
    gain = 0.0 if a.objective == 'net' else MIN_GAIN
    while len(used) < (99 if a.objective == 'net' else MAX_STEPS):
        cur = score(mask); best = None
        for nm, op, th, g in gates:
            if (nm, op) in used:
                continue
            mm = mask & g
            if len(np.unique(EP[mm & FIT])) < MIN_KEEP * nfit:
                continue
            sc = score(mm)
            if sc > cur + gain and (best is None or sc > best[0]):
                best = (sc, nm, op, th, mm)
        if best is None:
            break
        _, nm, op, th, mm = best
        used.add((nm, op)); mask = mm
        show(len(used), f'**{nm} {op} {th:+.3g}**', mask)
    print('\nPer root, the final stack (mc 0 raw PF fit → holdout (holdout episodes)): ' + ' · '.join(
        f'{r} {f(pf(RAW[mask & FIT & (root == r)]))} → {f(pf(RAW[mask & ~FIT & (root == r)]))} ({len(np.unique(EP[mask & ~FIT & (root == r)])):,})'
        for r in sorted(set(root))))
    if SIDE is None:                                 # PIT-16: the pooled stack, side by side
        m1 = mck(mask, 1)
        print('\nThe final stack by side (PF raw / −1 tick (episodes)): ' + ' · '.join(
            f'{lab} mc 0 fit {f(pf(RAW[mask & FIT & (s == sd)]))} / {f(pf(NET[mask & FIT & (s == sd)]))} '
            f'({len(np.unique(EP[mask & FIT & (s == sd)])):,}), holdout {f(pf(RAW[mask & ~FIT & (s == sd)]))} / '
            f'{f(pf(NET[mask & ~FIT & (s == sd)]))} ({len(np.unique(EP[mask & ~FIT & (s == sd)])):,}); mc 1 holdout '
            f'{f(pf(RAW[m1 & ~FIT & (s == sd)]))} / {f(pf(NET[m1 & ~FIT & (s == sd)]))}'
            for lab, sd in (('LONG', 1.0), ('SHORT', -1.0))))
        # PIT-16: the one parameter the per-side searches disagreed on is the exit — the same entries, each exit, by side
        print('\nThe final stack\'s entries by exit and side (PF raw (mc 0 episodes) fit → holdout; mc 1 holdout):')
        for n2, (ex2, px2) in EXS.items():
            R2 = 1e4 * s * (px2 - epx) / np.abs(epx); E2 = ex2.astype(np.int64)
            EP2 = pd.factorize(pd.Series(key * 100_000 + E2))[0]
            t2 = replay_k(grp, SIG[order], E2[order], mask[order], 1); m2_ = np.zeros(len(t2), np.bool_); m2_[order] = t2
            print(f'- exit {n2}: ' + ' · '.join(
                f'{lab} {f(pf(R2[mask & FIT & (s == sd)]))} ({len(np.unique(EP2[mask & FIT & (s == sd)])):,}) → '
                f'{f(pf(R2[mask & ~FIT & (s == sd)]))} ({len(np.unique(EP2[mask & ~FIT & (s == sd)])):,}); mc 1 '
                f'{f(pf(R2[m2_ & ~FIT & (s == sd)]))}' for lab, sd in (('LONG', 1.0), ('SHORT', -1.0))))
    if SIDE is None:                                 # PIT-16 (user: SpikeFader liked longer exits AND higher leg counts)
        print('\nThe final stack, deeper by side (k300 ≥ K and exit; PF raw mc 0 fit → holdout (holdout episodes); mc 1 fit → holdout):')
        print('| side | k300 ≥ | exit | mc 0 fit | mc 0 holdout | mc 1 fit | mc 1 holdout |')
        print('|---|---|---|---|---|---|---|')
        for lab, sd in (('LONG', 1.0), ('SHORT', -1.0)):
            for kk in sorted({K, 52, 75, 100, 150} - {x for x in (25, 52, 75, 100, 150) if x < K}):
                for n2, (ex2, px2) in EXS.items():
                    R2 = 1e4 * s * (px2 - epx) / np.abs(epx); E2 = ex2.astype(np.int64)
                    EP2 = pd.factorize(pd.Series(key * 100_000 + E2))[0]
                    mm = mask & (k3 >= kk) & (s == sd)
                    t2 = replay_k(grp, SIG[order], E2[order], mm[order], 1); m2_ = np.zeros(len(t2), np.bool_); m2_[order] = t2
                    print(f'| {lab} | {kk} | {n2} | {f(pf(R2[mm & FIT]))} | {f(pf(R2[mm & ~FIT]))} ({len(np.unique(EP2[mm & ~FIT])):,}) | '
                          f'{f(pf(R2[m2_ & FIT]))} | {f(pf(R2[m2_ & ~FIT]))} |')
    print(f'\nThe final stack by year (mc = 0, PF raw (episodes)): ' + ' · '.join(
        f'{y} {f(pf(RAW[mask & (YR == y)]))} ({len(np.unique(EP[mask & (YR == y)])):,})' for y in sorted(set(YR))) + '\n')
    del t
    log(f'{gname} done')
