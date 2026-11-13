#!/usr/bin/env python3
"""WHERE OPTION VOLUME TRADES (user 2026-11-13: which strikes / deltas / tenors are actually traded - can we be the
other side of real flow? - and daily per-name activity features for the systems).

Per root, per year (RAM), market_full rows with volume > 0 on the root's IN-UNIVERSE days: S = the unadjusted close
(daily_adjusted), IV from the NBBO mid (the trade's close price when the quote is missing/crossed), BS r=q=0, T = trading
days / 252, delta from that IV. |delta| buckets (OTM side < .5): <.05 .05-.15 .15-.30 .30-.45 .45-.55 (ATM) .55-.70 .70-.85
>.85, 'na' = no IV (price outside the no-arbitrage range). Calendar-DTE buckets: 0, 1-2, 3-7, 8-30, 31-90, 91-365, >365.

Writes  <out>/profile.parquet   year x right x dte_b x delta_b: volume, trades (ThetaData 'count'), contracts traded,
                                 premium $ (volume x close x 100)
        <out>/root_day.parquet  per (root, date): totals and the buckets the systems use (all DTE <= 45 unless noted):
          vol, trades, prem (all), vol_c / vol_p, vol_atm (|d| .40-.60), vol_cw / vol_pw (wing |d| .05-.35), vol_tail
          (|d| < .05), vol_0_7 (DTE <= 7), vol_far (DTE > 90), trades_atm / trades_cw / trades_pw
    cd research && python3 scripts/options/activity.py > logs/options_activity.log 2>&1
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
from multiprocessing import Pool
from scipy.special import ndtr
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from survey_inputs import load

ap = argparse.ArgumentParser()
ap.add_argument('--eod', default='data/options/market_full')
ap.add_argument('--out', default='data/options/activity')
ap.add_argument('--roots', default='')
ap.add_argument('--workers', type=int, default=4)
a = ap.parse_args()
t0_ = time.time()
I = load(a.eod, a.roots)
roots, SPOT, TD = I['roots'], I['SPOT'], I['TD']
UI = duckdb.sql("select root, cast(date as date) d from 'data/options/universe_daily_full.parquet' where in_universe").df()
UI['d'] = pd.to_datetime(UI.d)
UDAYS = {r: g.d.values for r, g in UI.groupby('root')}
DB = [0, 1, 3, 8, 31, 91, 366]; DL = ['0', '1-2', '3-7', '8-30', '31-90', '91-365', '>365']
AB = [0, .05, .15, .30, .45, .55, .70, .85, 1.0001]; AL = ['<.05', '.05-.15', '.15-.30', '.30-.45', 'ATM .45-.55', '.55-.70', '.70-.85', '>.85']
print(f'inputs {time.time() - t0_:.0f}s: {len(roots)} roots', flush=True)


def implied(price, S, K, T, call):
    lo = np.full(len(price), 1e-4); hi = np.full(len(price), 8.0); sT = np.sqrt(T); lk = np.log(S / K)
    for _ in range(45):
        m = (lo + hi) / 2; v = m * sT
        d1 = (lk + 0.5 * v * v) / v; c = S * ndtr(d1) - K * ndtr(d1 - v)
        p = np.where(call, c, c - S + K)
        up = p > price; hi = np.where(up, m, hi); lo = np.where(up, lo, m)
    iv = (lo + hi) / 2
    intr = np.where(call, np.maximum(S - K, 0), np.maximum(K - S, 0))
    return np.where((price > intr + 1e-9) & (price < np.where(call, S, K)) & (iv < 7.9) & (iv > 2e-4), iv, np.nan)


def run(root):
    days = UDAYS.get(root)
    if days is None or root not in SPOT: return [], []
    s = SPOT[root]
    files = sorted(glob.glob(f'{a.eod}/{root}__*.parquet'))
    years = sorted({int(os.path.basename(f).split('__')[1][:4]) for f in files})
    prof, rd = [], []
    dset = pd.DatetimeIndex(days)
    for y in years:
        fy = [f for f in files if os.path.basename(f).split('__')[1][:4] == str(y)]
        if not ((dset.year == y).any()): continue
        q = duckdb.sql(f"""select cast(date as date) date, cast(expiration as date) exp, strike K, "right" r, volume v, count n,
                           bid, ask, close from read_parquet({fy}) where volume > 0""").df()
        if q.empty: continue
        q['date'] = pd.to_datetime(q.date); q = q[q.date.isin(dset)]
        q['exp'] = pd.to_datetime(q.exp)
        q['S'] = q.date.map(s); q = q.dropna(subset=['S'])
        if q.empty: continue
        q['cdte'] = (q.exp - q.date).dt.days
        ti = np.searchsorted(TD, q.date.values.astype('datetime64[D]'), 'right') - 1
        te = np.searchsorted(TD, q.exp.values.astype('datetime64[D]'), 'right') - 1
        T = np.maximum(te - ti, 0.25) / 252   # 0-DTE: a quarter session
        good = (q.bid > 0) & (q.ask >= q.bid)
        px = np.where(good, (q.bid + q.ask) / 2, q.close)
        call = (q.r == 'C').values
        iv = implied(px, q.S.values, q.K.values, T, call)
        sT = iv * np.sqrt(T); d1 = (np.log(q.S.values / q.K.values) + 0.5 * sT * sT) / sT
        ad = np.abs(np.where(call, ndtr(d1), ndtr(d1) - 1))
        q['ad'] = ad
        q['dte_b'] = pd.cut(q.cdte, DB + [100000], right=False, labels=DL).astype(str)
        q['delta_b'] = pd.cut(q.ad, AB, right=False, labels=AL).astype(str).where(np.isfinite(ad), 'na')
        q['prem'] = q.v * q.close * 100
        q['year'] = y
        prof.append(q.groupby(['year', 'r', 'dte_b', 'delta_b'], observed=True)
                     .agg(volume=('v', 'sum'), trades=('n', 'sum'), contracts=('v', 'size'), prem=('prem', 'sum')).reset_index())
        n45 = q.cdte <= 45
        def m(mask, col='v'): return q[col].where(mask, 0)
        q['vol_c'] = m(call); q['vol_p'] = m(~call)
        atm = n45 & (q.ad >= .40) & (q.ad <= .60); cw = n45 & call & (q.ad >= .05) & (q.ad <= .35)
        pw = n45 & ~call & (q.ad >= .05) & (q.ad <= .35)
        q['vol_atm'] = m(atm); q['vol_cw'] = m(cw); q['vol_pw'] = m(pw); q['vol_tail'] = m(n45 & (q.ad < .05))
        q['vol_0_7'] = m(q.cdte <= 7); q['vol_far'] = m(q.cdte > 90)
        q['trades_atm'] = m(atm, 'n'); q['trades_cw'] = m(cw, 'n'); q['trades_pw'] = m(pw, 'n')
        cols = ['v', 'n', 'prem', 'vol_c', 'vol_p', 'vol_atm', 'vol_cw', 'vol_pw', 'vol_tail', 'vol_0_7', 'vol_far',
                'trades_atm', 'trades_cw', 'trades_pw']
        g = q.groupby('date')[cols].sum().reset_index().rename(columns={'v': 'vol', 'n': 'trades'})
        g['root'] = root
        rd.append(g)
        del q
    return prof, rd


if __name__ == '__main__':
    os.makedirs(a.out, exist_ok=True)
    P, R = [], []
    with Pool(a.workers, maxtasksperchild=20) as pool:
        for i, (p, r) in enumerate(pool.imap_unordered(run, roots), 1):
            P += p; R += r
            if i % 50 == 0 or i == len(roots): print(f'{i}/{len(roots)} roots  {time.time() - t0_:.0f}s', flush=True)
    P = pd.concat(P).groupby(['year', 'r', 'dte_b', 'delta_b']).sum().reset_index()
    P.to_parquet(f'{a.out}/profile.parquet', index=False)
    R = pd.concat(R, ignore_index=True); R.to_parquet(f'{a.out}/root_day.parquet', index=False)
    print(f'wrote profile {len(P)} rows, root_day {len(R):,} rows  {time.time() - t0_:.0f}s')
