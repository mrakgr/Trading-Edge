#!/usr/bin/env python3
"""PATIENT MID FILLS (user 2026-11-13): rest a limit at yesterday's closing NBBO mid M - how often does the NEXT day
trade through it, and what happens after the fill? (Also the same-day lookahead version: did the day itself trade above /
below its own closing mid.)

Per contract-day t (the root in the universe on t, a two-sided NBBO at the close, M = mid >= $0.05, calendar DTE 1-90),
next trading day t+1 for the SAME contract (EOD OHLC = the day's trade prints):
  short fill  = high(t+1) >= M  (strict: > M)   - someone paid at least our ask
  long  fill  = low(t+1)  <= M  (strict: < M)
  markout     = the mid at the t+1 close vs M, in % of M, signed for the side (+ = good for us): short (M - mid1)/M,
                long (mid1 - M)/M. Mean over FILLED (adverse selection) and over ALL (what the mid did anyway).
  half-spread at t (% of M) = what a marketable order would have paid instead.
⚠ The EOD high/low include every print: complex-order legs can print outside the NBBO, so a 'fill' here is an UPPER BOUND
  on what a resting simple limit gets. Counts of trades above/below need trade-level data (ThetaData trade_quote).
Delta from the t mid IV (BS r=q=0), buckets as activity.py.

Writes <out>/fills.parquet: ygrp x r x dte_b x delta_b x hs_b x vol_b (contract's volume on t: 0 / 1-9 / 10-99 / 100+) ->
  n, sums of the fill flags, markouts (filled / all), half-spread, and same-day flags.
    cd research && python3 scripts/options/fill_study.py > logs/options_fill_study.log 2>&1
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
from multiprocessing import Pool
from scipy.special import ndtr
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from survey_inputs import load

ap = argparse.ArgumentParser()
ap.add_argument('--eod', default='data/options/market_full')
ap.add_argument('--out', default='data/options/fills')
ap.add_argument('--roots', default='')
ap.add_argument('--workers', type=int, default=4)
a = ap.parse_args()
t0_ = time.time()
I = load(a.eod, a.roots)
roots, SPOT, TD = I['roots'], I['SPOT'], I['TD']
UI = duckdb.sql("select root, cast(date as date) d from 'data/options/universe_daily_full.parquet' where in_universe").df()
UI['d'] = pd.to_datetime(UI.d)
UDAYS = {r: g.d.values for r, g in UI.groupby('root')}
DB = [1, 8, 15, 31, 61, 91]; DL = ['1-7', '8-14', '15-30', '31-60', '61-90']
AB = [0, .05, .15, .30, .45, .55, .70, .85, 1.0001]; AL = ['<.05', '.05-.15', '.15-.30', '.30-.45', 'ATM .45-.55', '.55-.70', '.70-.85', '>.85']
HB = [0, .01, .02, .05, .10, .20, 10]; HL = ['<1%', '1-2%', '2-5%', '5-10%', '10-20%', '>20%']
VB = [-1, 0, 9, 99, 1e12]; VL = ['0', '1-9', '10-99', '100+']
KEYS = ['ygrp', 'r', 'dte_b', 'delta_b', 'hs_b', 'vol_b']
print(f'inputs {time.time() - t0_:.0f}s: {len(roots)} roots', flush=True)


def implied(price, S, K, T, call):
    lo = np.full(len(price), 1e-4); hi = np.full(len(price), 8.0); sT = np.sqrt(T); lk = np.log(S / K)
    for _ in range(40):
        m = (lo + hi) / 2; v = m * sT
        d1 = (lk + 0.5 * v * v) / v; c = S * ndtr(d1) - K * ndtr(d1 - v)
        p = np.where(call, c, c - S + K)
        up = p > price; hi = np.where(up, m, hi); lo = np.where(up, lo, m)
    iv = (lo + hi) / 2
    intr = np.where(call, np.maximum(S - K, 0), np.maximum(K - S, 0))
    return np.where((price > intr + 1e-9) & (price < np.where(call, S, K)) & (iv < 7.9) & (iv > 2e-4), iv, np.nan)


def ygrp(y):
    return '2012-15' if y <= 2015 else '2016-19' if y <= 2019 else '2020-22' if y <= 2022 else '2023-26'


def run(root):
    days = UDAYS.get(root)
    if days is None or root not in SPOT: return []
    s = SPOT[root]; dset = pd.DatetimeIndex(days)
    tdn = pd.Series(TD[1:], index=pd.DatetimeIndex(TD[:-1]))     # next trading day
    out = []
    for f in sorted(glob.glob(f'{a.eod}/{root}__*.parquet')):     # one quarter chunk at a time (RAM)
        q = duckdb.sql(f"""
          with x as (select cast(date as date) date, cast(expiration as date) exp, strike K, "right" r, volume v,
                            open, high, low, bid, ask, close from read_parquet('{f}')
                     where cast(expiration as date) - cast(date as date) between 0 and 91)
          select *, lead(date) over w d1, lead(open) over w open1, lead(high) over w high1, lead(low) over w low1, lead(v) over w v1,
                    lead(bid) over w bid1, lead(ask) over w ask1
          from x window w as (partition by exp, K, r order by date)""").df()
        q['date'] = pd.to_datetime(q.date); q = q[q.date.isin(dset)]
        q = q[(q.bid > 0) & (q.ask >= q.bid)]
        q['M'] = (q.bid + q.ask) / 2; q = q[q.M >= 0.05]
        q['exp'] = pd.to_datetime(q.exp); q['cdte'] = (q.exp - q.date).dt.days
        q = q[(q.cdte >= 1) & (q.cdte <= 90)]
        q['d1'] = pd.to_datetime(q.d1)
        q = q[q.d1.values == tdn.reindex(q.date).values]             # the contract's next row IS the next trading day
        q['S'] = q.date.map(s); q = q.dropna(subset=['S'])
        if q.empty: continue
        ti = np.searchsorted(TD, q.date.values.astype('datetime64[D]'), 'right') - 1
        te = np.searchsorted(TD, q.exp.values.astype('datetime64[D]'), 'right') - 1
        T = np.maximum(te - ti, 0.25) / 252
        call = (q.r == 'C').values
        iv = implied(q.M.values, q.S.values, q.K.values, T, call)
        sT = iv * np.sqrt(T); d1 = (np.log(q.S.values / q.K.values) + 0.5 * sT * sT) / sT
        ad = np.abs(np.where(call, ndtr(d1), ndtr(d1) - 1))
        q['delta_b'] = pd.cut(pd.Series(ad, index=q.index), AB, right=False, labels=AL).astype(str).where(np.isfinite(ad), 'na')
        q['dte_b'] = pd.cut(q.cdte, DB, right=False, labels=DL).astype(str)
        hs = (q.ask - q.bid) / 2 / q.M
        q['hs'] = hs; q['hs_b'] = pd.cut(hs, HB, right=False, labels=HL).astype(str)
        q['vol_b'] = pd.cut(q.v.fillna(0), VB, labels=VL).astype(str)
        q['ygrp'] = ygrp(q.date.dt.year.iloc[0])
        tr1 = q.v1.fillna(0) > 0; tr0 = q.v.fillna(0) > 0
        q['tr1'] = tr1
        q['fs'] = tr1 & (q.high1 >= q.M); q['fs_x'] = tr1 & (q.high1 > q.M)
        q['fl'] = tr1 & (q.low1 <= q.M); q['fl_x'] = tr1 & (q.low1 < q.M)
        q['s0'] = tr0 & (q.high > q.M); q['l0'] = tr0 & (q.low < q.M); q['tr0'] = tr0
        ok1 = (q.bid1 > 0) & (q.ask1 >= q.bid1)
        mid1 = ((q.bid1 + q.ask1) / 2).where(ok1)
        q['ok1'] = mid1.notna()
        mo_s = ((q.M - mid1) / q.M).clip(-5, 5).fillna(0)
        q['mo_all'] = mo_s.where(q.ok1, 0)                          # short-signed, over all with a t+1 mid
        # the FILL PRICE: a resting limit gets the open when the option gaps through M overnight
        o1 = q.open1.where(q.open1 > 0, q.M)
        fps = np.maximum(q.M, o1); fpl = np.minimum(q.M, o1)
        q['gs'] = q.fs & (o1 >= q.M); q['gl'] = q.fl & (o1 <= q.M)   # filled at the open (gap)
        q['imp_s'] = ((fps - q.M) / q.M).where(q.fs, 0); q['imp_l'] = ((q.M - fpl) / q.M).where(q.fl, 0)
        mo_s = ((fps - mid1) / q.M).clip(-5, 5).fillna(0); mo_l = ((mid1 - fpl) / q.M).clip(-5, 5).fillna(0)
        q['mo_fs'] = mo_s.where(q.fs & q.ok1, 0); q['n_fs'] = q.fs & q.ok1
        q['mo_fl'] = mo_l.where(q.fl & q.ok1, 0); q['n_fl'] = q.fl & q.ok1
        q['mo_fs2'] = (mo_s ** 2).where(q.fs & q.ok1, 0); q['mo_fl2'] = (mo_l ** 2).where(q.fl & q.ok1, 0)
        cols = ['tr1', 'fs', 'fs_x', 'fl', 'fl_x', 'tr0', 's0', 'l0', 'ok1', 'mo_all', 'mo_fs', 'n_fs', 'mo_fl', 'n_fl',
                'mo_fs2', 'mo_fl2', 'hs', 'gs', 'gl', 'imp_s', 'imp_l']
        q[cols] = q[cols].astype(float)
        g = q.groupby(KEYS)[cols].sum(); g['n'] = q.groupby(KEYS).size()
        out.append(g.reset_index())
        del q
    if not out: return []
    return [pd.concat(out).groupby(KEYS).sum().reset_index()]


if __name__ == '__main__':
    os.makedirs(a.out, exist_ok=True)
    R = []
    with Pool(a.workers, maxtasksperchild=20) as pool:
        for i, r in enumerate(pool.imap_unordered(run, roots), 1):
            R += r
            if i % 50 == 0 or i == len(roots): print(f'{i}/{len(roots)} roots  {time.time() - t0_:.0f}s', flush=True)
    R = pd.concat(R).groupby(KEYS).sum().reset_index()
    R.to_parquet(f'{a.out}/fills.parquet', index=False)
    print(f'wrote {len(R):,} rows  {time.time() - t0_:.0f}s')
