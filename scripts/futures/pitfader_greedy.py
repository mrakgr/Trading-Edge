"""PitFader PIT-5 — the efficiency GREEDY over the ported features, on VOLATILITY-NORMALISED thresholds (user 2026-11-01:
"Are there some cheap features like the ones the current FlushFader system has that actually improve net when added to
the system? We did a greedy search over them ..."; "Yeah, let's go with vol normalized thresholds for each instrument").

The book: the mc = 0 sampler (data/futures/pitfader/trips_mc0) replayed GREEDILY into the mc = 1 book — one trip at a time
per (root, session, side), a gated signal skipped, the next one taken once the last taken trip has exited. A gate is an
entry condition, so the replay of the gated sampler IS the gated mc = 1 engine (shared-exit semantics; PIT-2 control).
UNITS (the user: vol-normalised): every price distance / slope is divided by the root's own volat_20m at the signal
(the typical 30-bar |ln| move), and SIDE-SIGNED so that "stretched into the extreme" is NEGATIVE on both sides:
  speed  = side·ln(vwap / vwap_60_prev) / v      d1m  = side·ln(vwap / opp_60) / v       dlv = side·ln(vwap / leg vwap) / v
  crf    = side·chg_since_run_first_ext / v      cslu = side·chg_since_ct / v
  ssf    = side·ols_slope_since_flow·30 / v      s20 / s5 = side·ols_slope_{1200,300}·30 / v   accel = s10 − s20
  dhf    = side·d_opp_flow / v (the arming drop) rng20v = rng_20m / v
  z20    = side·z of ln(vwap) vs the 20m volume-weighted moments   rflow = side·ols_r_since_flow
Unit-free as recorded: k, k_300 (= the depth), k_180, k_600, |eff_20m|, |eff_10m|, eff_9ema_10m, rngf = rng_300 / rng_20m,
the coil (consol_5m_lag1m), v10r = (vol_10_bar/10) / (vol_60_bar/60), the delta imbalance side·delta_N_bar / vol_N_bar
(N = 30, 60, 300, 1200: negative = the tape is selling into a long), gap_60, the hour.
VOLATILITY itself, CAUSALLY: vrel = volat_20m / the root's median volat_20m over its PREVIOUS 20 sessions (the signals'
values; no full-sample percentile), and vt = volat_20m in ticks (the cost scale).
THE SEARCH: candidates = each feature × its fit-period quantiles {5..95 %}, kept ≥ or ≤. Each step adds the gate that
raises the FIT book's NET most (Σ net bp: equal notional, −1 tick a round trip, the PIT-1 cost); stop when none adds net.
FIT = 2015–2022, HOLDOUT = 2023–2026 (thresholds from the fit period only); both reported per step.
--roots narrows the universe (a market group); default all.

  cd research && python -u scripts/futures/pitfader_greedy.py --tag all > data/futures/pitfader/greedy_all.md 2> logs/pitfader_greedy_all.log; echo exit $?
"""
import argparse
import sys
import time

import numpy as np
import pandas as pd
from numba import njit

ap = argparse.ArgumentParser()
ap.add_argument('--roots', default='', help='comma-separated roots (default: all)')
ap.add_argument('--tag', default='all')
ap.add_argument('--fit-end', default='2022-12-31')
ap.add_argument('--max-steps', type=int, default=10)
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

COLS = ['root', 'td', 'side', 'sig_t', 'exit_t', 'entry_px', 'exit_px', 'tick', 'volat_20m', 'sess_vwap', 'dist_sess_vwap',
        'vwap_60_prev', 'opp_60', 'dv_leg', 'vol_leg', 'chg_since_run_first_ext', 'chg_since_ct', 'ols_slope_since_flow',
        'ols_r_since_flow', 'ols_slope_1200', 'ols_slope_600', 'ols_slope_300', 'd_opp_flow', 'rng_20m', 'rng_300',
        'dlv_1200', 'dlv2_1200', 'vol_1200_bar', 'k', 'k_300', 'k_180', 'k_600', 'eff_20m', 'eff_10m', 'eff_9ema_10m',
        'consol_5m_lag1m', 'vol_10_bar', 'vol_60_bar', 'gap_60', 'delta_30_bar', 'vol_30_bar', 'delta_60_bar',
        'delta_300_bar', 'vol_300_bar', 'delta_1200_bar']
# ⚠ MEMORY (2026-11-01: the pandas version peaked at 13.6 GB on all roots): one pyarrow read with the root filter
# pushed down, each column to numpy as it is needed, the features kept as float32, the raw columns dropped.
import pyarrow.dataset as ds
filt = ds.field('volat_20m') > 0
if a.roots:
    filt = filt & ds.field('root').isin(a.roots.split(','))
tbl = ds.dataset('data/futures/pitfader/trips_mc0').to_table(columns=COLS, filter=filt)
C = {}
def col(c):
    if c not in C:
        C[c] = tbl[c].to_numpy(zero_copy_only=False)
        if C[c].dtype.kind == 'f':
            C[c] = C[c].astype(np.float64)
    return C[c]
keep = np.abs(col('entry_px')) > 0
root = tbl['root'].to_numpy(zero_copy_only=False)[keep]
td = pd.to_datetime(tbl['td'].to_numpy(zero_copy_only=False)[keep])
N = int(keep.sum())
log(f'{N:,} trips, {len(set(root))} roots')
def g(c): return col(c)[keep]
s, v = g('side').astype(np.float64), g('volat_20m')
lnv = np.log(g('sess_vwap')) + g('dist_sess_vwap')   # the signal bar's ln vwap: dist_sess_vwap = ln(vwap / sess_vwap)
F = {}
def put(name, x):
    F[name] = np.where(np.isfinite(x), x, np.nan).astype(np.float32)
with np.errstate(all='ignore'):
    put('speed', s * (lnv - np.log(g('vwap_60_prev'))) / v)
    put('d1m', s * (lnv - np.log(g('opp_60'))) / v)
    put('dlv', s * (lnv - np.log(g('dv_leg') / g('vol_leg'))) / v)
    put('crf', s * g('chg_since_run_first_ext') / v)
    put('cslu', s * g('chg_since_ct') / v)
    put('ssf', s * g('ols_slope_since_flow') * 30 / v)
    put('rflow', s * g('ols_r_since_flow'))
    put('s20', s * g('ols_slope_1200') * 30 / v)
    put('s5', s * g('ols_slope_300') * 30 / v)
    put('accel', s * (g('ols_slope_600') - g('ols_slope_1200')) * 30 / v)
    put('dhf', s * g('d_opp_flow') / v)
    put('rng20v', g('rng_20m') / v)
    put('rngf', g('rng_300') / g('rng_20m'))
    m1, m2 = g('dlv_1200') / g('vol_1200_bar'), g('dlv2_1200') / g('vol_1200_bar')
    put('z20', s * (lnv - m1) / np.sqrt(m2 - m1 ** 2))
    put('K', g('k')); put('k300', g('k_300')); put('k180', g('k_180')); put('k600', g('k_600'))
    put('eff20a', np.abs(g('eff_20m'))); put('eff10a', np.abs(g('eff_10m'))); put('e9', g('eff_9ema_10m'))
    put('coil', g('consol_5m_lag1m'))
    put('v10r', (g('vol_10_bar') / 10) / (g('vol_60_bar') / 60))
    put('dimb30', s * g('delta_30_bar') / g('vol_30_bar'))
    put('dimb60', s * g('delta_60_bar') / g('vol_60_bar'))
    put('dimb300', s * g('delta_300_bar') / g('vol_300_bar'))
    put('dimb1200', s * g('delta_1200_bar') / g('vol_1200_bar'))
    put('gap60', g('gap_60')); put('hour', (g('sig_t') // 3600).astype(np.float64))
    put('vt', v * np.abs(g('entry_px')) / g('tick'))
NET = (1e4 * s * (g('exit_px') - g('entry_px')) / np.abs(g('entry_px')) - 1e4 * g('tick') / np.abs(g('entry_px')))
# vrel: volat_20m vs the root's median over its PREVIOUS 20 sessions (causal)
M = pd.DataFrame({'root': root, 'td': td, 'v': v})
med = M.groupby(['root', 'td']).v.median().rename('m').reset_index().sort_values(['root', 'td'])
med['trail'] = med.groupby('root').m.transform(lambda x: x.shift(1).rolling(20, min_periods=10).median())
trail = M.merge(med[['root', 'td', 'trail']], on=['root', 'td'], how='left').trail.values
put('vrel', v / trail)
SIG0, EXT0, SIDE0 = g('sig_t').astype(np.int64), g('exit_t').astype(np.int64), g('side')
del C, tbl, M, med, trail, lnv, m1, m2
FIT = (td <= pd.Timestamp(a.fit_end))
rc = pd.Categorical(root).codes
order = np.lexsort((SIG0, SIDE0, td.values.astype('int64'), rc))
NET, FIT, root, td = NET[order], np.asarray(FIT)[order], root[order], td[order]
for k_ in F:
    F[k_] = F[k_][order]
SIG, EXT = SIG0[order], EXT0[order]
day = pd.factorize(td)[0].astype(np.int64)          # ⚠ not epoch arithmetic: pyarrow dates arrive in s, not ns
key = (rc[order].astype(np.int64) * 100_000 + day) * 2 + (SIDE0[order] > 0)
grp = np.r_[0, np.cumsum(key[1:] != key[:-1])].astype(np.int64)
log('features built')


@njit(cache=True)
def replay(grp, sig, ext, mask):
    """greedy mc = 1: within a group, take a masked trip if its signal is at or after the last taken exit"""
    take = np.zeros(len(grp), np.bool_)
    g0, last = -1, -1
    for i in range(len(grp)):
        if grp[i] != g0:
            g0, last = grp[i], -1
        if mask[i] and sig[i] >= last:
            take[i] = True
            last = ext[i]
    return take


def stats(take, part):
    r = NET[take & part]
    if len(r) == 0:
        return 0, 0.0, np.nan
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return len(r), r.sum(), (np.inf if l == 0 else g / l)


QS = [0.05, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95]
CANDS = []
for name, x in F.items():
    q = np.nanquantile(x[FIT], QS)
    for qq, t in zip(QS, q):
        CANDS.append((name, '>=', qq, t))
        CANDS.append((name, '<=', qq, t))
log(f'{len(CANDS)} candidate gates')

mask = np.ones(N, np.bool_)
take = replay(grp, SIG, EXT, mask)
nF, netF, pfF = stats(take, FIT); nH, netH, pfH = stats(take, ~FIT)
print(f'# PIT-5 — greedy gate search, vol-normalised thresholds ({a.tag}: {len(set(root))} roots)\n')
print(f'mc = 1 by greedy replay of {N:,} mc = 0 trips; net = Σ bp (equal notional) − 1 tick; fit ≤ {a.fit_end}, holdout after. '
      'Each step adds the gate that raises FIT net most. Thresholds are fit-period quantiles of the vol-normalised feature.\n')
print('| step | gate | threshold (fit quantile) | fit trips | fit net bp | fit PF | Δ fit net | holdout trips | holdout net bp | holdout PF |')
print('|---|---|---|---|---|---|---|---|---|---|')
print(f'| 0 | (none: the bare book) | | {nF:,} | {netF:,.0f} | {pfF:.3f} | | {nH:,} | {netH:,.0f} | {pfH:.3f} |')
chosen = []
for step in range(1, a.max_steps + 1):
    best = None
    for name, op, qq, t in CANDS:
        if any(c[0] == name for c in chosen):
            continue
        mm = mask & ((F[name] >= t) if op == '>=' else (F[name] <= t))
        tk = replay(grp, SIG, EXT, mm)
        n, net, pf = stats(tk, FIT)
        if best is None or net > best[0]:
            best = (net, name, op, qq, t, mm, tk, n, pf)
    net, name, op, qq, t, mm, tk, n, pf = best
    if net <= netF:
        log(f'step {step}: no gate adds fit net (best {name} {op} {t:.4g}: {net:,.0f} vs {netF:,.0f})')
        break
    nH, netH, pfH = stats(tk, ~FIT)
    print(f'| {step} | **{name} {op} {t:.4g}** | p{int(100 * qq)} | {n:,} | {net:,.0f} | {pf:.3f} | {net - netF:+,.0f} | {nH:,} | {netH:,.0f} | {pfH:.3f} |', flush=True)
    log(f'step {step}: {name} {op} {t:.4g} fit net {net:,.0f} PF {pf:.3f} | holdout {netH:,.0f} PF {pfH:.3f}')
    chosen.append((name, op, t)); mask, netF = mm, net

print('\n## The final book by root (fit / holdout: trips · net bp · PF)\n')
print('| root | fit | holdout |\n|---|---|---|')
take = replay(grp, SIG, EXT, mask)
for r in sorted(set(root)):
    rr = root == r
    n1, t1, p1 = stats(take & rr, FIT); n2, t2, p2 = stats(take & rr, ~FIT)
    print(f'| {r} | {n1:,} · {t1:,.0f} · {p1:.2f} | {n2:,} · {t2:,.0f} · {p2:.2f} |')
yr = td.year.values
print('\n## The final book by year (trips · net bp · PF)\n')
print('| year | trips | net bp | PF |\n|---|---|---|---|')
for y in sorted(np.unique(yr)):
    n, t, p = stats(take, yr == y)
    print(f'| {y} | {n:,} | {t:,.0f} | {p:.3f} |')
log('done')
