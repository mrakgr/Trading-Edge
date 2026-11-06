#!/usr/bin/env python3
"""SpringFlyer S14 — the PUT DIAGONAL / BACKSPREAD (user 2026-11-06: "selling a short term ATM put and buying OTM puts
further out ... profit from both a decline in volatility and when the stock drops a lot, and only lose money if the
stock goes down a little. On parabolic moves the least likely outcome is that the stock will start to consolidate").

0. the user's premise, two ways: (a) the 10-day move's distribution over the whole book — is the middle thin?
   (b) on the chain trades, the realised |move| to the expiry against the ATM straddle's price (the market's expected
   |move|): a long-volatility structure needs realised > implied.
1. the structure on S11's 147 chain trades, entered at D's close, held to the 10-day exit date:
   SHORT 1 ATM put   the strike nearest D's raw close, the nearest expiry with dte >= 5; sold at the bid; it settles at
                     INTRINSIC on its expiry if that is on or before the exit date, else bought back at the exit's ask
   LONG N OTM puts   the strike nearest 0.80 x close (below the close), the first expiry with dte >= 30 (and after the
                     short's); bought at the ask, sold at the exit's bid
   N = 1, 2, 3 and the ZERO-COST ratio (short credit / long cost, capped at 5); the mid both ways as the bound.
   Net vega at entry per share = N * vega_long - vega_short (greeks/eod); each leg's IV change D -> exit.
P&L per share of the underlying, bp of D's close. Needs the Theta Terminal running (127.0.0.1:25503).

    cd research && python3 scripts/equity/springflyer_put_diagonal.py
writes data/equity/springflyer/put_diagonal.parquet
"""
import concurrent.futures as cf, json, os, urllib.error, urllib.parse, urllib.request
import duckdb, numpy as np, pandas as pd
import data_v2

BASE = 'http://127.0.0.1:25503/v3/option'
OUT = 'data/equity/springflyer/put_diagonal.parquet'


def get(path, **q):
    url = f"{BASE}/{path}?" + urllib.parse.urlencode({**q, 'format': 'ndjson'})
    try:
        with urllib.request.urlopen(url, timeout=180) as r:
            body = r.read().decode()
    except urllib.error.HTTPError as e:
        return [], f"HTTP {e.code}"
    try:
        return [json.loads(l) for l in body.splitlines() if l.strip()], None
    except json.JSONDecodeError:
        return [], body.strip()[:120]


def pf(r):
    r = np.asarray(r, float); r = r[~np.isnan(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum()
    return g / l if l > 0 else float('inf')


# ---- 0a. the premise on the whole book (local data)
b = pd.read_parquet('data/equity/springflyer/signals_spec_v2.parquet'); b = b[b.in_book10 & b.r10.notna()]
BINS = [-1e9, -4000, -2000, -1000, 0, 1000, 2000, 4000, 1e9]
LAB = ['fell > 40%', 'fell 20-40%', 'fell 10-20%', 'fell 0-10%', 'rose 0-10%', 'rose 10-20%', 'rose 20-40%', 'rose > 40%']
print(f"## 0a. the stock's 10-day move after the signal, the whole book ({len(b)} trades), share of trades")
dist = pd.DataFrame({'r10': pd.cut(-(-b.r10), BINS, labels=LAB).value_counts(normalize=True, sort=False) * 100,
                     'r5': pd.cut(b.r5, BINS, labels=LAB).value_counts(normalize=True, sort=False) * 100})
print(dist.round(1).to_string())
print(f"|move| < 10%: r10 {(b.r10.abs() < 1000).mean() * 100:.0f}%, r5 {(b.r5.abs() < 1000).mean() * 100:.0f}%")

# ---- the chain trades
cov = pd.read_parquet('data/equity/springflyer/option_coverage.parquet')
t = cov[cov.has_chain & cov.exit_date_10.notna()].copy()
for c in ('date', 'exit_date_10'):
    t[c] = pd.to_datetime(t[c])
t = t.reset_index(drop=True)
con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='4GB'")


def fetch(sym, day):
    """D's whole put chain, bid/ask only — the LIGHT EOD request (2026-11-06: greeks/eod for every expiry and strike was
    far too slow server-side; the greeks are fetched per chosen contract instead, greeks())."""
    d = day.strftime('%Y%m%d')
    rows, _ = get('history/eod', symbol=sym, expiration='*', right='put', start_date=d, end_date=d)
    if not rows:
        return None
    e = pd.DataFrame(rows); e['expiration'] = pd.to_datetime(e.expiration)
    return e[['expiration', 'strike', 'bid', 'ask']].groupby(['expiration', 'strike']).last()


def greeks(sym, exp, k, day):
    """(implied_vol, vega) of ONE contract at D's close, or nans."""
    d = day.strftime('%Y%m%d')
    rows, _ = get('history/greeks/eod', symbol=sym, expiration=exp.strftime('%Y%m%d'), strike=f"{k:.3f}", right='put',
                  start_date=d, end_date=d)
    return (rows[-1].get('implied_vol', np.nan), rows[-1].get('vega', np.nan)) if rows else (np.nan, np.nan)


DONE = [0]


def close_on(sym, day):
    v = con.cursor().execute("SELECT close FROM daily_episodes_causal WHERE ticker = ? AND date <= ? ORDER BY date DESC LIMIT 1",
                    [sym, day.date()]).fetchone()
    return v[0] if v else np.nan


def sim(r):
    try:
        return sim1(r)
    finally:
        DONE[0] += 1
        if DONE[0] % 10 == 0:
            print(f"  {DONE[0]} / {len(t)} trades", flush=True)


def sim1(r):
    o = dict(ticker=r.ticker, date=r.date)
    e0 = fetch(r.ticker, r.date)
    if e0 is None:
        return {**o, 'err': 'no D puts'}
    exps = sorted(e0.index.get_level_values(0).unique())
    dte = {x: (x - r.date).days for x in exps}
    s_exp = next((x for x in exps if dte[x] >= 5), None)
    l_exp = next((x for x in exps if dte[x] >= 30 and (s_exp is None or x > s_exp)), None)
    if s_exp is None or l_exp is None:
        return {**o, 'err': 'no expiries'}
    ks = e0.loc[s_exp].index.values; ksa = float(ks[np.argmin(np.abs(ks - r.close))])
    kl_all = e0.loc[l_exp].index.values; kl_all = kl_all[kl_all < r.close]
    if len(kl_all) == 0:
        return {**o, 'err': 'no OTM strike'}
    kl = float(kl_all[np.argmin(np.abs(kl_all - 0.8 * r.close))])
    S0, L0 = e0.loc[(s_exp, ksa)], e0.loc[(l_exp, kl)]
    e1 = fetch(r.ticker, r.exit_date_10)
    (s_iv0, s_vega0), (l_iv0, l_vega0) = greeks(r.ticker, s_exp, ksa, r.date), greeks(r.ticker, l_exp, kl, r.date)
    o.update(s_exp=s_exp, s_dte=dte[s_exp], k_short=ksa, l_exp=l_exp, l_dte=dte[l_exp], k_long=kl,
             s_bid0=S0.bid, s_ask0=S0.ask, l_bid0=L0.bid, l_ask0=L0.ask,
             s_iv0=s_iv0, l_iv0=l_iv0, s_vega0=s_vega0, l_vega0=l_vega0)
    # the short leg's exit
    if s_exp <= r.exit_date_10:
        sx = close_on(r.ticker, s_exp); intr = max(0.0, ksa - sx)
        o.update(s_exit=intr, s_exit_mid=intr, s_settled=True)
    else:
        if e1 is None or (s_exp, ksa) not in e1.index:
            return {**o, 'err': 'short leg missing at exit'}
        S1 = e1.loc[(s_exp, ksa)]
        o.update(s_exit=S1.ask, s_exit_mid=(S1.bid + S1.ask) / 2, s_settled=False, s_iv1=greeks(r.ticker, s_exp, ksa, r.exit_date_10)[0])
    if e1 is None or (l_exp, kl) not in e1.index:
        return {**o, 'err': 'long leg missing at exit'}
    L1 = e1.loc[(l_exp, kl)]
    o.update(l_bid1=L1.bid, l_ask1=L1.ask, l_iv1=greeks(r.ticker, l_exp, kl, r.exit_date_10)[0])
    # 0b: the ATM straddle's price at the first expiry on or after the exit (the call side from S11's EOD) vs the realised move
    return o


with cf.ThreadPoolExecutor(4) as ex:
    res = pd.DataFrame(list(ex.map(sim, t.itertuples())))
d = t[['ticker', 'date', 'yr', 'close', 'r10', 'exit_date_10', 'expiration', 'atm_strike', 'bid', 'ask']].rename(
    columns={'expiration': 'c_exp', 'atm_strike': 'c_k', 'bid': 'c_bid', 'ask': 'c_ask'}).merge(res, on=['ticker', 'date'])
d['stock'] = -d.r10
print(f"\n## 1. the chain trades: {len(d)}; errors: " + (d.err.value_counts().to_dict().__repr__() if 'err' in d else 'none'))
u = d[d.get('err').isna()].copy() if 'err' in d else d.copy()
C = u.close
u['short'] = (u.s_bid0 - u.s_exit) / C * 1e4
u['short_mid'] = ((u.s_bid0 + u.s_ask0) / 2 - u.s_exit_mid) / C * 1e4
u['long'] = (u.l_bid1 - u.l_ask0) / C * 1e4
u['long_mid'] = ((u.l_bid1 + u.l_ask1) / 2 - (u.l_bid0 + u.l_ask0) / 2) / C * 1e4
u['n0'] = np.clip(u.s_bid0 / u.l_ask0.where(u.l_ask0 > 0), 0, 5)
for n in (1, 2, 3):
    u[f'c{n}'] = u.short + n * u.long; u[f'c{n}_mid'] = u.short_mid + n * u.long_mid
u['c0'] = u.short + u.n0 * u.long; u['c0_mid'] = u.short_mid + u.n0 * u.long_mid
u['vega_net1'] = u.l_vega0 - u.s_vega0
u.to_parquet(OUT, index=False)
print(f"scored {len(u)}; short leg: median dte {u.s_dte.median():.0f}, settled at expiry {int(u.s_settled.sum())}, credit "
      f"{(u.s_bid0 / C * 1e4).median():.0f} bp; long leg: median dte {u.l_dte.median():.0f}, strike {(u.k_long / C).median() * 100:.0f}% "
      f"of the close, cost {(u.l_ask0 / C * 1e4).median():.0f} bp; zero-cost ratio median {u.n0.median():.2f}")
print(f"net vega at entry, 1:1 (per share, $ per vol point x 100): median {u.vega_net1.median():+.4f}, positive (LONG vol) in "
      f"{(u.vega_net1 > 0).mean() * 100:.0f}%; IV change D -> exit: short leg {(u.s_iv1 - u.s_iv0).median():+.3f} (where not settled), "
      f"long leg {(u.l_iv1 - u.l_iv0).median():+.3f} (median, vol units; entry IV short {u.s_iv0.median():.2f} long {u.l_iv0.median():.2f})")
print("\n| structure (same trades) | n | win% | mean bp | median bp | PF | worst bp | best bp |\n|---|---|---|---|---|---|---|---|")
for lab, col in [('stock short (no borrow fee)', 'stock'), ('short ATM put alone', 'short'), ('long OTM put alone', 'long'),
                 ('1:1 diagonal', 'c1'), ('1:2 backspread', 'c2'), ('1:3 backspread', 'c3'), ('zero-cost ratio', 'c0'),
                 ('1:2 backspread, mid', 'c2_mid'), ('zero-cost ratio, mid', 'c0_mid'), ('long OTM put alone, mid', 'long_mid')]:
    x = u[col].dropna().values
    print(f"| {lab} | {len(x)} | {(x > 0).mean() * 100:.0f}% | {x.mean():+.0f} | {np.median(x):+.0f} | **{pf(x):.2f}** | {x.min():+.0f} | {x.max():+.0f} |")
print("\n### the payoff by the stock's 10-day outcome (mean bp)")
u['outcome'] = pd.cut(-u.stock, BINS, labels=LAB)
print(u.groupby('outcome', observed=True)[['stock', 'short', 'long', 'c1', 'c2', 'c0']].agg(['size', 'mean']).round(0)
      .loc[:, [('stock', 'size'), ('stock', 'mean'), ('short', 'mean'), ('long', 'mean'), ('c1', 'mean'), ('c2', 'mean'), ('c0', 'mean')]].to_string())

# ---- 0b. implied vs realised move: the ATM straddle at the call's expiry (S11's contract; the put at the same strike)
def straddle(r):
    rows, _ = get('history/eod', symbol=r.ticker, expiration=r.c_exp.strftime('%Y%m%d'), strike=f"{r.c_k:.3f}", right='put',
                  start_date=r.date.strftime('%Y%m%d'), end_date=r.date.strftime('%Y%m%d'))
    return (r.ticker, r.date, (rows[0]['bid'] + rows[0]['ask']) / 2 if rows else np.nan)


u['c_exp'] = pd.to_datetime(u.c_exp)
print(f"  the straddles: {len(u)} requests", flush=True)
with cf.ThreadPoolExecutor(4) as ex:
    sp = pd.DataFrame(list(ex.map(straddle, u.itertuples())), columns=['ticker', 'date', 'p_mid'])
u = u.merge(sp, on=['ticker', 'date'])
u['implied_move'] = ((u.c_bid + u.c_ask) / 2 + u.p_mid) / u.close * 100
u['realised_move'] = [abs(close_on(tk, x) / c - 1) * 100 for tk, x, c in zip(u.ticker, u.c_exp, u.close)]
v = u.dropna(subset=['implied_move', 'realised_move'])
print(f"\n## 0b. the ATM straddle (the market's expected |move| to its expiry, median {v.c_exp.sub(v.date).dt.days.median():.0f} days) "
      f"vs the realised |move|, {len(v)} trades")
print(f"implied: median {v.implied_move.median():.1f}%, mean {v.implied_move.mean():.1f}%; realised: median {v.realised_move.median():.1f}%, "
      f"mean {v.realised_move.mean():.1f}%; realised > implied in {(v.realised_move > v.implied_move).mean() * 100:.0f}% of trades; "
      f"a long straddle at the mid held to expiry: mean {(v.realised_move - v.implied_move).mean():+.1f}% of the stock")
