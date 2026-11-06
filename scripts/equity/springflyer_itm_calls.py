#!/usr/bin/env python3
"""SpringFlyer S17 — SOLD IN-THE-MONEY calls vs ATM (user 2026-11-06: "a test on how selling ITM calls on SpringFlyer
candidates would do. I wonder if those would be more profitable than ATM ones?").
S13's 147 chain trades and expiries (call_sale.parquet: the first expiry on or after the 10-day exit, median 24 dte). From D's
call chain at that expiry (one EOD request), the strike nearest D's raw close (ATM, as S13) and the strikes nearest 0.90 x and
0.80 x the close (both BELOW it: in the money); the exit day's chain for the 10-day buyback (one more).
  10-day exit  sold at D's close at the bid / bid + 25 % of the spread / the mid; bought back at the exit day's close at the
               ask / ask − 25 % / the mid (a contract expiring on or before the exit settles at intrinsic)
  to expiry    sold the same way; settled at intrinsic against the stock's raw close on its expiration day
Stub filter (S14): an ask above the stock, a bid below intrinsic − 5 % of the stock (a stale ITM quote), a spread wider than
50 % of the mid, or no quote — per strike. P&L per share in bp of D's close; the stock short beside it (to the 10-day exit
and to the expiry day; no borrow fee). Needs the Theta Terminal running (127.0.0.1:25503).
    cd research && python3 -u scripts/equity/springflyer_itm_calls.py
writes data/equity/springflyer/itm_calls.parquet
"""
import concurrent.futures as cf, json, urllib.error, urllib.parse, urllib.request
import numpy as np, pandas as pd

BASE = 'http://127.0.0.1:25503/v3/option'
u = pd.read_parquet('data/equity/springflyer/call_sale.parquet')
for c in ('date', 'expiration', 'exit_date_10'):
    u[c] = pd.to_datetime(u[c])
u = u[u.k_atm.notna()].reset_index(drop=True)
F = lambda d: d.strftime('%Y%m%d')


def chain(sym, exp, day):
    q = dict(symbol=sym, expiration=F(exp), right='call', start_date=F(day), end_date=F(day), format='ndjson')
    try:
        with urllib.request.urlopen(f"{BASE}/history/eod?" + urllib.parse.urlencode(q), timeout=120) as r:
            rows = [json.loads(l) for l in r.read().decode().splitlines() if l.strip()]
    except urllib.error.HTTPError:
        rows = []
    return pd.DataFrame(rows).groupby('strike')[['bid', 'ask']].last() if rows else None


STRIKES = {'atm': 1.00, 'itm10': 0.90, 'itm20': 0.80}


def sim(r):
    o = dict(ticker=r.ticker, date=r.date)
    e0 = chain(r.ticker, r.expiration, r.date)
    if e0 is None:
        return o
    expires_first = r.expiration <= r.exit_date_10
    e1 = None if expires_first else chain(r.ticker, r.expiration, r.exit_date_10)
    ks = e0.index.values
    for lab, m in STRIKES.items():
        cand = ks if lab == 'atm' else ks[ks < r.close]
        if len(cand) == 0:
            continue
        k = float(cand[np.argmin(np.abs(cand - m * r.close))])
        o[f'k_{lab}'] = k; o[f'{lab}_b0'], o[f'{lab}_a0'] = e0.loc[k, 'bid'], e0.loc[k, 'ask']
        if expires_first:
            o[f'{lab}_b1'] = o[f'{lab}_a1'] = max(0.0, r.close_exit - k)   # settled at the exit's close (on or after expiry)
        elif e1 is not None and k in e1.index:
            o[f'{lab}_b1'], o[f'{lab}_a1'] = e1.loc[k, 'bid'], e1.loc[k, 'ask']
    return o


with cf.ThreadPoolExecutor(4) as ex:
    res = pd.DataFrame(list(ex.map(sim, u.itertuples())))
d = u[['ticker', 'date', 'close', 'r10', 'close_exit', 'close_expiry', 'expiration', 'dte', 'exit_date_10']].merge(res, on=['ticker', 'date'])
C = d.close
FILL = {'bid': 0.0, 'bid+25%': 0.25, 'mid': 0.5}
for lab in STRIKES:
    K, b0, a0, b1, a1 = d[f'k_{lab}'], d[f'{lab}_b0'], d[f'{lab}_a0'], d[f'{lab}_b1'], d[f'{lab}_a1']
    for fl, f in FILL.items():
        sell = b0 + f * (a0 - b0); buy = a1 - f * (a1 - b1)
        d[f'{lab}_x_{fl}'] = (sell - buy) / C * 1e4
        d[f'{lab}_e_{fl}'] = (sell - np.maximum(0, d.close_expiry - K)) / C * 1e4
    mid0 = (b0 + a0) / 2
    d[f'{lab}_stub'] = (b0.isna() | (a0 > C) | ((a0 - b0) / mid0 > 0.5) | (mid0 <= 0) | b1.isna()
                        | (b0 < np.maximum(0, C - K) - 0.05 * C))
    d[f'{lab}_tv_pct'] = (mid0 - np.maximum(0, C - K)) / C * 100          # the time value at the mid, % of the stock
d['stock_x'] = -d.r10
d['stock_e'] = (C - d.close_expiry) / C * 1e4
d.to_parquet('data/equity/springflyer/itm_calls.parquet', index=False)


def pf(r):
    r = np.asarray(r, float); r = r[np.isfinite(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum()
    return g / l if l > 0 else float('inf')


def line(lab, x):
    x = np.asarray(x, float); x = x[np.isfinite(x)]
    return f"| {lab} | {len(x)} | {(x > 0).mean() * 100:.0f}% | {x.mean():+.0f} | {np.median(x):+.0f} | **{pf(x):.2f}** | {x.min():+.0f} | {np.percentile(x, 5):+.0f} |"


print(f"trades {len(d)}; strikes found: " + ", ".join(f"{lab} {int(d[f'k_{lab}'].notna().sum())}" for lab in STRIKES)
      + "; stub/missing: " + ", ".join(f"{lab} {int(d[f'{lab}_stub'].sum())}" for lab in STRIKES))
for lab in STRIKES:
    v = d[~d[f'{lab}_stub']]
    print(f"{lab}: strike a median {(v[f'k_{lab}'] / v.close).median() * 100:.0f} % of the close; premium at the bid {(v[f'{lab}_b0'] / v.close * 100).median():.1f} %"
          f" of the stock, of it time value {v[f'{lab}_tv_pct'].median():.1f} %; spread {((v[f'{lab}_a0'] - v[f'{lab}_b0']) / ((v[f'{lab}_a0'] + v[f'{lab}_b0']) / 2) * 100).median():.0f} % of the mid")
# the like-for-like set: every strike clean
both = d[~d.atm_stub & ~d.itm10_stub & ~d.itm20_stub]
for title, v in (("each strike on its own clean trades", None), (f"the trades clean at EVERY strike ({len(both)})", both)):
    print(f"\n## {title}\n\n| structure | n | win% | mean bp | median bp | PF | worst bp | p5 bp |\n|---|---|---|---|---|---|---|---|")
    vv = both if v is not None else d
    print(line("stock short, 10-day exit", vv.stock_x)); print(line("stock short, to the expiry day", vv.stock_e))
    for lab in STRIKES:
        w = vv if v is not None else d[~d[f'{lab}_stub']]
        for fl in ('bid', 'bid+25%', 'mid'):
            print(line(f"sold {lab} call, 10-day exit, {fl}", w[f'{lab}_x_{fl}']))
        for fl in ('bid', 'bid+25%', 'mid'):
            print(line(f"sold {lab} call, to expiry, {fl}", w[f'{lab}_e_{fl}']))
print("\n### the clean-at-every-strike trades, by the stock's move to expiry (mean bp; bid + 25 %, to expiry)")
both = both.assign(outcome=pd.cut(-both.stock_e, [-1e9, -4000, -2000, 0, 2000, 4000, 1e9],
                                  labels=['fell > 40%', 'fell 20-40%', 'fell 0-20%', 'rose 0-20%', 'rose 20-40%', 'rose > 40%']))
print(both.groupby('outcome', observed=True)[['stock_e', 'atm_e_bid+25%', 'itm10_e_bid+25%', 'itm20_e_bid+25%']].agg(['size', 'mean']).round(0)
      .loc[:, [('stock_e', 'size'), ('stock_e', 'mean'), ('atm_e_bid+25%', 'mean'), ('itm10_e_bid+25%', 'mean'), ('itm20_e_bid+25%', 'mean')]].to_string())
