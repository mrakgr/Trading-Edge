#!/usr/bin/env python
"""Checks on the zone result (escvd_zones.py): is the seller-heavy zone a real sorter? FIT only.

  cd research && python -u scripts/futures/escvd_zones_checks.py > logs/escvd_zones_checks.log
"""
import sys, numpy as np, polars as pl
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

Z = pl.read_parquet('data/futures/escvd/zones.parquet').filter(pl.col('fit')).with_columns(g=pl.col('session').cast(pl.Utf8))

def q5(x):
    q = np.quantile(x, [.2, .4, .6, .8]); return np.searchsorted(q, x, side='right')

print('=== f10 / f30 / f60 (bps) by quintile of zratio (share of the zone volume that hit the bid), each N and K ===')
for (N, K), d in sorted(Z.group_by('N', 'K'), key=lambda x: x[0]):
    b = q5(d['zratio'].to_numpy()); g = d['g'].to_numpy(); s = f'N {N:>2} K {K:>2} n {d.height:>6} '
    for c in ('f10', 'f30', 'f60'):
        r = [L.cluster_t(d[c].to_numpy()[b == k], g[b == k]) for k in range(5)]
        s += f'| {c} ' + ' '.join(f'{v[0]:+5.2f}' for v in r) + f' t(Q1) {r[0][1]:+4.1f} t(Q5) {r[4][1]:+4.1f} '
    print(s)

print('\n=== the same split by side (f30) ===')
for (N, K, side), d in sorted(Z.group_by('N', 'K', 'side'), key=lambda x: x[0]):
    b = q5(d['zratio'].to_numpy()); g = d['g'].to_numpy(); y = d['f30'].to_numpy()
    r = [L.cluster_t(y[b == k], g[b == k]) for k in range(5)]
    print(f'N {N:>2} K {K:>2} {side} ' + ' '.join(f'{v[0]:+5.2f}' for v in r) + f'  t(Q1) {r[0][1]:+4.1f} t(Q5) {r[4][1]:+4.1f}')

print('\n=== by year: zratio top quintile minus bottom quintile, f30 (bps); and the top quintile alone ===')
for (N, K), d in sorted(Z.group_by('N', 'K'), key=lambda x: x[0]):
    b = q5(d['zratio'].to_numpy()); y = d['f30'].to_numpy(); yr = d['g'].str.slice(0, 4).to_numpy()
    s = f'N {N:>2} K {K:>2} '
    for Y in sorted(set(yr)):
        s += f'{Y} {np.nanmean(y[(b == 4) & (yr == Y)]) - np.nanmean(y[(b == 0) & (yr == Y)]):+5.2f} (top {np.nanmean(y[(b == 4) & (yr == Y)]):+5.2f})  '
    print(s)

def ols(d, y, cols):
    X = np.column_stack([d[c].to_numpy().astype(float) for c in cols]); yv = d[y].to_numpy()
    ok = np.isfinite(X).all(1) & np.isfinite(yv); X = X[ok]; yv = yv[ok]; g = d['g'].to_numpy()[ok]
    X = (X - X.mean(0)) / X.std(0); X = np.column_stack([np.ones(len(X)), X])
    xtx = np.linalg.inv(X.T @ X); b = xtx @ X.T @ yv; u = yv - X @ b
    _, inv = np.unique(g, return_inverse=True); S = np.zeros((inv.max() + 1, X.shape[1]))
    np.add.at(S, inv, X * u[:, None]); V = xtx @ (S.T @ S) @ xtx
    return b[1:], b[1:] / np.sqrt(np.diag(V))[1:], len(yv)

print('\n=== does the per-price zone add to the bar-level delta of the same 3 bars? bps per 1 sd, session-clustered t ===')
cols = ['zratio', 'wdz', 'bigr', 'share', 'lag', 'volr', 'brk', 'dist3', 'day_ret']
print(' ' * 22 + ' '.join(f'{c:>13}' for c in cols))
for (N, K), d in sorted(Z.group_by('N', 'K'), key=lambda x: x[0]):
    for y in ('f10', 'f30', 'f60'):
        b, t, nn = ols(d, y, cols)
        print(f'N {N:>2} K {K:>2} {y:<4} n {nn:>6} ' + ' '.join(f'{x:+5.2f} ({tt:+4.1f})' for x, tt in zip(b, t)))

print('\n=== the bias cells: trade side agrees with price vs the 3-day vwap and the 5-day trend; zratio top quintile ===')
for (N, K), d in sorted(Z.group_by('N', 'K'), key=lambda x: x[0]):
    zr = d['zratio'].quantile(0.8); s = f'N {N:>2} K {K:>2} '
    for lab, f in (('bias', (pl.col('dist3') > 0) & (pl.col('ret5') > 0)), ('bias+zr', (pl.col('dist3') > 0) & (pl.col('ret5') > 0) & (pl.col('zratio') >= zr)),
                   ('anti+zr', (pl.col('dist3') < 0) & (pl.col('ret5') < 0) & (pl.col('zratio') >= zr)),
                   ('open+bias+zr', (pl.col('minute') < 1050) & (pl.col('dist3') > 0) & (pl.col('ret5') > 0) & (pl.col('zratio') >= zr))):
        e = d.filter(f); mu, t, k = L.cluster_t(e['f30'].to_numpy(), e['g'].to_numpy()); mu6, t6, _ = L.cluster_t(e['f60'].to_numpy(), e['g'].to_numpy())
        s += f'| {lab} n {k:>5} f30 {mu:+5.2f} ({t:+4.1f}) f60 {mu6:+5.2f} ({t6:+4.1f}) '
    print(s)
