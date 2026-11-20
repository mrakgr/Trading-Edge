#!/usr/bin/env python3
"""Instruments with 0DTE / weekly options whose ONE contract is under a notional cap (user 2026-11-19: SPY ~$75k per
contract is too big to start; < $30k per contract, positions ~$10k). From market_full EOD 2026Q3 (2026-07-01..09-30):
  S        the underlying from put-call parity on the nearest expiry, last day (median K + C - P over the 6 strikes nearest
           the C = P crossing) - works for indexes (XSP, XND, MRUT) as well; x 100 = notional per contract
  d0       share of trading days with an expiry THAT day (0DTE);  wk = share of days with an expiry within 7 calendar days
  vol      mean daily option contracts (all expiries); vol0 = in the 0DTE expiry
  hs_atm   median half-spread of the ATM options (|K/S-1| < 1%) with 1-7 DTE, % of mid
Kept: S x 100 <= --cap, wk >= .9, vol >= --minvol. Writes data/options/small_notional.csv.
    cd research && python3 scripts/options/small_notional.py > logs/options_small_notional.log
"""
import argparse, glob
import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 220); pd.set_option('display.max_rows', 300)
ap = argparse.ArgumentParser()
ap.add_argument('--cap', type=float, default=30000); ap.add_argument('--minvol', type=float, default=5000)
a = ap.parse_args()
con = duckdb.connect(); con.sql("set memory_limit='6GB'; set threads=8")
files = glob.glob('data/options/market_full/*__20260701.parquet')
con.sql(f"""create table x as select root, cast(date as date) d, cast(expiration as date) e, strike K, "right" r, bid, ask, volume v
            from read_parquet({files}) where cast(date as date) between '2026-07-01' and '2026-09-30'""")
G = con.sql("""select root, count(distinct d) nd,
                 count(distinct case when e = d then d end) nd0,
                 sum(v) / count(distinct d) vol, sum(case when e = d then v else 0 end) / count(distinct d) vol0
               from x group by root""").df()
W = con.sql("""select root, count(distinct d) ndw from (select distinct root, d from x where e between d and d + 7) group by root""").df()
G = G.merge(W, on='root', how='left')
G['d0'] = G.nd0 / G.nd; G['wk'] = G.ndw.fillna(0) / G.nd
G = G[(G.wk >= .9) & (G.vol >= a.minvol)]
print(f'{len(G)} roots with weeklies and >= {a.minvol:,.0f} contracts/day', flush=True)
roots = ','.join(repr(r) for r in G.root)
# parity price on the last day, nearest expiry >= that day
P = con.sql(f"""with last as (select root, max(d) d from x where root in ({roots}) group by root),
                 y as (select x.* from x join last using (root, d)),
                 ne as (select root, min(e) e from y where bid > 0 and ask >= bid group by root),
                 z as (select y.root, y.K, y.r, (y.bid + y.ask) / 2 mid from y join ne using (root, e) where y.bid > 0 and y.ask >= y.bid)
               select root, K, max(case when r = 'C' then mid end) c, max(case when r = 'P' then mid end) p from z group by root, K""").df().dropna()
P['F'] = P.K + P.c - P.p
S = {}
for r, g in P.groupby('root'):
    k0 = g.loc[(g.c - g.p).abs().idxmin(), 'K']
    S[r] = float(np.median(g.iloc[(g.K - k0).abs().argsort()[:6]].F))
G['S'] = G.root.map(S); G['notional'] = G.S * 100
# ATM half-spread, 1-7 DTE
H = con.sql(f"""select root, median((ask - bid) / 2 / ((ask + bid) / 2)) hs from x
               where root in ({roots}) and bid > 0 and ask >= bid and e - d between 1 and 7 group by root, K, d, e""").df()
Sx = pd.Series(S)
H2 = con.sql(f"""select root, d, e, K, (ask - bid) / 2 / ((ask + bid) / 2) hs from x
               where root in ({roots}) and bid > 0 and ask >= bid and e - d between 1 and 7""").df()
H2['S'] = H2.root.map(Sx)
G['hs_atm'] = G.root.map(H2[(H2.K / H2.S - 1).abs() < .01].groupby('root').hs.median())
K = G[G.notional <= a.cap].sort_values('vol', ascending=False)
K.to_csv('data/options/small_notional.csv', index=False)
print(K[['root', 'S', 'notional', 'd0', 'wk', 'vol', 'vol0', 'hs_atm']].round({'S': 2, 'notional': 0, 'd0': 2, 'wk': 2, 'vol': 0, 'vol0': 0, 'hs_atm': 4}).to_string(index=False))
print('\nabove the cap, for reference:')
print(G[G.notional > a.cap].sort_values('vol', ascending=False).head(15)[['root', 'S', 'notional', 'd0', 'vol', 'vol0', 'hs_atm']].round(2).to_string(index=False))
