#!/usr/bin/env python3
"""RATCHET ENTRY for the 0DTE shorts (user 2026-11-20): instead of hitting the bid, OFFER at the ask and ratchet down one
tick every 5 s; data: 1-second NBBO (intraday_1s, q1s) + every trade print (intraday_trades).
Per sampled entry (every 15 min, 09:45..15:45; the 25d put / call of zdte_trades.py at that minute):
  start  price = ask(t0), JOINING the ask: queue ahead = the ask size at t0
  fill   in a 5-s window: a single-leg print (condition 18 auto-execution / 95 ISO; auctions 125/126 and multi-leg
         130/131/134/138 excluded) ABOVE our price; or AT our price once the cumulative printed size exceeds the queue ahead
         (improved inside the spread: queue 0); or the bid reaching our price
  ratchet after an unfilled window: price = min(price - tick, ask now); == ask -> join (queue = ask size), below -> improved;
         at / below the bid -> SELL AT THE BID then (the fallback cross)
  tick   $0.01 (SPY, QQQ; TSLA $0.01 under $3, $0.05 above)
Advantage vs selling at the bid at t0, per share: (fill - bid0) - delta x (S_fill - S0): once both have sold and hedged
the positions are the same; the immediate seller bought its hedge (delta shares) at S0, the ratchet at S_fill. In % of mid0.
--mode chase: the offer follows the ask down (price = min(price - tick, ask now)); hold: one tick a step, never chasing.
    cd research && python3 scripts/options/ratchet_sim.py [--roots SPY] [--days N] > logs/options_ratchet_sim.log
"""
import argparse, glob, os
import duckdb, numpy as np, pandas as pd
from multiprocessing import Pool
ap = argparse.ArgumentParser()
ap.add_argument('--roots', default='SPY,QQQ,TSLA'); ap.add_argument('--days', type=int, default=0)
ap.add_argument('--step', type=int, default=5); ap.add_argument('--every', type=int, default=15)
ap.add_argument('--out', default='data/options/zdte/ratchet.parquet')
ap.add_argument('--mode', default='chase', choices=['chase', 'hold'])
a = ap.parse_args()
GOOD = {18, 95}


def spot(ticker, date):
    f = f'data/intraday_1s_slim/{date}.parquet'
    x = duckdb.sql(f"select bucket, vwap from '{f}' where ticker = '{ticker}' and bucket between 34200 and 57600 order by bucket").df()
    med = x.groupby(x.bucket // 60).vwap.transform('median'); x = x[(x.vwap / med - 1).abs() < 0.003]
    return x.set_index('bucket').vwap.reindex(np.arange(34200, 57601)).ffill().bfill().values


def run(job):
    root, d = job
    date = f'{d[:4]}-{d[4:6]}-{d[6:]}'
    fq, ft = f'data/options/intraday_1s/{root}__{d}.q1s.parquet', f'data/options/intraday_trades/{root}__{d}.trades.parquet'
    if not (os.path.exists(fq) and os.path.exists(ft)): return None
    Q = pd.read_parquet(fq); TR = pd.read_parquet(ft)
    TR = TR[TR.condition.isin(GOOD)]
    S = spot(root, date)
    E = ENT[(ENT.root == root) & (ENT.date == pd.Timestamp(date))]
    out = []
    for (k, r), e in E.groupby(['strike', 'right']):
        q = Q[(Q.strike == np.float32(k)) & (Q.right == r)].set_index('sec').reindex(np.arange(34200, 57601)).ffill()
        bid, ask, asz = q.bid.values, q.ask.values, q.ask_size.values
        t = TR[(TR.strike == np.float32(k)) & (TR.right == r)].sort_values('ms')
        tms, tpx, tsz = t.ms.values, t.price.values.astype(float), t['size'].values
        for _, row in e.iterrows():
            t0 = int(row.m) * 60; i0 = t0 - 34200
            b0, a0 = bid[i0], ask[i0]
            if not (b0 > 0 and a0 >= b0): continue
            m0 = (b0 + a0) / 2
            tick = 0.05 if (root == 'TSLA' and a0 >= 3) else 0.01
            px, queue = float(a0), float(asz[i0]); cum = 0.0; ts = t0; kind = None; fill_t = None
            while ts < 57600:
                lo, hi = np.searchsorted(tms, ts * 1000), np.searchsorted(tms, (ts + a.step) * 1000)
                for j in range(lo, hi):
                    p = tpx[j]
                    if p > px + 1e-9: kind, fill_t = 'through', tms[j] // 1000; break
                    if abs(p - px) < 1e-9:
                        cum += tsz[j]
                        if cum > queue: kind, fill_t = ('at' if queue > 0 else 'improved'), tms[j] // 1000; break
                if kind is None:
                    ib = np.searchsorted(np.arange(ts, ts + a.step), 0)    # placeholder for clarity
                    sl = slice(ts - 34200, min(ts + a.step, 57600) - 34200)
                    hit = np.nonzero(bid[sl] >= px - 1e-9)[0]
                    if len(hit): kind, fill_t = 'bid_crossed', ts + int(hit[0])
                if kind: break
                ts += a.step
                if ts >= 57600: break
                ia = ts - 34200
                new = round(min(px - tick, ask[ia]) if a.mode == 'chase' else px - tick, 2)
                if new <= bid[ia] + 1e-9:
                    kind, fill_t, px = 'cross', ts, float(bid[ia]); break
                if abs(new - ask[ia]) < 1e-9 and new != px: queue = float(asz[ia]); cum = 0.0
                elif new < ask[ia]: queue = 0.0; cum = 0.0
                else: queue = float('inf'); cum = 0.0                  # above the ask (hold mode): not the best offer
                px = new
            if kind is None: continue
            fi = int(min(fill_t, 57600)) - 34200
            adv = (px - b0) - row.dl * (S[fi] - S[i0])
            out.append({'root': root, 'date': date, 'm': row.m, 'inst': row.inst, 'mid0': m0, 'hs0': (a0 - b0) / 2 / m0,
                        'kind': kind, 'wait': int(fill_t) - t0, 'fill_vs_bid': (px - b0) / m0, 'adv': adv / m0})
    return pd.DataFrame(out)


if __name__ == '__main__':
    ENT = []
    for r in a.roots.split(','):
        T = pd.read_parquet(f'data/options/zdte/trades/{r}.parquet', columns=['root', 'date', 'm', 'inst', 'strike', 'right', 'dl'])
        T = T[T.inst.isin(['P25', 'C25']) & (T.m % a.every == 0) & (T.m >= 585) & (T.m <= 945)]
        ENT.append(T)
    ENT = pd.concat(ENT); ENT['date'] = pd.to_datetime(ENT.date)
    jobs = sorted({(os.path.basename(f).split('__')[0], os.path.basename(f).split('__')[1][:8]) for f in glob.glob('data/options/intraday_trades/*.trades.parquet')})
    jobs = [j for j in jobs if j[0] in a.roots.split(',')]
    if a.days: jobs = jobs[-a.days * 3:]
    with Pool(6, initializer=None) as p: R = [x for x in p.map(run, jobs) if x is not None and len(x)]
    R = pd.concat(R, ignore_index=True); R.to_parquet(a.out, index=False)
    print(f'{len(R):,} simulated entries on {R.groupby(["root", "date"]).ngroups} root-days')
    g = R.groupby(['root', 'inst'])
    print(pd.DataFrame({'n': g.size(), 'half-spread %': 100 * g.hs0.mean(), 'fill vs bid %': 100 * g.fill_vs_bid.mean(),
                        'ADVANTAGE % (mean)': 100 * g.adv.mean(), 'adv median %': 100 * g.adv.median(),
                        'wait s (median)': g.wait.median(), 'filled passively %': 100 * g.kind.apply(lambda s: (s != 'cross').mean())}).round(2).to_string())
    print(R.groupby(['root', 'kind']).adv.agg(['size', 'mean']).assign(mean=lambda x: 100 * x['mean']).round(2).to_string())
