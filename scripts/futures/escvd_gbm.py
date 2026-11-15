#!/usr/bin/env python
"""ES CVD study, the CEILING test: can ANY function of the features predict the next 30 / 60 minutes?

  cd research && python -u scripts/futures/escvd_gbm.py [--m0 930 --m1 1260] > logs/escvd_gbm.log

LightGBM on every 3rd RTH bar of the FIT sample, leave-one-half-year-out; nothing is tuned on the scores (fixed, heavily
regularised parameters, no early stopping). Two feature sets: P = price, volume, context only; PD = P + the delta features.
If PD's out-of-fold predictions don't beat P's, delta carries nothing price doesn't; if neither is above zero, the
feature family has no edge at these horizons. Scores: the out-of-fold correlation, the session-clustered t of the
slope of the return on the prediction, and the mean return by decile of the prediction.
"""
import sys, argparse, numpy as np, lightgbm as lgb
from numba import njit
sys.path.insert(0, 'scripts/futures')
import escvd_lib as L
from escvd_legs import prior_ext

ap = argparse.ArgumentParser()
ap.add_argument('--m0', type=int, default=930); ap.add_argument('--m1', type=int, default=1260); ap.add_argument('--step', type=int, default=3)
ap.add_argument('--drop', default='', help='comma list of features to leave out')
a = ap.parse_args()

D = L.load(); n = D['n']; ns = D['ns']; M = L.M; si = D['si']; m = D['m']
kd, kp = L.vol_scales(D); F = L.forward(D)
sp, sd = D['sd_px'], D['sd_delta']

@njit(cache=True)
def leg_state(h, l, c, vw, vol, dlt, si, hiN, loN):
    """Per bar, for the leg anchored at the last new N-minute high: bars since, k, depth, rebound, cumD, CVD drawdown
    and rebound, volume, close - sellers' vwap."""
    n = len(h); O = np.full((n, 9), np.nan)
    have = False; anchor = 0; cumD = maxD = minD = lv = spv = sv = pk = low = 0.0; k = 0
    for t in range(n):
        if t == 0 or si[t] != si[t - 1]: have = False
        if np.isnan(hiN[t]): continue
        if h[t] > hiN[t]:
            have = True; anchor = t; cumD = 0.0; maxD = 0.0; minD = 0.0; lv = 0.0; spv = 0.0; sv = 0.0; pk = h[t]; low = l[t]; k = 0
        elif have:
            cumD += dlt[t]; lv += vol[t]; s = (vol[t] - dlt[t]) / 2.0; spv += s * vw[t]; sv += s
            if cumD > maxD: maxD = cumD; minD = cumD
            elif cumD < minD: minD = cumD
            if l[t] < loN[t]: k += 1
            if l[t] < low: low = l[t]
        if not have: continue
        O[t, 0] = t - anchor; O[t, 1] = k; O[t, 2] = low - pk; O[t, 3] = (c[t] - low) / (pk - low) if pk > low else 1.0
        O[t, 4] = cumD; O[t, 5] = minD - maxD; O[t, 6] = (cumD - minD) / (maxD - minD) if maxD > minD else 1.0
        O[t, 7] = lv; O[t, 8] = c[t] - spv / sv if sv > 0 else 0.0
    return O

@njit(cache=True)
def roll_ols(y, w):
    """OLS slope and r of y against the bar index over the last w bars (through the current one)."""
    n = len(y); sl = np.full(n, np.nan); r = np.full(n, np.nan)
    j = np.arange(w).astype(np.float64); jm = j.mean(); jv = ((j - jm) ** 2).sum()
    for t in range(w - 1, n):
        x = y[t - w + 1:t + 1]; xm = x.mean(); cov = ((j - jm) * (x - xm)).sum(); xv = ((x - xm) ** 2).sum()
        sl[t] = cov / jv
        if xv > 0: r[t] = cov / np.sqrt(jv * xv)
    return sl, r

csd = np.concatenate([[0.0], np.cumsum(D['delta'])]); csv = np.concatenate([[0.0], np.cumsum(D['volume'])])
i = np.arange(n) + 1
P, Dl = {}, {}
for w in (5, 15, 30, 60, 120):
    j = np.maximum(i - w, 0); vol = np.maximum(csv[i] - csv[j], 1.0)
    Dl[f'dz{w}'] = (csd[i] - csd[j]) / np.sqrt(kd * vol)
    pc = np.full(n, np.nan); pc[w:] = D['c'][w:] - D['c'][:-w]
    P[f'rz{w}'] = pc / np.sqrt(kp * vol)
    P[f'volr{w}'] = vol / (D['adv20'] * w / 390.0)
P['dist1'] = (D['c'] - L.rolling_vwap(D, 1)) / sp; P['dist3'] = (D['c'] - L.rolling_vwap(D, 3)) / sp
for w in (20, 60):
    s, r = roll_ols(D['c'], w); P[f'slpx{w}'] = s / sp * 60; P[f'rpx{w}'] = r
    s, r = roll_ols(D['volume'], w); P[f'slvol{w}'] = s * w / np.maximum(csv[i] - csv[np.maximum(i - w, 0)], 1.0)
    s, r = roll_ols(csd[1:], w); Dl[f'slcvd{w}'] = s / sd * 60; Dl[f'rcvd{w}'] = r
# the 5-minute delta extremes of the last 30 bars
d5 = Dl['dz5']
@njit(cache=True)
def roll_minmax(x, w):
    n = len(x); lo = np.full(n, np.nan); hi = np.full(n, np.nan)
    for t in range(w - 1, n):
        lo[t] = np.nanmin(x[t - w + 1:t + 1]); hi[t] = np.nanmax(x[t - w + 1:t + 1])
    return lo, hi
Dl['d5min30'], Dl['d5max30'] = roll_minmax(d5, 30)
# session-so-far: overnight (18:00 -> 09:30) and since the open
C2 = D['c'].reshape(ns, M); O2 = D['o'].reshape(ns, M); cs2 = np.cumsum(D['delta'].reshape(ns, M), axis=1)
P['on_ret'] = np.repeat((C2[:, L.RTH0 - 1] - O2[:, 0]), M) / sp
Dl['on_delta'] = np.repeat(cs2[:, L.RTH0 - 1], M) / sd
P['day_ret'] = (D['c'] - np.repeat(O2[:, L.RTH0], M)) / sp
Dl['day_delta'] = (cs2 - cs2[:, [L.RTH0 - 1]]).ravel() / sd
hi_s = np.maximum.accumulate(D['h'].reshape(ns, M), axis=1).ravel(); lo_s = np.minimum.accumulate(D['l'].reshape(ns, M), axis=1).ravel()
P['sess_pos'] = (D['c'] - lo_s) / np.maximum(hi_s - lo_s, 0.25)
Cc = C2[:, L.RTH1 - 1]
for k in (5, 20):
    x = np.full(ns, np.nan); x[k + 1:] = Cc[k:-1] - Cc[:-k - 1]
    P[f'ret{k}'] = np.repeat(x / (D['sd_px_s'] * np.sqrt(k)), M)
P['minute'] = m.astype(np.float64)
for N in (20, 60):
    for tag, sg in (('L', 1.0), ('S', -1.0)):
        h, l = (D['h'], D['l']) if sg > 0 else (-D['l'], -D['h'])
        hiN, loN = prior_ext(h, l, si, N)
        O = leg_state(h, l, sg * D['c'], sg * D['vwap'], D['volume'], sg * D['delta'], si, hiN, loN)
        v = np.maximum(O[:, 7], 1.0)
        P[f'{tag}{N}_bars'] = O[:, 0]; P[f'{tag}{N}_k'] = O[:, 1]; P[f'{tag}{N}_depth'] = O[:, 2] / sp; P[f'{tag}{N}_reb'] = O[:, 3]
        P[f'{tag}{N}_vol'] = O[:, 7] / D['adv20']
        Dl[f'{tag}{N}_cumD'] = O[:, 4] / np.sqrt(kd * v); Dl[f'{tag}{N}_cdd'] = O[:, 5] / np.sqrt(kd * v)
        Dl[f'{tag}{N}_creb'] = O[:, 6]; Dl[f'{tag}{N}_svgap'] = O[:, 8] / sp

for k in a.drop.split(','):
    P.pop(k, None); Dl.pop(k, None)
base = D['fit_bar'] & (m >= a.m0) & (m < a.m1) & (m % a.step == 0) & np.isfinite(F[60]) & np.isfinite(P['dist3']) & np.isfinite(P['rz120'])
idx = np.where(base)[0]
sess = np.array([str(s) for s in D['sess']])
block = np.array([s[:4] + ('H1' if s[5:7] <= '06' else 'H2') for s in sess])[si[idx]]
Xp = np.column_stack([P[k][idx] for k in P]); Xd = np.column_stack([Dl[k][idx] for k in Dl])
names_p, names_d = list(P), list(Dl)
print(f'fit bars {len(idx)} (every {a.step}rd bar of [{a.m0},{a.m1})), sessions {len(np.unique(si[idx]))}, blocks {sorted(set(block))}')
print(f'features: P {len(names_p)}, D {len(names_d)}\n')

par = dict(objective='regression', learning_rate=0.03, num_leaves=15, min_data_in_leaf=800, feature_fraction=0.5,
           bagging_fraction=0.5, bagging_freq=1, lambda_l2=20.0, verbose=-1, seed=7, num_threads=8)

def oof(X, y, rounds=300):
    p = np.full(len(y), np.nan)
    for b in sorted(set(block)):
        te = block == b
        mdl = lgb.train(par, lgb.Dataset(X[~te], y[~te]), num_boost_round=rounds)
        p[te] = mdl.predict(X[te])
    return p

def score(p, y_bps, g, label):
    c = np.corrcoef(p, y_bps)[0, 1]
    z = (p - p.mean()) / p.std()
    b = (z * (y_bps - y_bps.mean())).mean()                       # bps per 1 sd of prediction
    _, inv = np.unique(g, return_inverse=True); s = np.bincount(inv, weights=z * (y_bps - y_bps.mean() - b * z))
    t = b / (np.sqrt((s ** 2).sum()) / len(z))
    q = np.quantile(p, np.linspace(0, 1, 11)); d = np.clip(np.searchsorted(q, p, side='right') - 1, 0, 9)
    dec = [y_bps[d == k].mean() for k in range(10)]
    t10 = L.cluster_t(y_bps[d == 9], g[d == 9])[1]; t1 = L.cluster_t(y_bps[d == 0], g[d == 0])[1]
    print(f'{label:<14} corr {c:+.4f}  slope {b:+.2f} bps/sd (t {t:+.1f})  deciles ' + ' '.join(f'{x:+5.1f}' for x in dec) + f'   t(D1) {t1:+.1f} t(D10) {t10:+.1f}')
    return c

for hname in (10, 30, 60):
    y_bps = F[hname][idx]; y = y_bps / (sp[idx] / D['raw'][idx] * 1e4)
    print(f'--- horizon {hname}m: sd {y_bps.std():.1f} bps')
    pp = oof(Xp, y); score(pp, y_bps, si[idx], 'P  (price)')
    pd_ = oof(np.column_stack([Xp, Xd]), y); score(pd_, y_bps, si[idx], 'PD (+delta)')
    po = oof(Xd, y); score(po, y_bps, si[idx], 'D  (delta only)')
    by = {}
    for b in sorted(set(block)):
        te = block == b; by[b] = (np.corrcoef(pp[te], y_bps[te])[0, 1], np.corrcoef(pd_[te], y_bps[te])[0, 1])
    print('   per block corr  P / PD: ' + '  '.join(f'{b} {x:+.3f}/{z:+.3f}' for b, (x, z) in by.items()))

# what a model fitted on everything leans on (gain), for the record only
y = F[30][idx] / (sp[idx] / D['raw'][idx] * 1e4)
mdl = lgb.train(par, lgb.Dataset(np.column_stack([Xp, Xd]), y), num_boost_round=300)
g = mdl.feature_importance('gain'); nm = names_p + names_d; o = np.argsort(-g)[:20]
print('\ntop gain, PD model at 30m (in-sample, descriptive): ' + ', '.join(f'{nm[k]} {g[k] / g.sum() * 100:.1f}%' for k in o))
