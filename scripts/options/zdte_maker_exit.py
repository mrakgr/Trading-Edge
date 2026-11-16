#!/usr/bin/env python3
"""MAKER EXIT BEFORE THE BELL (user 2026-11-16): buy the 0DTE short back with a resting order AT THE BID from 15:55,
re-pegged to the current bid each minute (15:55..15:59); filled when that minute's trade bar prints at the bid or lower
('touch') / strictly lower ('through', conservative: a queue ahead of us); not filled by 15:59 -> HOLD with the 15:59
hedge carried (SPXW: the 16:00 cash close; ETFs / stocks: the 17:30 price). A contract with no bid is left to expire.
Per contract (root, day, strike, right) - every entry of that contract shares the exit. P&L per trade (minute-hedged to
15:59 - the few minutes between a fill and 15:59 keep the hedge: a small approximation), % of the entry mid, sold at the bid.
    cd research && python3 scripts/options/zdte_maker_exit.py > logs/options_zdte_maker_exit.log
"""
import glob, os
import numpy as np, pandas as pd
from multiprocessing import Pool
pd.set_option('display.width', 250)
ROOTS = ['SPY', 'QQQ', 'SPXW', 'IWM', 'TSLA', 'NVDA']
SRC = 'data/options/intraday_0dte'


def exits(job):
    root, d = job
    date = f'{d[:4]}-{d[4:6]}-{d[6:]}'
    q = pd.read_parquet(f'{SRC}/{root}__{d}.quote.parquet', columns=['expiration', 'strike', 'right', 'timestamp', 'bid', 'ask'])
    q = q[q.expiration == date]
    ts = pd.to_datetime(q.timestamp); q['m'] = ts.dt.hour * 60 + ts.dt.minute
    q = q[(q.m >= 955) & (q.m <= 959)]
    o = pd.read_parquet(f'{SRC}/{root}__{d}.ohlc.parquet', columns=['expiration', 'strike', 'right', 'timestamp', 'low', 'volume'])
    o = o[(o.expiration == date) & (o.volume > 0)]
    ts = pd.to_datetime(o.timestamp); o['m'] = ts.dt.hour * 60 + ts.dt.minute
    o = o[(o.m >= 955) & (o.m <= 959)]
    x = q.merge(o[['strike', 'right', 'm', 'low']], on=['strike', 'right', 'm'], how='left').sort_values('m')
    x['touch'] = (x.bid > 0) & (x.low <= x.bid); x['thru'] = (x.bid > 0) & (x.low < x.bid)
    out = []
    for (k, r), g in x.groupby(['strike', 'right']):
        row = {'root': root, 'date': pd.Timestamp(date), 'strike': k, 'right': r, 'nobid': bool((g.bid <= 0).all())}
        for v in ('touch', 'thru'):
            f = g[g[v]]
            row[f'fill_{v}'] = float(f.bid.iloc[0]) if len(f) else np.nan
            row[f'm_{v}'] = int(f.m.iloc[0]) if len(f) else -1
        out.append(row)
    return pd.DataFrame(out)


if __name__ == '__main__':
    jobs = [(os.path.basename(f).split('__')[0], os.path.basename(f).split('__')[1][:8]) for f in glob.glob(f'{SRC}/*.quote.parquet')]
    jobs = [j for j in jobs if j[0] in ROOTS]
    with Pool(6) as p: E = pd.concat(p.map(exits, jobs, chunksize=8), ignore_index=True)
    T = pd.concat([pd.read_parquet(f'data/options/zdte/trades/{r}.parquet') for r in ROOTS], ignore_index=True)
    T = T[T.inst.isin(['P25', 'C25'])]
    T = T.merge(E, on=['root', 'date', 'strike', 'right'], how='left')
    T['mid'] = (T.bid + T.ask) / 2; T = T[T.mid >= .05].copy()
    CPS = np.where(T.root == 'SPXW', 0.06, 0.006); T['hcost'] = T.turn * CPS
    s17 = np.where(T.root == 'SPXW', T.close, T.s1730)
    settle_h = np.where(T.right == 'C', np.maximum(s17 - T.strike, 0), np.maximum(T.strike - s17, 0))
    hold = (T.bid - settle_h + T.hd + T.dlast.fillna(0) * (s17 - T.close) - T.hcost) / T.mid       # held, hedge carried
    T['ask_x'] = T.ask_x.fillna(0); T['bid_x'] = T.bid_x.fillna(0)
    P = {'hold all (hedge carried)': hold,
         'buy back at the 15:59 ask': (T.bid - T.ask_x + T.hd_x - T.hcost) / T.mid,
         'buy back at the 15:59 mid': (T.bid - (T.ask_x + T.bid_x) / 2 + T.hd_x - T.hcost) / T.mid}
    for v in ('touch', 'thru'):
        filled = T[f'fill_{v}'].notna()
        P[f'maker at bid ({v}), else hold'] = np.where(filled, (T.bid - T[f'fill_{v}'] + T.hd_x - T.hcost) / T.mid, hold)
    for k, v in P.items(): T[k] = v

    def st(x, c):
        v = x[c]; dm = v.groupby(x.date).mean(); w, l = v[v > 0].sum(), -v[v < 0].sum()
        return f'{100 * v.mean():6.1f}% PF {w / l:4.2f} t {dm.mean() / dm.std() * np.sqrt(len(dm)):5.1f}'
    print('% of the entry mid | PF | day-clustered t (25-delta, minute-hedged, sold at the bid)')
    for (r, i), x in T.groupby(['root', 'inst']):
        c = x.drop_duplicates(['date', 'strike', 'right'])
        print(f'\n{r} {i}: n {len(x):,} trades on {len(c):,} contracts; contracts with a bid at 15:55-59 {100 * (~c.nobid).mean():.0f}%; '
              f'maker filled: touch {100 * c.fill_touch.notna().mean():.0f}%, through {100 * c.fill_thru.notna().mean():.0f}% '
              f'(of those with a bid: {100 * c[~c.nobid].fill_touch.notna().mean():.0f}% / {100 * c[~c.nobid].fill_thru.notna().mean():.0f}%)')
        for k in P: print(f'   {k:34s} {st(x, k)}')
