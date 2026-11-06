#!/usr/bin/env python3
"""SpringFlyer S18 — could a RESTING SELL LIMIT near the ask fill? (user 2026-11-06: "For stocks that meet the criteria, how
realistic would it be to get a fill close to the ask?" — the fill assumption behind S13/S16/S17's "bid + x %").
S13's 147 chain trades, the ATM call at its expiry (call_sale.parquet: the strike nearest D's raw close, the first expiry on or
after the 10-day exit). Every option trade on D with the NBBO in force when it printed (history/trade_quote, STANDARD tick
data). For each print: pos = (price − bid) / (ask − bid) — 0 at the bid, 1 at the ask (a crossed / locked quote: dropped).
A sell limit resting at spread level x can only fill when a buyer prints at pos >= x (an UPPER bound: no queue priority, any
venue). The would-be size N = the contracts for ~$5k of the stock (max(1, round(5000 / (100 x close)))).
Windows: the whole day, the last 60 minutes (15:00–16:00) and the last 30.
  fill rate(x, window)   the share of trades whose prints at pos >= x in the window sum to >= N contracts
  realised level          the VWAP of those prints in units of D's CLOSING quote: (vwap − bid_close) / (ask_close − bid_close)
                          — S16's "bid + f" scale (it moves with the stock: a print before the close at a then-ask can
                          sit above or below the closing quote)
Needs the Theta Terminal running (127.0.0.1:25503).
    cd research && python3 -u scripts/equity/springflyer_option_fills.py
writes data/equity/springflyer/option_fills.parquet (one row per trade: the aggregates)
"""
import concurrent.futures as cf, json, urllib.error, urllib.parse, urllib.request
import numpy as np, pandas as pd

BASE = 'http://127.0.0.1:25503/v3/option'
u = pd.read_parquet('data/equity/springflyer/call_sale.parquet')
for c in ('date', 'expiration'):
    u[c] = pd.to_datetime(u[c])
u = u[u.k_atm.notna()].reset_index(drop=True)
u['N'] = np.maximum(1, np.round(5000 / (100 * u.close))).astype(int)
F = lambda d: d.strftime('%Y%m%d')


W = {'the whole day': 9 * 3600 + 30 * 60, 'the last 60 min': 15 * 3600, 'the last 30 min': 15 * 3600 + 30 * 60}
X = (0.5, 0.75, 0.9, 1.0)
BINS = [(-np.inf, 0, 'le_bid'), (0, .5, 'b0_50'), (.5, .75, 'b50_75'), (.75, 1, 'b75_100'), (1, np.inf, 'ge_ask')]


def agg(r):
    """one trade's prints on D, aggregated in the worker (2026-11-06: holding every print OOM-ed at 3 GB — GME / AMC days)"""
    q = dict(symbol=r.ticker, expiration=F(r.expiration), strike=f"{r.k_atm:.3f}", right='call', start_date=F(r.date),
             end_date=F(r.date), format='ndjson')
    o = dict(ticker=r.ticker, date=r.date)
    try:
        with urllib.request.urlopen(f"{BASE}/history/trade_quote?" + urllib.parse.urlencode(q), timeout=300) as resp:
            body = resp.read().decode()
    except urllib.error.HTTPError:
        return o
    ts, px, sz, bid, ask = [], [], [], [], []
    for l in body.splitlines():
        if not l.strip():
            continue
        j = json.loads(l); t = j['trade_timestamp']
        ts.append(int(t[11:13]) * 3600 + int(t[14:16]) * 60 + int(t[17:19])); px.append(j['price']); sz.append(j['size'])
        bid.append(j['bid']); ask.append(j['ask'])
    del body
    if not ts:
        return o
    sec, px, sz, bid, ask = (np.asarray(v, float) for v in (ts, px, sz, bid, ask))
    k = (sec >= 9 * 3600 + 30 * 60) & (sec < 16 * 3600) & (ask > bid) & (bid > 0)
    sec, px, sz, bid, ask = sec[k], px[k], sz[k], bid[k], ask[k]
    pos = (px - bid) / (ask - bid)
    o['prints'], o['contracts'] = len(sz), sz.sum()
    for lab, s0 in W.items():
        w = sec >= s0; o[f'{lab}|contracts'] = sz[w].sum()
        for lo, hi, b in BINS:
            o[f'{lab}|{b}'] = sz[w & (pos > lo) & (pos <= hi)].sum() if b != 'le_bid' else sz[w & (pos <= 0)].sum()
        o[f'{lab}|ge_ask'] = sz[w & (pos >= 1)].sum(); o[f'{lab}|b75_100'] = sz[w & (pos >= .75) & (pos < 1)].sum()
        for x in X:
            m = w & (pos >= x); o[f'{lab}|v{x}'] = sz[m].sum(); o[f'{lab}|pv{x}'] = (px[m] * sz[m]).sum()
    return o


with cf.ThreadPoolExecutor(4) as ex:
    A = pd.DataFrame(list(ex.map(agg, u.itertuples())))
d = u[['ticker', 'date', 'close', 'N', 'atm_bid', 'atm_ask']].merge(A, on=['ticker', 'date'], how='left').fillna(0)
d.to_parquet('data/equity/springflyer/option_fills.parquet', index=False)
print(f"trades {len(d)}; with any RTH print on D's ATM contract {int((d.contracts > 0).sum())}; would-be size N: median {d.N.median():.0f} "
      f"contracts (p10 {d.N.quantile(.1):.0f}, p90 {d.N.quantile(.9):.0f}); contracts traded on D: median {d.contracts.median():.0f}, "
      f"p25 {d.contracts.quantile(.25):.0f}, p75 {d.contracts.quantile(.75):.0f}")
# ⚠ IDEX 2020-11-24: 8.8M contracts in 985k prints on a $1-2 stock's ATM call, 99.9 % at the bid — a data artifact; out of the POOL
ART = d.prints > 1e5
print(f"artifacts out of the pooled table (> 100k prints in a day): {', '.join(d[ART].ticker + ' ' + d[ART].date.dt.strftime('%Y-%m-%d'))}")
print("\n### where the prints sit in the spread — pooled (contract-weighted) / per-trade median share\n")
print("| window | contracts | at or below the bid | 0-0.5 | 0.5-0.75 | 0.75-1 | at or above the ask |\n|---|---|---|---|---|---|---|")
for lab in W:
    q = d[~ART]; tot = q[f'{lab}|contracts'].sum()
    sh = lambda b: f"{q[f'{lab}|{b}'].sum() / tot * 100:.0f} / {(q[f'{lab}|{b}'] / q[f'{lab}|contracts'].where(q[f'{lab}|contracts'] > 0)).median() * 100:.0f} %"
    print(f"| {lab} | {tot:,.0f} | {sh('le_bid')} | {sh('b0_50')} | {sh('b50_75')} | {sh('b75_100')} | {sh('ge_ask')} |")
print("\n### FILL RATE: the share of the 147 trades in which buyers printed >= N contracts at or above spread level x\n")
print("| window | x = 0.5 (the mid) | x = 0.75 (bid + 75 %) | x = 0.9 | x = 1.0 (the ask) |\n|---|---|---|---|---|")
for lab in W:
    print(f"| {lab} | " + " | ".join(f"{(d[f'{lab}|v{x}'] >= d.N).mean() * 100:.0f} %" for x in X) + " |")
print("\n### the REALISED level of those prints in units of D's closing spread ((vwap − bid_close) / (ask_close − bid_close)), median over the trades that had them\n")
print("| window | prints at pos >= 0.5 | >= 0.75 | >= 1.0 |\n|---|---|---|---|")
for lab in W:
    cells = []
    for x in (0.5, 0.75, 1.0):
        m = d[(d[f'{lab}|v{x}'] > 0) & (d.atm_ask > d.atm_bid)]
        lvl = (m[f'{lab}|pv{x}'] / m[f'{lab}|v{x}'] - m.atm_bid) / (m.atm_ask - m.atm_bid)
        cells.append(f"{lvl.median():.2f} (n {len(m)})")
    print(f"| {lab} | " + " | ".join(cells) + " |")
d['band'] = pd.cut(d.close, [0, 5, 10, 20, 50, 1e9], labels=['< $5', '$5-10', '$10-20', '$20-50', '$50+'])
P = d.rename(columns={'the last 60 min|v0.75': 'v'})
print("\n### by the stock's price at D's close: the last 60 min, fill rate at x = 0.75")
m = P
print(m.groupby('band', observed=True)[['v', 'N', 'contracts']].apply(lambda z: pd.Series({'trades': len(z), 'fill %': (z.v >= z.N).mean() * 100,
      'median contracts on D': z.contracts.median(), 'median near-ask contracts, last hour': z.v.median()})).round(1).to_string())
