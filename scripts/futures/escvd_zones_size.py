#!/usr/bin/env python
"""Zone triggers: the delta of the 3 bars into the trough, split by the SIZE of the aggressor's burst. FIT only.

d0..d3 = (volume toward the trade - volume against it) of bursts of <5, 5-19, 20-99, 100+ contracts, over the 3 bars
ending at the trough bar, / sqrt(kd * window volume). For a long: + = buying.

  cd research && python -u scripts/futures/escvd_zones_size.py > logs/escvd_zones_size.log
"""
import sys, numpy as np, polars as pl
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

Z = pl.read_parquet('data/futures/escvd/zones.parquet').filter(pl.col('fit')).with_columns(g=pl.col('session').cast(pl.Utf8))
Z = Z.with_columns(dsm=pl.col('d0') + pl.col('d1'), dbig=pl.col('d2') + pl.col('d3'))
Z = Z.with_columns(sig=pl.col('dsm') - pl.col('dbig'))

def ols(d, y, cols):
    X = np.column_stack([d[c].to_numpy().astype(float) for c in cols]); yv = d[y].to_numpy()
    ok = np.isfinite(X).all(1) & np.isfinite(yv); X = X[ok]; yv = yv[ok]; g = d['g'].to_numpy()[ok]
    X = (X - X.mean(0)) / X.std(0); X = np.column_stack([np.ones(len(X)), X])
    xtx = np.linalg.inv(X.T @ X); b = xtx @ X.T @ yv; u = yv - X @ b
    _, inv = np.unique(g, return_inverse=True); S = np.zeros((inv.max() + 1, X.shape[1]))
    np.add.at(S, inv, X * u[:, None]); V = xtx @ (S.T @ S) @ xtx
    return b[1:], b[1:] / np.sqrt(np.diag(V))[1:], len(yv)

def q5(x):
    q = np.quantile(x, [.2, .4, .6, .8]); return np.searchsorted(q, x, side='right')

d = Z.filter((pl.col('N') == 20) & (pl.col('K') == 4))
print('volume share by burst size (N 20 K 4): ' + ' '.join(f'v{k} {d[f"v{k}"].mean():.2f}' for k in range(4)))
print('correlations of d0..d3: '); print(np.round(np.corrcoef(np.column_stack([d[f'd{k}'].to_numpy() for k in range(4)]).T), 2))

print('\n=== f ~ d0 + d1 + d2 + d3 (bps per 1 sd, session-clustered t) ===')
cols = ['d0', 'd1', 'd2', 'd3']
for (N, K), d in sorted(Z.group_by('N', 'K'), key=lambda x: x[0]):
    if K not in (4, 8): continue
    for y in ('f10', 'f30', 'f60'):
        b, t, nn = ols(d, y, cols)
        print(f'N {N:>2} K {K:>2} {y:<4} n {nn:>6} ' + ' '.join(f'{c} {x:+5.2f} ({tt:+4.1f})' for c, x, tt in zip(cols, b, t)))

print('\n=== univariate quintiles, f30 and f60: small (<20) delta, big (>=20) delta, sig = small - big ===')
for (N, K), d in sorted(Z.group_by('N', 'K'), key=lambda x: x[0]):
    if K not in (4, 8): continue
    g = d['g'].to_numpy()
    for f in ('dsm', 'dbig', 'sig', 'd0', 'd3'):
        b = q5(d[f].to_numpy()); s = f'N {N:>2} K {K:>2} {f:<5}'
        for c in ('f30', 'f60'):
            r = [L.cluster_t(d[c].to_numpy()[b == k], g[b == k]) for k in range(5)]
            s += f' | {c} ' + ' '.join(f'{v[0]:+5.2f}' for v in r) + f' t {r[0][1]:+4.1f}/{r[4][1]:+4.1f}'
        r = [L.cluster_t(d['b1_R'].to_numpy()[b == k], g[b == k]) for k in range(5)]
        s += ' | 1R ' + ' '.join(f'{v[0]:+.3f}' for v in r)
        print(s)

print('\n=== sig top quintile: by side, by year, by time of day (f30 / f60, t) ===')
for (N, K), d in sorted(Z.group_by('N', 'K'), key=lambda x: x[0]):
    if K not in (4, 8): continue
    top = d.filter(pl.col('sig') >= d['sig'].quantile(0.8)); bot = d.filter(pl.col('sig') <= d['sig'].quantile(0.2))
    def cell(e):
        a, ta, k = L.cluster_t(e['f30'].to_numpy(), e['g'].to_numpy()); b, tb, _ = L.cluster_t(e['f60'].to_numpy(), e['g'].to_numpy())
        return f'n {k:>5} {a:+5.2f} ({ta:+4.1f}) / {b:+5.2f} ({tb:+4.1f})'
    print(f'--- N {N} K {K}:  top {cell(top)}   bottom {cell(bot)}')
    for side in 'LS': print(f'   {side}: top {cell(top.filter(pl.col("side") == side))}   bottom {cell(bot.filter(pl.col("side") == side))}')
    for Y in ('2020', '2021', '2022', '2023'):
        print(f'   {Y}: top {cell(top.filter(pl.col("g").str.starts_with(Y)))}   bottom {cell(bot.filter(pl.col("g").str.starts_with(Y)))}')
    for lab, lo, hi in (('09:30-10:30', 930, 990), ('10:30-12:00', 990, 1080), ('12:00-15:00', 1080, 1260)):
        f = (pl.col('minute') >= lo) & (pl.col('minute') < hi)
        print(f'   {lab}: top {cell(top.filter(f))}   bottom {cell(bot.filter(f))}')
