#!/usr/bin/env python
"""Report on the trapped-seller zones (escvd_zones.py). FIT sample only; the holdout is not read.

  cd research && python -u scripts/futures/escvd_zones_report.py [--N 20 --K 8] > logs/escvd_zones_report.log
"""
import sys, argparse, numpy as np, polars as pl
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

ap = argparse.ArgumentParser(); ap.add_argument('--N', type=int, default=20); ap.add_argument('--K', type=int, default=8)
ap.add_argument('--m1', type=int, default=1260)
a = ap.parse_args()
Z = pl.read_parquet('data/futures/escvd/zones.parquet').filter(pl.col('fit') & (pl.col('minute') < a.m1))
Z = Z.with_columns(g=pl.col('session').cast(pl.Utf8))

def line(d, label):
    g = d['g'].to_numpy(); out = f'{label:<26} n {d.height:>6}'
    for c in ('f10', 'f30', 'f60'):
        mu, t, _ = L.cluster_t(d[c].to_numpy(), g); out += f'  {c} {mu:+6.2f} ({t:+4.1f})'
    mu, t, _ = L.cluster_t(d['b1_R'].to_numpy(), g); w = (d['b1_R'].drop_nans() > 0).mean()
    mu2, t2, _ = L.cluster_t(d['b2_R'].to_numpy(), g)
    out += f'  1R {mu:+.3f} ({t:+4.1f}) win {w:.2f}  2R {mu2:+.3f} ({t2:+4.1f})'
    print(out)

print('=== every trigger: the geometry alone (bps, session-clustered t; 1R/2R brackets in R) ===')
for (N, K, side), d in sorted(Z.group_by('N', 'K', 'side'), key=lambda x: x[0]):
    line(d, f'N {N} K {K} {side}  R {d["R_bps"].median():.1f}bps')

d0 = Z.filter((pl.col('N') == a.N) & (pl.col('K') == a.K))
print(f'\n=== N {a.N} K {a.K}, both sides pooled ({d0.height} triggers): outcome by quintile of each feature ===')
feats = ['zratio', 'zdz', 'share', 'wdz', 'nh', 'nd', 'stack', 'x0', 'y0', 'zvol', 'bigr', 'bigshare', 'sweepshare',
         'bdz', 'bzratio', 'bzvol', 'lag', 'brk', 'volr', 'dist1', 'dist3', 'day_ret', 'on_ret', 'ret1', 'ret5', 'ret20']
print(f'{"feature":<11} ' + ' '.join(f'{"Q" + str(k + 1):>7}' for k in range(5)) + '   | f30 by quintile' + ' ' * 22 + '| f60' + ' ' * 34 + '| 1R bracket')
for f in feats:
    x = d0[f].to_numpy().astype(float); ok = np.isfinite(x)
    q = np.quantile(x[ok], [.2, .4, .6, .8]); b = np.searchsorted(q, x, side='right')
    if len(np.unique(q)) < 4:                                    # a count: split at its distinct values instead
        u = np.unique(x[ok])[:5]; b = np.clip(np.searchsorted(u, x), 0, len(u) - 1)
    s = f'{f:<11} '; g = d0['g'].to_numpy(); parts = []
    cuts = ' '.join(f'{v:7.2f}' for v in ([x[ok & (b == k)].mean() if (ok & (b == k)).any() else np.nan for k in range(5)]))
    for c in ('f30', 'f60', 'b1_R'):
        y = d0[c].to_numpy(); r = []
        for k in range(5):
            mk = ok & (b == k)
            r.append(L.cluster_t(y[mk], g[mk]) if mk.sum() > 5 else (np.nan, np.nan, 0))
        fm = '{:+5.2f}' if c != 'b1_R' else '{:+5.3f}'
        parts.append(' '.join(fm.format(v[0]) for v in r) + f' t {r[0][1]:+4.1f}/{r[4][1]:+4.1f}')
    print(s + cuts + '   | ' + ' | '.join(parts))

print('\n=== the pattern as he describes it: with the bias, heavy one-sided zone ===')
for N, K in ((20, 4), (20, 8), (60, 4), (60, 8)):
    d = Z.filter((pl.col('N') == N) & (pl.col('K') == K))
    zr = d['zratio'].quantile(0.8); zz = d['zdz'].quantile(0.2)
    print(f'--- N {N} K {K}  (zratio top quintile >= {zr:.2f}; zone delta z bottom quintile <= {zz:.2f})')
    line(d, 'all')
    line(d.filter(pl.col('zratio') >= zr), 'zratio top q')
    line(d.filter(pl.col('zdz') <= zz), 'zone delta z bottom q')
    line(d.filter(pl.col('stack') >= 3), 'stack >= 3')
    line(d.filter(pl.col('dist3') > 0), 'bias: beyond 3d vwap')
    line(d.filter((pl.col('dist3') > 0) & (pl.col('ret5') > 0)), 'bias: 3d vwap & 5d trend')
    line(d.filter((pl.col('dist3') > 0) & (pl.col('ret5') > 0) & (pl.col('zdz') <= zz)), 'bias & zone delta bot q')
    line(d.filter((pl.col('dist3') > 0) & (pl.col('ret5') > 0) & (pl.col('zratio') >= zr)), 'bias & zratio top q')
    line(d.filter((pl.col('day_ret') > 0) & (pl.col('zdz') <= zz)), 'day up & zone delta bot q')
    line(d.filter((pl.col('minute') < 1050) & (pl.col('zdz') <= zz)), 'before 11:30 & zone bot q')
    line(d.filter((pl.col('minute') < 1050) & (pl.col('dist3') > 0) & (pl.col('ret5') > 0) & (pl.col('zdz') <= zz)), 'open & bias & zone bot q')
    for side in 'LS':
        line(d.filter((pl.col('side') == side) & (pl.col('zdz') <= zz)), f'{side} zone delta bot q')
