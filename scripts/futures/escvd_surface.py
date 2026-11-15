#!/usr/bin/env python
"""ES CVD study, bar level: what do delta and price over the last W minutes say about the next H?

  cd research && python -u scripts/futures/escvd_surface.py > logs/escvd_surface.log 2>&1

Every bar is a sample (far more power than the rare flip events); the t-values are clustered by
session because neighbouring bars share their forward path. FIT sample only unless --sample hold.

  dz_W  = (delta summed over W minutes) / sqrt(kd * volume over W)   kd = prior-20-session sum(delta^2)/sum(volume)
  rz_W  = (price change over W minutes) / sqrt(kp * volume over W)   kp = prior-20-session sum(dclose^2)/sum(volume)
  dist_k = (close - VWAP over k sessions) / a day's price sigma
"""
import sys, argparse
import numpy as np
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

ap = argparse.ArgumentParser()
ap.add_argument('--sample', default='fit', choices=['fit', 'hold'])
ap.add_argument('--m0', type=int, default=930); ap.add_argument('--m1', type=int, default=1260)
a = ap.parse_args()

D = L.load(); n = D['n']; M = L.M
F = L.forward(D)
W = (5, 15, 30, 60, 120)

def prior(x, k=20):
    cs = np.concatenate([[0.0], np.cumsum(x)]); out = np.full(len(x), np.nan); out[k:] = cs[k:-1] - cs[:-k - 1]
    return out
ns = D['ns']
dc = np.diff(D['c'], prepend=D['o'][0])
vs = prior(D['volume'].reshape(ns, M).sum(1))
kd = np.repeat(prior((D['delta'] ** 2).reshape(ns, M).sum(1)) / vs, M)
kp = np.repeat(prior((dc ** 2).reshape(ns, M).sum(1)) / vs, M)

csd = np.concatenate([[0.0], np.cumsum(D['delta'])]); csv = np.concatenate([[0.0], np.cumsum(D['volume'])])
i = np.arange(n) + 1
X = {}
for w in W:
    j = np.maximum(i - w, 0)
    vol = np.maximum(csv[i] - csv[j], 1.0)
    X[f'dz{w}'] = (csd[i] - csd[j]) / np.sqrt(kd * vol)
    pc = np.full(n, np.nan); pc[w:] = D['c'][w:] - D['c'][:-w]
    X[f'rz{w}'] = pc / np.sqrt(kp * vol)
for k in (1, 3):
    X[f'dist{k}'] = (D['c'] - L.rolling_vwap(D, k)) / D['sd_px']

sample = D['fit_bar'] if a.sample == 'fit' else (D['hold_bar'] & np.isfinite(D['sd_px']))
base = sample & (D['m'] >= a.m0) & (D['m'] < a.m1) & np.isfinite(F[60]) & np.isfinite(X['dist3']) & np.isfinite(X['rz120'])
si = D['si']
print(f'sample {a.sample}, entry minutes [{a.m0},{a.m1}) = {a.m0 // 60 - 6 if a.m0 >= 360 else a.m0 // 60 + 18}:{a.m0 % 60:02d} .. , bars {base.sum()}, sessions {len(np.unique(si[base]))}')
print(f'unconditional f30 {np.nanmean(F[30][base]):+.2f} bps, f60 {np.nanmean(F[60][base]):+.2f} bps; sd f60 {np.nanstd(F[60][base]):.1f} bps\n')

def ct(x, g):
    mu, t, k = L.cluster_t(x, g); return mu, t, k

# ---- 1. deciles ---------------------------------------------------------------------------
print('1. mean forward 60m return (bps) by decile of each feature; D1 = most negative. (t) under D1 and D10')
print(f'{"":<8}' + ''.join(f'{"D" + str(d + 1):>7}' for d in range(10)) + '    t(D1)  t(D10)   corr with f60')
for name in [f'dz{w}' for w in W] + [f'rz{w}' for w in W] + ['dist1', 'dist3']:
    x = X[name][base]; y = F[60][base]; g = si[base]
    q = np.quantile(x, np.linspace(0, 1, 11)); b = np.clip(np.searchsorted(q, x, side='right') - 1, 0, 9)
    mus = [y[b == d].mean() for d in range(10)]
    t1 = ct(y[b == 0], g[b == 0])[1]; t10 = ct(y[b == 9], g[b == 9])[1]
    print(f'{name:<8}' + ''.join(f'{m:+7.2f}' for m in mus) + f'   {t1:+6.1f}  {t10:+6.1f}   {np.corrcoef(x, y)[0, 1]:+.4f}')

# ---- 2. clustered regression ---------------------------------------------------------------
def ols(cols, h, mask):
    y = (F[h] / (D['sd_px'] / D['raw'] * 1e4))[mask]              # the forward return in day-sigmas
    Z = np.column_stack([np.ones(mask.sum())] + [(X[c][mask] - X[c][mask].mean()) / X[c][mask].std() for c in cols])
    XtX = np.linalg.inv(Z.T @ Z); b = XtX @ Z.T @ y; e = y - Z @ b
    _, inv = np.unique(si[mask], return_inverse=True)
    S = np.zeros((inv.max() + 1, Z.shape[1])); np.add.at(S, inv, Z * e[:, None])
    V = XtX @ (S.T @ S) @ XtX
    return b[1:], b[1:] / np.sqrt(np.diag(V))[1:]

print('\n2. regression of the forward return (in day-sigmas x 100) on standardized features; clustered t')
for cols in (['dist1', 'rz15', 'rz60', 'dz15', 'dz60'], ['dist3', 'rz5', 'rz30', 'rz120', 'dz5', 'dz30', 'dz120']):
    print('   ' + ''.join(f'{c:>16}' for c in cols))
    for h in (10, 30, 60):
        b, t = ols(cols, h, base)
        print(f'{h:>3}m' + ''.join(f'{x * 100:+8.2f} ({tt:+5.1f})' for x, tt in zip(b, t)))

# ---- 3. the pattern: stretched below VWAP, slow leg down, fast leg turned -------------------
print('\n3. the pattern. Bars below the 24h VWAP by >= s day-sigmas whose SLOW 60m leg is in its lowest decile,')
print('   split by the FAST 10m... (15m) leg. cvd: legs are dz; price twin: legs are rz. f30 / f60 in bps (t), n')
for s in (0.0, 0.5, 1.0):
    st = base & (X['dist1'] <= -s)
    print(f'-- dist1 <= -{s}: {st.sum()} bars; all of them: f30 {ct(F[30][st], si[st])[0]:+.2f} ({ct(F[30][st], si[st])[1]:+.1f})  f60 {ct(F[60][st], si[st])[0]:+.2f} ({ct(F[60][st], si[st])[1]:+.1f})')
    for kind in ('dz', 'rz'):
        slow, fast = X[f'{kind}60'], X[f'{kind}15']
        lo = np.quantile(slow[base], 0.10)
        arm = st & (slow <= lo)
        fq = np.quantile(fast[base], [0.2, 0.5, 0.8])
        cells = (('fast <= p20', arm & (fast <= fq[0])), ('p20..p50', arm & (fast > fq[0]) & (fast <= fq[1])),
                 ('p50..p80', arm & (fast > fq[1]) & (fast <= fq[2])), ('fast > p80', arm & (fast > fq[2])))
        m30, t30, k = ct(F[30][arm], si[arm]); m60, t60, _ = ct(F[60][arm], si[arm])
        print(f'   {kind}60 <= p10 ({lo:+.2f}): n {k:>6}  f30 {m30:+6.2f} ({t30:+5.1f})  f60 {m60:+6.2f} ({t60:+5.1f})')
        for lab, c in cells:
            m30, t30, k = ct(F[30][c], si[c]); m60, t60, _ = ct(F[60][c], si[c])
            print(f'      {kind}15 {lab:<12} n {k:>6}  f30 {m30:+6.2f} ({t30:+5.1f})  f60 {m60:+6.2f} ({t60:+5.1f})')
