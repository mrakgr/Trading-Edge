"""PitFader PIT-6 — the LONGER TARGET (user 2026-11-01: "Ok, let's try a longer target"; and "Are we not testing raw
signals here?" — yes: the fills are the next 1s bar's vwap, no spread model, so the RAW (gross) numbers lead and the
−1-tick net is a stress line only).

The book: the mc = 0 sampler with AUX EXITS (data/futures/pitfader/trips_mc0_aux): every trip records, per exit channel
N ∈ {300 (the primary), 540, 600, 900, 1200, 1800, 2400, 3600} present bars, the exit at the first new N-bar opposite
extreme at or after its fill bar (filled at the next bar's vwap), or the session's last bar (eod_N). The signals and
their features do not depend on the exit, so one run carries every target.
Controls: the primary columns == the PIT-4 run (trips_mc0); every aux exit at or after the 300-bar one.
Then per target: the mc = 0 sampler pooled, the depth × volatility cells (PIT-3), the mc = 1 book by greedy replay
(per root), and the PIT-5 pooled gates (k300 ≥ 52, vrel ≥ 0.91, cslu ≥ −0.30, dimb30 ≤ −0.084) fit / holdout.

  cd research && python -u scripts/futures/pitfader_exits.py > data/futures/pitfader/exits.md 2> logs/pitfader_exits.log; echo exit $?
"""
import sys
import time

import numpy as np
import pandas as pd
import pyarrow.dataset as ds
from numba import njit

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)
X = [300, 540, 600, 900, 1200, 1800, 2400, 3600]
AUX = X[1:]
SRC = 'data/futures/pitfader/trips_mc0_aux'

# ---- control 1: the primary columns equal the PIT-4 run
K = ['root', 'td', 'side', 'sig_t', 'entry_t', 'entry_px', 'exit_t', 'exit_px', 'eod', 'open_at_sig']
a = ds.dataset(SRC).to_table(columns=K).to_pandas().sort_values(K[:4]).reset_index(drop=True)
b = ds.dataset('data/futures/pitfader/trips_mc0').to_table(columns=K).to_pandas().sort_values(K[:4]).reset_index(drop=True)
same = len(a) == len(b) and a.equals(b)
log(f'control 1 (primary == PIT-4 run): {same} ({len(a):,} vs {len(b):,})')
del a, b

cols = ['root', 'td', 'side', 'sig_t', 'exit_t', 'entry_px', 'exit_px', 'eod', 'tick', 'open_at_sig', 'volat_20m', 'k_300',
        'chg_since_ct', 'delta_30_bar', 'vol_30_bar'] + [f'{p}_{n}' for n in AUX for p in ('exit_t', 'exit_px', 'eod')]
tbl = ds.dataset(SRC).to_table(columns=cols, filter=ds.field('volat_20m') > 0)
def c(n): return tbl[n].to_numpy(zero_copy_only=False)
root, td = c('root'), pd.to_datetime(c('td'))
side, epx, tick, v = c('side').astype(np.float64), c('entry_px'), c('tick'), c('volat_20m')
ok = np.abs(epx) > 0
ET = {300: c('exit_t')}; EP = {300: c('exit_px')}; EOD = {300: c('eod')}
for n in AUX:
    ET[n], EP[n], EOD[n] = c(f'exit_t_{n}'), c(f'exit_px_{n}'), c(f'eod_{n}')
bad = sum(int((ET[n][ok] < ET[300][ok]).sum()) for n in AUX)
log(f'control 2 (every aux exit at or after the 300-bar one): {bad} violations')
SIG = c('sig_t').astype(np.int64)
GROSS = {n: 1e4 * side * (EP[n] - epx) / np.abs(epx) for n in X}            # bp, equal notional
TBP = 1e4 * tick / np.abs(epx)
TICKS = {n: side * (EP[n] - epx) / tick for n in X}
HOLD = {n: (ET[n] - SIG) / 60 for n in X}                                    # minutes from the signal
DEPTH, K300 = c('open_at_sig'), c('k_300')
# PIT-3 / PIT-5 inputs
M = pd.DataFrame({'root': root, 'td': td, 'v': v})
dec = M.groupby('root').v.transform(lambda x: np.ceil(10 * x.rank(pct=True)).clip(1, 10)).values.astype(int)
med = M.groupby(['root', 'td']).v.median().rename('m').reset_index().sort_values(['root', 'td'])
med['trail'] = med.groupby('root').m.transform(lambda x: x.shift(1).rolling(20, min_periods=10).median())
vrel = v / M.merge(med[['root', 'td', 'trail']], on=['root', 'td'], how='left').trail.values
with np.errstate(all='ignore'):
    cslu = side * c('chg_since_ct') / v
    dimb30 = side * c('delta_30_bar') / c('vol_30_bar')
del M, med
FIT = (td <= pd.Timestamp('2022-12-31'))
log(f'{int(ok.sum()):,} trips loaded')


def pf(r):
    r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l


def f(x, d=3): return 'inf' if np.isinf(x) else ('nan' if np.isnan(x) else f'{x:.{d}f}')


# ---- the mc = 1 replay (sorted once; each target uses its own exit times)
rc = pd.Categorical(root).codes
order = np.lexsort((SIG, side, td.values.astype('int64'), rc))
day = pd.factorize(td)[0].astype(np.int64)
key = (rc.astype(np.int64) * 100_000 + day) * 2 + (side > 0)
key_o = key[order]
grp = np.r_[0, np.cumsum(key_o[1:] != key_o[:-1])].astype(np.int64)


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


def book(n, mask):
    """the mc = 1 book on exit n: a boolean over the ORIGINAL row order"""
    t = replay(grp, SIG[order], ET[n][order].astype(np.int64), mask[order])
    out = np.zeros(len(t), np.bool_); out[order] = t
    return out


print('# PIT-6 — the longer target: the exit sweep (raw = next-bar vwap, no spread model; −1 tick = the stress line)\n')
print(f'Controls: the primary exit columns == the PIT-4 run: **{same}**; aux exits earlier than the 300-bar exit: **{bad}**.\n')
print('## 1. The mc = 0 sampler, pooled (bp at equal notional)\n')
print('| exit (bars) | win % | mean ticks | **PF raw** | mean bp raw | PF −1 tick | hold min (median) | eod % |\n|---|---|---|---|---|---|---|---|')
for n in X:
    g, t = GROSS[n][ok], TICKS[n][ok]
    print(f'| {n} | {100 * (g > 0).mean():.0f} | {t.mean():+.2f} | **{f(pf(g))}** | {g.mean():+.2f} | {f(pf(g - TBP[ok]))} | '
          f'{np.median(HOLD[n][ok]):.1f} | {100 * EOD[n][ok].mean():.1f} |')

print('\n## 2. Depth × volatility (PIT-3 cells): one trip per episode (exactly k open), raw PF (mean ticks)\n')
cells = [('D1–5', dec <= 5), ('D10', dec == 10)]
print('| exit | ' + ' | '.join(f'{nm} k = {k}' for nm, _ in cells for k in (0, 25, 50, 100)) + ' |\n|---|' + '---|' * 8)
for n in X:
    row = []
    for nm, cm in cells:
        for k in (0, 25, 50, 100):
            s = ok & cm & (DEPTH == k)
            row.append(f'{f(pf(GROSS[n][s]), 2)} ({TICKS[n][s].mean():+.1f})')
    print(f'| {n} | ' + ' | '.join(row) + ' |')

print('\n## 3. The mc = 1 book by greedy replay, pooled: raw PF (net PF −1 tick), trips\n')
print('| exit | trips | win % | mean ticks | **PF raw** | PF −1 tick | fit raw / holdout raw |\n|---|---|---|---|---|---|---|')
B = {}
for n in X:
    B[n] = book(n, ok)
    g = GROSS[n][B[n]]
    print(f'| {n} | {B[n].sum():,} | {100 * (g > 0).mean():.0f} | {TICKS[n][B[n]].mean():+.2f} | **{f(pf(g))}** | '
          f'{f(pf(g - TBP[B[n]]))} | {f(pf(GROSS[n][B[n] & FIT]))} / {f(pf(GROSS[n][B[n] & ~FIT]))} |')

print('\n## 4. The mc = 1 book per root: raw PF by exit\n')
print('| root | ' + ' | '.join(str(n) for n in X) + ' | best |\n|---|' + '---|' * (len(X) + 1))
for r in sorted(set(root)):
    rr = root == r
    ps = [pf(GROSS[n][B[n] & rr]) for n in X]
    print(f'| {r} | ' + ' | '.join(f(p, 2) for p in ps) + f' | {X[int(np.nanargmax(ps))]} |')

print('\n## 5. The PIT-5 pooled gates (k300 ≥ 52 · vrel ≥ 0.91 · cslu ≥ −0.30 · dimb30 ≤ −0.084) under each exit, mc = 1\n')
G = ok & (K300 >= 52) & (vrel >= 0.9083) & (cslu >= -0.299) & (dimb30 <= -0.0839)
print('| exit | trips fit / holdout | **raw PF fit / holdout** | −1 tick PF fit / holdout | mean ticks fit / holdout |\n|---|---|---|---|---|')
for n in X:
    bk = book(n, G)
    fi, ho = bk & FIT, bk & ~FIT
    print(f'| {n} | {fi.sum():,} / {ho.sum():,} | **{f(pf(GROSS[n][fi]))} / {f(pf(GROSS[n][ho]))}** | '
          f'{f(pf(GROSS[n][fi] - TBP[fi]))} / {f(pf(GROSS[n][ho] - TBP[ho]))} | {TICKS[n][fi].mean():+.2f} / {TICKS[n][ho].mean():+.2f} |')
log('done')

# ---- 6. the PIT-7 selective core under each exit: k300 ≥ K + the per-root ABSOLUTE volat floor, re-chosen on FIT for
# each exit (max Σ (raw − 1 tick), ≥ 30 fit episodes; a root with fewer stays unfloored)
VBP = 1e4 * v
key_u = key
print('\n## 6. The PIT-7 selective core (k300 ≥ K + the per-root absolute volat floor, chosen on FIT per exit): holdout\n')
print('| exit | K | mc = 0 fit raw | **mc = 0 holdout raw / −1t** (trips, episodes) | mc = 1 fit raw | **mc = 1 holdout raw / −1t** (episodes) | mean ticks mc = 1 holdout |')
print('|---|---|---|---|---|---|---|')
for n in X:
    EPn = pd.factorize(pd.Series(key_u * 100_000 + ET[n]))[0]
    for KB in (75, 100):
        base = ok & (K300 >= KB)
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
                sc = (GROSS[n][m] - TBP[m]).sum()
                if best is None or sc > best[0]:
                    best = (sc, th)
            FL |= rr & (VBP >= best[1])
        b1 = book(n, FL)
        h0, h1 = FL & ~FIT, b1 & ~FIT
        print(f'| {n} | {KB} | {f(pf(GROSS[n][FL & FIT]))} | **{f(pf(GROSS[n][h0]))} / {f(pf(GROSS[n][h0] - TBP[h0]))}** '
              f'({h0.sum():,}, {len(np.unique(EPn[h0])):,}) | {f(pf(GROSS[n][b1 & FIT]))} | **{f(pf(GROSS[n][h1]))} / '
              f'{f(pf(GROSS[n][h1] - TBP[h1]))}** ({h1.sum():,}) | {TICKS[n][h1].mean():+.2f} |')
log('done 6')
