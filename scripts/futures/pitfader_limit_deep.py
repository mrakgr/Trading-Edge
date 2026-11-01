"""PitFader PIT-10 — is the mc = 0 vs mc = 1 gap an ENTRY-PRICE gap, and would a 1–2-tick better (limit) entry close it?
(user 2026-11-01: "I wonder if the huge difference between mc = 0 and mc = 1 for bonds is due to how thick the order book
is? Maybe getting 1 or 2 ticks better entry would close the gap between mc = 0 and mc = 1?")

1. THE DECOMPOSITION: in an mc = 0 episode (the gated trips of one root-session-side that exit together) every later
   trip is a fresh 20m extreme, so it enters better than the episode's first trip. Per group: the later trips' entry
   improvement over the first, in ticks; and the first trip alone vs the whole episode.
2. A LIMIT ENTRY for the mc = 1 trip: a bid (offer, short) k = 1, 2, 3 ticks better than the SIGNAL bar's vwap, resting
   until the trip's exit. The path's extreme until the exit = the min (max, short) of the signal vwaps of every later
   trip of the same root-session-side signalled before that exit (each fresh 20m extreme IS a recorded signal; the
   gate-free sampler has them all — only the bars after the 15:00 entry cutoff are not seen). Fill rules: TOUCH = the
   path's 1s vwap reaches the limit; THROUGH = it goes ≥ 1 tick past it (a thick book: touching does not fill the back
   of the queue). A fill at the limit price; a miss = no trade (0). The signal vwap = sess_vwap · exp(dist_sess_vwap).
Configurations: the PIT-9 cells per group; mc = 1 greedy replay on the market fill; raw = next-bar vwap; −1 tick stress
(a resting limit pays no spread — the stress is shown for comparison only).

  cd research && python -u scripts/futures/pitfader_limit_deep.py > data/futures/pitfader/limit_deep.md 2> logs/pitfader_limit_deep.log; echo exit $?
(the deeper-limit variant of pitfader_limit.py: limits 3, 5, 8, 12 ticks)
"""
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
GROUPS = {'bonds': (['ZB', 'ZN', 'ZT'], [(25, 300), (25, 600), (52, 600)]),
          'equity': (['ES', 'MES', 'NQ'], [(75, 300), (100, 300), (75, 600)]),
          'fx': (['6A', '6B', '6C', '6E', '6J', '6N'], [(25, 300), (52, 300)])}
cols = ['root', 'td', 'side', 'sig_t', 'entry_px', 'tick', 'volat_20m', 'k_300', 'exit_t', 'exit_px', 'exit_t_600', 'exit_px_600',
        'sess_vwap', 'dist_sess_vwap']


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


@njit(cache=True)
def path_extreme(grp, sig, ext, sv, side):
    """for each trip: the best (min for a long, max for a short) signal vwap of the LATER trips of its group signalled
    before its exit; nan if none (all arrays sorted by group, then sig)"""
    n = len(grp); out = np.full(n, np.nan)
    for i in range(n):
        best = np.nan
        j = i + 1
        while j < n and grp[j] == grp[i] and sig[j] < ext[i]:
            x = sv[j]
            if np.isnan(best) or (side[i] > 0 and x < best) or (side[i] < 0 and x > best):
                best = x
            j += 1
        out[i] = best
    return out


def pf(r):
    r = r[np.isfinite(r)]
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else (g / l if g > 0 else 0.0)


def f(x): return 'inf' if np.isinf(x) else f'{x:.2f}'


print('# PIT-10 — the entry-price gap between mc = 0 and mc = 1, and a resting limit 1–3 ticks better\n')
print('RAW = next-bar vwap (the market fill), raw bp at equal notional; FIT 2015–22 / HOLDOUT 2023–26. '
      'A limit fill is at the limit price; a miss is no trade.\n')
for gname, (roots, cfgs) in GROUPS.items():
    t = ds.dataset('data/futures/pitfader/trips_mc0_aux').to_table(columns=cols, filter=(ds.field('volat_20m') > 0) & ds.field('root').isin(roots))
    c = lambda n: t[n].to_numpy(zero_copy_only=False)
    root, td = c('root'), pd.to_datetime(c('td'))
    rc = pd.Categorical(root).codes.astype(np.int64)
    day = pd.factorize(td)[0].astype(np.int64)
    s = c('side').astype(np.float64)
    order = np.lexsort((c('sig_t'), s, td.values.astype('int64'), rc))
    def o(x): return x[order]
    root, td, s, rc, day = o(root), td[order], o(s), o(rc), o(day)
    sig, epx, tick, k3 = o(c('sig_t')).astype(np.int64), o(c('entry_px')), o(c('tick')), o(c('k_300'))
    sv = o(c('sess_vwap')) * np.exp(o(c('dist_sess_vwap')))
    EX = {300: (o(c('exit_t')).astype(np.int64), o(c('exit_px'))), 600: (o(c('exit_t_600')).astype(np.int64), o(c('exit_px_600')))}
    key = (rc * 100_000 + day) * 2 + (s > 0)
    grp = np.r_[0, np.cumsum(key[1:] != key[:-1])].astype(np.int64)
    FIT = np.asarray(td <= pd.Timestamp('2022-12-31'))
    ok = np.abs(epx) > 0
    log(f'{gname}: {len(grp):,} trips')
    print(f'## {gname} ({", ".join(roots)}); median tick {np.median(1e4 * tick / np.abs(epx)):.2f} bp\n')
    print('| config | part | mc 0 PF (trips, ep) | first trip only PF | later trips: mean entry gain vs the first (ticks) | '
          'mc 1 market PF (trips) | −3t touch | −5t touch | −8t touch | −12t touch | −5t THROUGH | −8t THROUGH |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|')
    for K, n in cfgs:
        ext, xpx = EX[n]
        raw = 1e4 * s * (xpx - epx) / np.abs(epx)
        gate = ok & (k3 >= K)
        epi = pd.factorize(pd.Series(key * 100_000 + ext))[0]
        # the first gated trip of each episode, and the later ones' entry gain over it
        first = np.zeros(len(gate), np.bool_)
        gain = np.full(len(gate), np.nan)
        dfx = pd.DataFrame({'e': epi[gate], 'i': np.flatnonzero(gate)})
        fi = dfx.groupby('e').i.min().values
        first[fi] = True
        fpx = pd.Series(epx[fi], index=epi[fi])
        gi = np.flatnonzero(gate & ~first)
        gain[gi] = s[gi] * (fpx.loc[epi[gi]].values - epx[gi]) / tick[gi]
        take = replay(grp, sig, ext, gate)
        pe = path_extreme(grp, sig, ext, sv, s)
        for part, pm in (('fit', FIT), ('holdout', ~FIT)):
            g0 = gate & pm; t1 = take & pm
            cells = [f'k300 ≥ {K} · exit {n}', part,
                     f'{f(pf(raw[g0]))} ({g0.sum():,}, {len(np.unique(epi[g0])):,})', f'{f(pf(raw[first & pm]))}',
                     f'{np.nanmean(gain[g0 & ~first]):+.2f} (median {np.nanmedian(gain[g0 & ~first]):+.1f})',
                     f'{f(pf(raw[t1]))} ({t1.sum():,})']
            for k, thr in ((3, 0), (5, 0), (8, 0), (12, 0), (5, 1), (8, 1)):
                lim = sv - s * k * tick                                  # the resting price
                hit_bar = s * (lim - epx) >= thr * tick                  # the next bar (the market fill bar) already reached it
                hit_path = s * (lim - pe) >= thr * tick                  # a later fresh extreme reached it (+ thr ticks past)
                fill = t1 & (hit_path | hit_bar)
                rl = 1e4 * s * (xpx - lim) / np.abs(lim)
                miss = t1 & ~fill
                cells.append(f'{100 * fill.sum() / max(1, t1.sum()):.0f} % · **{f(pf(rl[fill]))}** ({f(pf(raw[miss]))})')
            print('| ' + ' | '.join(cells) + ' |')
    print()
    del t
log('done')
