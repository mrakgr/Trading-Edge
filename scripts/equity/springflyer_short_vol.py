#!/usr/bin/env python3
"""SpringFlyer S16 — SHORT VOLATILITY held to expiry: the short ATM call and the short ATM straddle (user 2026-11-06,
after S14 found the climax's options overpriced: the straddle implied a 40.9% move, the realised was 28.9%).

S13's 147 trades and contracts (call_sale.parquet: the strike nearest D's raw close at the first expiry on or after the
10-day exit, median 24 dte); the put at the SAME strike and expiry fetched here (one EOD request per trade). Entered at D's
close, held to EXPIRY, settled at intrinsic against the stock's raw close on the expiration day (no exit spread; no
split in any hold, S13). Three entry fills for each SOLD leg: the bid; the bid + 25% of the spread; the mid.
Stub-quote filter (S14): a call ask above the stock, a put ask above the strike, or a spread wider than 50% of the mid on
either leg. P&L per share in bp of D's close; the stock short to the same expiration day beside it (no borrow fee).

    cd research && python3 -u scripts/equity/springflyer_short_vol.py
writes data/equity/springflyer/short_vol.parquet
"""
import concurrent.futures as cf, json, urllib.error, urllib.parse, urllib.request
import numpy as np, pandas as pd

BASE = 'http://127.0.0.1:25503/v3/option'
u = pd.read_parquet('data/equity/springflyer/call_sale.parquet')
for c in ('date', 'expiration'):
    u[c] = pd.to_datetime(u[c])
u = u[u.k_atm.notna()].reset_index(drop=True)


def put_quote(r):
    q = dict(symbol=r.ticker, expiration=r.expiration.strftime('%Y%m%d'), strike=f"{r.k_atm:.3f}", right='put',
             start_date=r.date.strftime('%Y%m%d'), end_date=r.date.strftime('%Y%m%d'), format='ndjson')
    try:
        with urllib.request.urlopen(f"{BASE}/history/eod?" + urllib.parse.urlencode(q), timeout=120) as resp:
            rows = [json.loads(l) for l in resp.read().decode().splitlines() if l.strip()]
    except urllib.error.HTTPError:
        rows = []
    return (rows[-1]['bid'], rows[-1]['ask']) if rows else (np.nan, np.nan)


with cf.ThreadPoolExecutor(4) as ex:
    pq = list(ex.map(put_quote, u.itertuples()))
u['p_bid'], u['p_ask'] = [x[0] for x in pq], [x[1] for x in pq]
C, K, SX = u.close, u.k_atm, u.close_expiry
call_int, put_int = np.maximum(0, SX - K), np.maximum(0, K - SX)
FILLS = {'bid': 0.0, 'bid+25%': 0.25, 'mid': 0.5}
for lab, f in FILLS.items():
    cs = u.atm_bid + f * (u.atm_ask - u.atm_bid); ps = u.p_bid + f * (u.p_ask - u.p_bid)
    u[f'call_{lab}'] = (cs - call_int) / C * 1e4
    u[f'straddle_{lab}'] = (cs + ps - call_int - put_int) / C * 1e4
u['stock_exp'] = (C - SX) / C * 1e4
mid = lambda b, a: (b + a) / 2
u['stub'] = ((u.atm_ask > C) | (u.p_ask > K) | ((u.atm_ask - u.atm_bid) / mid(u.atm_bid, u.atm_ask) > 0.5)
             | ((u.p_ask - u.p_bid) / mid(u.p_bid, u.p_ask) > 0.5) | u.p_bid.isna())
u['range_gt3'] = u.ticker.isna()          # filled below
sig = pd.read_parquet('data/equity/springflyer/signals_spec_v2.parquet')[['ticker', 'date', 'rng20c_d']]
sig['date'] = pd.to_datetime(sig.date)
u = u.drop(columns='range_gt3').merge(sig, on=['ticker', 'date'], how='left')
u.to_parquet('data/equity/springflyer/short_vol.parquet', index=False)


def pf(r):
    r = np.asarray(r, float); r = r[~np.isnan(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum()
    return g / l if l > 0 else float('inf')


print(f"trades {len(u)}; put quote found {int(u.p_bid.notna().sum())}; stub/wide quotes {int(u.stub.sum())}; median dte {u.dte.median():.0f}; "
      f"median straddle credit at the bid {((u.atm_bid + u.p_bid) / C * 100).median():.1f}% of the stock, at the mid "
      f"{((mid(u.atm_bid, u.atm_ask) + mid(u.p_bid, u.p_ask)) / C * 100).median():.1f}%")
for title, sel in (('all trades', u.p_bid.notna()), ('clean quotes only', ~u.stub), ('clean quotes, range > 3x (S15)', ~u.stub & (u.rng20c_d > 3))):
    x0 = u[sel]
    print(f"\n### {title} ({len(x0)})\n\n| structure, held to expiry | win% | mean bp | median bp | PF | worst bp | p5 bp |\n|---|---|---|---|---|---|---|")
    for lab, col in [('stock short to the expiry day', 'stock_exp')] + [(f'short ATM call, sold at the {k}', f'call_{k}') for k in FILLS] \
            + [(f'short ATM straddle, sold at the {k}', f'straddle_{k}') for k in FILLS]:
        x = x0[col].dropna().values
        print(f"| {lab} | {(x > 0).mean() * 100:.0f}% | {x.mean():+.0f} | {np.median(x):+.0f} | **{pf(x):.2f}** | {x.min():+.0f} | {np.percentile(x, 5):+.0f} |")
v = u[~u.stub].copy()
v['outcome'] = pd.cut(-v.stock_exp, [-1e9, -4000, -2000, 0, 2000, 4000, 1e9],
                      labels=['fell > 40%', 'fell 20-40%', 'fell 0-20%', 'rose 0-20%', 'rose 20-40%', 'rose > 40%'])
print("\n### clean quotes, by the stock's move to expiry (mean bp; fills at bid+25%)")
print(v.groupby('outcome', observed=True)[['stock_exp', 'call_bid+25%', 'straddle_bid+25%']].agg(['size', 'mean']).round(0)
      .loc[:, [('stock_exp', 'size'), ('stock_exp', 'mean'), ('call_bid+25%', 'mean'), ('straddle_bid+25%', 'mean')]].to_string())
