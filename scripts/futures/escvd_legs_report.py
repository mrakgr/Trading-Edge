#!/usr/bin/env python
"""ES CVD study: the leg events (escvd_legs.py), FIT sample only.

  cd research && python -u scripts/futures/escvd_legs_report.py [--m0 930 --m1 1260] [--first] > logs/escvd_legs_report.log

t-values are clustered by session. Returns are signed for the side (a short's f30 is minus the tape's).
"""
import sys, argparse, numpy as np, polars as pl
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L

ap = argparse.ArgumentParser()
ap.add_argument('--m0', type=int, default=930); ap.add_argument('--m1', type=int, default=1260)
ap.add_argument('--first', action='store_true', help="only a trigger's first fire in its leg")
ap.add_argument('--trigs', default='sv,p25,c25'); ap.add_argument('--N', type=int, default=20)
a = ap.parse_args()

E = pl.read_parquet('data/futures/escvd/legs.parquet').filter(pl.col('fit') & pl.col('minute').is_between(a.m0, a.m1 - 1) & pl.col('f60').is_not_null())
if a.first: E = E.filter(pl.col('nfire') == 1)
E = E.with_columns(sid=pl.col('session').rank('dense').cast(pl.Int64), year=pl.col('session').dt.year())
print(f'fit, entry minutes [{a.m0},{a.m1}), first-fire-only={a.first}: {E.height} events, {E["sid"].n_unique()} sessions\n')

def ct(df, col):
    return L.cluster_t(df[col].to_numpy().astype(float), df['sid'].to_numpy())

def cell(df, col):
    mu, t, n = ct(df, col)
    return f'{mu:+6.2f} ({t:+5.1f})'

print('1. every trigger. f = forward return in bps (clustered t); b1/b2 = the bracket (stop under the leg low, target 1R/2R, 60m cap) in R; R = the risk in bps')
print(f'{"N":>3} {"side":<6}{"trig":<5}{"n":>7} {"f10":>15} {"f30":>15} {"f60":>15} {"b1 R":>15} {"b2 R":>15} {"b1 bps":>8} {"win1":>6} {"R bps":>6} {"reb":>5} {"bars":>5}')
for N in (20, 60):
    for side in ('long', 'short'):
        for trig in ('sv', 'cx', 'p25', 'p50', 'c25', 'c50'):
            d = E.filter((pl.col('N') == N) & (pl.col('side') == side) & (pl.col('trig') == trig))
            if d.height < 30: continue
            print(f'{N:>3} {side:<6}{trig:<5}{d.height:>7} {cell(d, "f10")} {cell(d, "f30")} {cell(d, "f60")} {cell(d, "b1_R")} {cell(d, "b2_R")} '
                  f'{np.nanmean(d["b1_bps"].to_numpy()):+8.2f} {(d["b1_R"] > 0).mean():6.2f} {d["R_bps"].median():6.1f} {d["reb"].median():5.2f} {d["bars_anchor"].median():5.0f}')

FEATS = ('depth_z', 'legP_z', 'legDtr_z', 'legD_z', 'cvd_dd_z', 'cvd_dd_day', 'cvd_reb', 'clx_z', 'clx_day', 'bars_clx', 'sv_pos', 'legvol_adv',
         'bars_anchor', 'bars_tr', 'k', 'sl_px', 'r_px', 'sl_cvd', 'r_cvd', 'sl_vol', 'dist1', 'dist3', 'ret5', 'ret20', 'poc_d', 'vah_d', 'val_d', 'sess_pos', 'minute')

def quint(d, name, y):
    x = d[name].to_numpy().astype(float); ok = np.isfinite(x)
    q = np.quantile(x[ok], np.linspace(0, 1, 6)); b = np.clip(np.searchsorted(q, x, side='right') - 1, 0, 4)
    out = []
    for i in range(5):
        mu, t, n = L.cluster_t(d[y].to_numpy().astype(float)[ok & (b == i)], d['sid'].to_numpy()[ok & (b == i)])
        out.append((mu, t))
    return q, out

for trig in a.trigs.split(','):
    d = E.filter((pl.col('N') == a.N) & (pl.col('trig') == trig))
    print(f'\n2. N={a.N} trigger {trig}, both sides pooled ({d.height} events): f30 bps (t) by quintile of each feature; Q1 = lowest')
    print(f'   all: f30 {cell(d, "f30")}  f60 {cell(d, "f60")}  b1 {cell(d, "b1_R")} R;  long f30 {cell(d.filter(pl.col("side") == "long"), "f30")}  short f30 {cell(d.filter(pl.col("side") == "short"), "f30")}')
    print(f'{"":<12}' + ''.join(f'{"Q" + str(i + 1):>15}' for i in range(5)) + '    Q1 edge .. Q5 edge')
    for name in FEATS:
        q, o = quint(d, name, 'f30')
        print(f'{name:<12}' + ''.join(f'{mu:+7.2f} ({t:+4.1f}) ' for mu, t in o) + f'   {q[1]:+.2f} .. {q[4]:+.2f}')

    cols = ['depth_z', 'legP_z', 'legDtr_z', 'cvd_dd_z', 'cvd_reb', 'clx_z', 'sl_px', 'sl_cvd', 'r_cvd', 'sl_vol', 'dist1', 'ret20', 'poc_d', 'sess_pos']
    dd = d.drop_nulls(cols).filter(pl.all_horizontal([pl.col(c).is_finite() for c in cols]))
    sid = dd['sid'].to_numpy()
    Z = np.column_stack([np.ones(dd.height)] + [(dd[c].to_numpy() - dd[c].mean()) / dd[c].std() for c in cols])
    XtX = np.linalg.inv(Z.T @ Z)
    print(f'   regression on standardized features (bps per 1 sd, clustered t), n {dd.height}')
    print('      ' + ''.join(f'{c:>10}' for c in cols))
    for y in ('f10', 'f30', 'f60'):
        yy = dd[y].to_numpy(); b = XtX @ Z.T @ yy; e = yy - Z @ b
        _, inv = np.unique(sid, return_inverse=True); S = np.zeros((inv.max() + 1, Z.shape[1])); np.add.at(S, inv, Z * e[:, None])
        t = b / np.sqrt(np.diag(XtX @ (S.T @ S) @ XtX))
        print(f'{y:>5} ' + ''.join(f'{x:+10.2f}' for x in b[1:]))
        print(f'{"t":>5} ' + ''.join(f'{x:+10.1f}' for x in t[1:]))
