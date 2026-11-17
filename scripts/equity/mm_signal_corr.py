r"""MARKET-MAKING SIM, step 2: how well do CAUSAL trades + NBBO features predict the next few seconds of the mid?
The noisy oracle of mm_sim.py (--oracle 5:5 --oracle-rho) puts the BREAK-EVEN of the join rule at a correlation of about
0.35 with the 5 s mid move. This measures the correlation real features reach.

On a 100 ms grid over each trigger's trading window (the trigger to +30 min), from the data strictly before the grid
time:
  flow_1 / flow_5   the signed lit volume over the last 1 / 5 s (+ above the prevailing mid, − below), ÷ that window's
                    total lit volume (−1..+1)
  ret_1 / ret_5     the mid return over the last 1 / 5 s, bp
  qimb              the NBBO size imbalance bid_size / (bid_size + ask_size) − ½
  micro             the micro-price − mid, bp: spread · (qimb), as a share of the mid
  spr               the quoted spread, bp (for an interaction only)
Target: the mid return over the next H s (1, 5), bp. Reported: the pooled and the median per-window Pearson correlation of
each feature, and of an OLS combination fit on HALF the windows and scored on the other half (no in-sample flattery).

  cd research && python -u scripts/equity/mm_signal_corr.py > data/equity/mm_sim/signal_corr.md 2> logs/mm_signal_corr.log; echo exit $?
"""
import argparse, glob, os
from multiprocessing import Pool
import numpy as np, pandas as pd
import importlib.util

ap = argparse.ArgumentParser()
ap.add_argument('--triggers', default='data/equity/mm_opportunity/triggers_B3.csv')
ap.add_argument('--quotes', default='data/equity/mm_sim/quotes')
ap.add_argument('--prints', default='data/equity/mm_sim/prints')
ap.add_argument('--minutes', type=float, default=30.0)
ap.add_argument('--step', type=float, default=0.1)
a = ap.parse_args()
import datetime, zoneinfo
NY = zoneinfo.ZoneInfo('America/New_York')
def ns_at(d, sec):
    y, m, dd = map(int, d.split('-')); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)
def sec_of(hhmm): h, m = map(int, hhmm.split(':')); return h * 3600 + m * 60
FEAT = ['flow_1', 'flow_5', 'ret_1', 'ret_5', 'qimb', 'micro']


def one(r):
    nm = f'{r["ticker"]}_{r["date"]}_{r["trigger_et"].replace(":", "")}'
    Q = pd.read_parquet(f'{a.quotes}/{nm}.parquet').dropna().sort_values('ts', kind='stable')
    P = pd.read_parquet(f'{a.prints}/{nm}.parquet')
    b = Q.bid.to_numpy(float); aq = Q.ask.to_numpy(float)
    v = (b > 0) & (aq > b) & ((aq - b) < 0.05 * (aq + b))
    if v.sum() < 100: return None
    qt = Q.ts.to_numpy(np.int64)[v]; mid = 0.5 * (b + aq)[v]
    bs = Q.bid_size.to_numpy(float)[v]; as_ = Q.ask_size.to_numpy(float)[v]; sp = (aq - b)[v]
    t0 = ns_at(r['date'], sec_of(r['trigger_et'])); t1 = t0 + int(a.minutes * 60e9)
    t1 = min(t1, qt[-1] - int(5e9))
    if t1 <= t0: return None
    g = np.arange(t0, t1, int(a.step * 1e9))
    j = np.searchsorted(qt, g, 'left') - 1                 # the last quote strictly before
    ok = j >= 0; g = g[ok]; j = j[ok]
    m0 = mid[j]
    out = dict(name=np.full(len(g), nm))
    for H in (1, 5):
        jh = np.searchsorted(qt, g + int(H * 1e9), 'right') - 1
        out[f'y_{H}'] = 1e4 * (mid[jh] / m0 - 1)
    for H in (1, 5):
        jb = np.clip(np.searchsorted(qt, g - int(H * 1e9), 'right') - 1, 0, None)
        out[f'ret_{H}'] = 1e4 * (m0 / mid[jb] - 1)
    qi = bs[j] / (bs[j] + as_[j]) - 0.5
    out['qimb'] = qi; out['micro'] = 1e4 * sp[j] * qi / m0; out['spr'] = 1e4 * sp[j] / m0
    # the signed lit flow: each lit print signed against the mid of the last quote strictly before it
    L = P[P.lit]
    pt = L.ts.to_numpy(np.int64); pp = L.price.to_numpy(float); ps = L['size'].to_numpy(float)
    k = np.searchsorted(qt, pt, 'left') - 1
    sg = np.where(k >= 0, np.sign(pp - mid[np.clip(k, 0, None)]), 0)
    cs = np.concatenate([[0], np.cumsum(sg * ps)]); ca = np.concatenate([[0], np.cumsum(ps)])
    for H in (1, 5):
        i1 = np.searchsorted(pt, g, 'left'); i0 = np.searchsorted(pt, g - int(H * 1e9), 'left')
        vol = ca[i1] - ca[i0]
        out[f'flow_{H}'] = np.where(vol > 0, (cs[i1] - cs[i0]) / np.maximum(vol, 1), 0.0)
    return pd.DataFrame(out)


if __name__ == '__main__':
    R = pd.read_csv(a.triggers).to_dict('records')
    with Pool(8) as pool:
        D = pd.concat([x for x in pool.map(one, R) if x is not None], ignore_index=True)
    D = D.replace([np.inf, -np.inf], np.nan).dropna()
    names = D.name.unique(); rng = np.random.default_rng(1); half = set(rng.choice(names, len(names) // 2, replace=False))
    tr = D.name.isin(half)
    print(f'# Causal features vs the next H s of the mid — {len(names)} windows, {len(D):,} grid points ({a.step} s)\n')
    print('Break-even (mm_sim noisy oracle, join, base): a correlation of ~0.35 with the 5 s move.\n')
    print('| feature | corr 1 s (pooled) | corr 5 s (pooled) | corr 5 s (median per window) |\n|---|---|---|---|')
    for f in FEAT:
        pw = D.groupby('name').apply(lambda x: x[f].corr(x['y_5']) if x[f].std() > 0 else np.nan, include_groups=False)
        print(f'| {f} | {D[f].corr(D.y_1):+.3f} | {D[f].corr(D.y_5):+.3f} | {pw.median():+.3f} |')
    X = np.column_stack([D[FEAT].to_numpy(), D[FEAT].to_numpy() * D[['spr']].to_numpy() / 50, np.ones(len(D))])
    for H in (1, 5):
        y = D[f'y_{H}'].to_numpy()
        beta = np.linalg.lstsq(X[tr.to_numpy()], y[tr.to_numpy()], rcond=None)[0]
        yh = X @ beta; te = ~tr.to_numpy()
        pw = pd.Series(yh[te]).groupby(D.name.to_numpy()[te]).apply(lambda s: np.nan) if False else None
        Dt = pd.DataFrame(dict(n=D.name.to_numpy()[te], y=y[te], yh=yh[te]))
        pwc = Dt.groupby('n').apply(lambda x: x.y.corr(x.yh), include_groups=False)
        print(f'\nOLS combination (the 6 features + × spread), fit on half the windows, OUT OF SAMPLE on the other half, H = {H} s: '
              f'pooled corr {np.corrcoef(y[te], yh[te])[0, 1]:+.3f}, median per window {pwc.median():+.3f}')
