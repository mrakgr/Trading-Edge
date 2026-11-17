r"""BUILDUP STUDY, step 2: does the liquidity providers' forced inventory (the signed aggressor volume they absorbed) predict a
REVERSAL, beyond the price move itself? Reads buildup_scan.py's 5-minute bars.

At the end of bar m of a ticker-day (causal: bars <= m):
  B_W = sum of sv over the last W bars / the PREVIOUS day's volume (signed; > 0 = buyers aggressive, makers SHORT)
  R_W = the price move over those W bars, bp (the last 1 s vwap of bar m vs the first of bar m-W+1)
Entry = the first 1 s vwap of bar m+1. Forward return F_H = the last 1 s vwap of bar m+H vs the entry, bp (H in bars; 'close'
= bar 77), winsorized at ±2000 bp. Signal bars are sampled every W bars (no overlap of the signal windows; the forward
windows of H > W still overlap, so t-stats are optimistic). Gross: no spread or fees.
REVERSAL = F with the opposite sign of B. The tables show mean F (bp) by B bin, and the DOUBLE SORT: within a bin of the
price move R, does B still predict F? (If B only restates R, the columns are flat within each row.)

  cd research && python -u scripts/equity/buildup_report.py > data/equity/buildup/report.md 2> logs/buildup_report.log; echo exit $?
"""
import argparse, glob, os
from multiprocessing import Pool
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--bars', default='/mnt/d/trading-edge-bulk/buildup_5m')
ap.add_argument('--start', default='2016-01-01')
ap.add_argument('--end', default='9999-12-31')
ap.add_argument('--workers', type=int, default=8)
a = ap.parse_args()
NB = 78
WS = (3, 6, 12)                     # 15 / 30 / 60 min
HS = (6, 12, 24, -1)                # 30 / 60 / 120 min / the close
HN = {6: '30 min', 12: '60 min', 24: '120 min', -1: 'close'}
BE = np.array([-np.inf, -10, -3, -1, -0.3, -0.1, 0.1, 0.3, 1, 3, 10, np.inf])      # B, % of the previous day's volume
BL = ['< −10', '−10…−3', '−3…−1', '−1…−0.3', '−0.3…−0.1', '±0.1', '0.1…0.3', '0.3…1', '1…3', '3…10', '> 10']
RE = np.array([-np.inf, -1000, -300, -100, -30, 30, 100, 300, 1000, np.inf])      # R, bp
RL = ['< −1000', '−1000…−300', '−300…−100', '−100…−30', '±30', '30…100', '100…300', '300…1000', '> 1000']


def one(f):
    d = os.path.basename(f)[:-8]
    X = pd.read_parquet(f, columns=['ticker', 'm', 'vol', 'sv', 'p0', 'p1', 'prev_vol'])
    X = X[(X.m >= 0) & (X.m < NB)]
    tk, ti = np.unique(X.ticker.to_numpy(), return_inverse=True)
    n = len(tk); mm = X.m.to_numpy().astype(int)
    sv = np.zeros((n, NB)); p0 = np.full((n, NB), np.nan); p1 = np.full((n, NB), np.nan)
    sv[ti, mm] = X.sv.to_numpy(); p0[ti, mm] = X.p0.to_numpy(); p1[ti, mm] = X.p1.to_numpy()
    pv = np.zeros(n); pv[ti] = X.prev_vol.to_numpy()
    # carry the last price forward through bars without trades (a missing first price = the carried last one)
    p1f = pd.DataFrame(p1).ffill(axis=1).to_numpy()
    p0f = np.where(np.isnan(p0), np.concatenate([np.full((n, 1), np.nan), p1f[:, :-1]], 1), p0)
    csv = np.concatenate([np.zeros((n, 1)), np.cumsum(sv, 1)], 1)
    acc = {}
    yr = int(d[:4])
    for W in WS:
        for m in range(W - 1, NB - 1, W):
            B = 100 * (csv[:, m + 1] - csv[:, m + 1 - W]) / np.maximum(pv, 1)
            R = 1e4 * np.log(p1f[:, m] / p0f[:, m + 1 - W])
            e = p0f[:, m + 1]
            ok = np.isfinite(B) & np.isfinite(R) & np.isfinite(e) & (e > 0) & (pv > 0)
            bi = np.searchsorted(BE, B, 'right') - 1; ri = np.searchsorted(RE, R, 'right') - 1
            for H in HS:
                x = NB - 1 if H == -1 else m + H
                if x >= NB: continue
                F = np.clip(1e4 * np.log(p1f[:, x] / e), -2000, 2000)
                g = ok & np.isfinite(F)
                key = (W, H)
                if key not in acc: acc[key] = np.zeros((3, len(BL), len(RL))); acc[(W, H, 'y')] = {}
                np.add.at(acc[key][0], (bi[g], ri[g]), 1); np.add.at(acc[key][1], (bi[g], ri[g]), F[g]); np.add.at(acc[key][2], (bi[g], ri[g]), F[g] ** 2)
                ya = acc[(W, H, 'y')].setdefault(yr, np.zeros((2, len(BL))))
                np.add.at(ya[0], bi[g], 1); np.add.at(ya[1], bi[g], F[g])
    return acc


def merge(A, B):
    for k, v in B.items():
        if isinstance(v, dict):
            dd = A.setdefault(k, {})
            for y, arr in v.items(): dd[y] = dd.get(y, 0) + arr
        else:
            A[k] = A.get(k, 0) + v
    return A


if __name__ == '__main__':
    files = sorted(f for f in glob.glob(f'{a.bars}/*.parquet') if a.start <= os.path.basename(f)[:10] <= a.end)
    acc = {}
    with Pool(a.workers) as pool:
        for k, r in enumerate(pool.imap_unordered(one, files, chunksize=4)):
            acc = merge(acc, r)
            if (k + 1) % 250 == 0: print(f'{k + 1}/{len(files)}', file=__import__('sys').stderr, flush=True)
    print(f'# Market-maker BUILDUP vs the forward return — {len(files)} days, {os.path.basename(files[0])[:10]} … {os.path.basename(files[-1])[:10]}\n')
    print('B = the signed aggressor volume over the window as % of the PREVIOUS day\'s volume (> 0: buyers aggressive, makers short). '
          'F = the forward return (bp, gross) from the next bar\'s first 1 s vwap. A REVERSAL is F against the sign of B.\n')
    for W in WS:
        print(f'## Window {W * 5} min\n')
        print('| B (%) | n | ' + ' | '.join(f'F {HN[H]}' for H in HS) + ' |')
        print('|---|---|' + '---|' * len(HS))
        for b in range(len(BL)):
            cells = []
            n0 = acc[(W, HS[0])][0][b].sum()
            for H in HS:
                c = acc[(W, H)]; nn = c[0][b].sum(); s = c[1][b].sum(); s2 = c[2][b].sum()
                if nn < 30: cells.append('-'); continue
                mu = s / nn; sd = np.sqrt(max(s2 / nn - mu ** 2, 0)); cells.append(f'{mu:+.1f} (t {mu / sd * np.sqrt(nn):+.0f})')
            print(f'| {BL[b]} | {int(n0):,} | ' + ' | '.join(cells) + ' |')
        print()
    for W, H in ((6, 12), (12, 24), (6, -1)):
        c = acc[(W, H)]
        print(f'## Double sort — window {W * 5} min, F {HN[H]}: mean F (bp) by the price move R (rows) and the buildup B (columns); n < 200 blank\n')
        print('| R (bp) \\ B (%) | ' + ' | '.join(BL) + ' |')
        print('|---|' + '---|' * len(BL))
        for r in range(len(RL)):
            row = []
            for b in range(len(BL)):
                nn = c[0][b, r]
                row.append(f'{c[1][b, r] / nn:+.0f}' if nn >= 200 else '')
            print(f'| {RL[r]} | ' + ' | '.join(row) + ' |')
        print()
    W, H = 6, 12
    ya = acc[(W, H, 'y')]
    print(f'## By year — window 30 min, F 60 min: mean F (bp) by B bin\n')
    print('| year | ' + ' | '.join(BL) + ' |')
    print('|---|' + '---|' * len(BL))
    for y in sorted(ya):
        nn, s = ya[y]
        print(f'| {y} | ' + ' | '.join(f'{s[b] / nn[b]:+.0f}' if nn[b] >= 200 else '' for b in range(len(BL))) + ' |')
