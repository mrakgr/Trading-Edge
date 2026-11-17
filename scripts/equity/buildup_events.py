r"""BUILDUP STUDY, step 3: the big-move events. The broad report (buildup_report.py) shows the buildup B adds almost nothing
on ordinary bars, but inside the BIG price moves the reversal depends on B. Here every ticker-day's FIRST 30-minute window
(sampled every 30 min, causal) with |R| >= --min-r bp is one event (one per ticker-day and direction: no overlap), with
B (the 30-min signed volume, % of the previous day's volume), the 30-min relative volume (vs the previous day's volume per
30 min), and the forward returns F (bp, gross, from the next bar's first 1 s vwap).

  cd research && python -u scripts/equity/buildup_events.py > logs/buildup_events.log 2>&1; echo exit $?
"""
import argparse, glob, os
from multiprocessing import Pool
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--bars', default='/mnt/d/trading-edge-bulk/buildup_5m')
ap.add_argument('--out', default='data/equity/buildup/events.parquet')
ap.add_argument('--min-r', type=float, default=300.0)
ap.add_argument('--w', type=int, default=6)
a = ap.parse_args()
NB = 78
HS = {'f30': 6, 'f60': 12, 'f120': 24, 'fclose': -1}


def one(f):
    d = os.path.basename(f)[:-8]
    X = pd.read_parquet(f, columns=['ticker', 'm', 'vol', 'sv', 'dv', 'p0', 'p1', 'prev_vol', 'prev_dv'])
    X = X[(X.m >= 0) & (X.m < NB)]
    tk, ti = np.unique(X.ticker.to_numpy(), return_inverse=True)
    n = len(tk); mm = X.m.to_numpy().astype(int)
    sv = np.zeros((n, NB)); vol = np.zeros((n, NB)); p0 = np.full((n, NB), np.nan); p1 = np.full((n, NB), np.nan)
    sv[ti, mm] = X.sv.to_numpy(); vol[ti, mm] = X.vol.to_numpy(); p0[ti, mm] = X.p0.to_numpy(); p1[ti, mm] = X.p1.to_numpy()
    pv = np.zeros(n); pv[ti] = X.prev_vol.to_numpy(); pdv = np.zeros(n); pdv[ti] = X.prev_dv.to_numpy()
    p1f = pd.DataFrame(p1).ffill(axis=1).to_numpy()
    p0f = np.where(np.isnan(p0), np.concatenate([np.full((n, 1), np.nan), p1f[:, :-1]], 1), p0)
    csv = np.concatenate([np.zeros((n, 1)), np.cumsum(sv, 1)], 1); cv = np.concatenate([np.zeros((n, 1)), np.cumsum(vol, 1)], 1)
    W = a.w; seen = np.zeros((n, 2), bool); rows = []
    for m in range(W - 1, NB - 1, W):
        B = 100 * (csv[:, m + 1] - csv[:, m + 1 - W]) / np.maximum(pv, 1)
        rv = (cv[:, m + 1] - cv[:, m + 1 - W]) / np.maximum(pv * W / NB, 1)
        R = 1e4 * np.log(p1f[:, m] / p0f[:, m + 1 - W])
        e = p0f[:, m + 1]
        for sgn, col in ((1, 0), (-1, 1)):
            hit = np.isfinite(R) & (sgn * R >= a.min_r) & ~seen[:, col] & np.isfinite(e) & (e > 0) & (pv > 0)
            for i in np.nonzero(hit)[0]:
                seen[i, col] = True
                r = dict(date=d, ticker=tk[i], m=m, R=R[i], B=B[i], rvol=rv[i], px=e[i], prev_dv=pdv[i],
                         imb=(csv[i, m + 1] - csv[i, m + 1 - W]) / max(cv[i, m + 1] - cv[i, m + 1 - W], 1))
                for k, H in HS.items():
                    x = NB - 1 if H == -1 else m + H
                    r[k] = 1e4 * np.log(p1f[i, x] / e[i]) if x < NB and np.isfinite(p1f[i, x]) else np.nan
                rows.append(r)
    return rows


if __name__ == '__main__':
    files = sorted(glob.glob(f'{a.bars}/*.parquet'))
    out = []
    with Pool(8) as pool:
        for r in pool.imap_unordered(one, files, chunksize=4): out += r
    E = pd.DataFrame(out).sort_values(['date', 'ticker', 'm']).reset_index(drop=True)
    E.to_parquet(a.out, index=False)
    print(f'{len(E):,} events, {E.date.nunique()} days', flush=True)
