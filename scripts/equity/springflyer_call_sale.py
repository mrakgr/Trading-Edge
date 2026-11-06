#!/usr/bin/env python3
"""SpringFlyer S13 — the SpringFlyer short expressed as a SOLD CALL (user 2026-11-06: "test the call sale on those 147
trades ... a naked at-the-money call, an out-of-the-money call and a bear call spread against the stock short").

The trades: S11's book signals with a call chain on D (option_coverage.parquet, has_chain). The contract: S11's
expiration (the first on or after the 10-day exit date, else the last listed). Per trade, two EOD requests (D and the
exit day, every strike of that expiration); ThetaData's EOD row carries the day's closing NBBO.
  entry  D's close: SELL at the bid (BUY the spread's long leg at the ask)
  exit   the 10-day exit date's close: BUY back at the ask (SELL the long leg at the bid); a contract expiring on or before
         the exit date settles at INTRINSIC against the stock's raw close on its expiration day
  bound  the same trades at the mid both ways
Legs: ATM = the strike nearest D's raw close; OTM = nearest 1.25 × close (above the close); the spread's long leg =
nearest 1.50 × close (above the short strike). P&L per share of the underlying, in bp of D's close — the same unit as
the stock short (−r10, D close → exit close, dividends as cash, NO borrow fee); the spread also on its max risk
(width − credit). Trades with a split between D and the exit are dropped (the contract's deliverable changes).
Needs the Theta Terminal running (127.0.0.1:25503).

    cd research && python3 scripts/equity/springflyer_call_sale.py
writes data/equity/springflyer/call_sale.parquet
"""
import concurrent.futures as cf, json, os, urllib.error, urllib.parse, urllib.request
import duckdb, numpy as np, pandas as pd
import data_v2

BASE = 'http://127.0.0.1:25503/v3/option'
OUT = 'data/equity/springflyer/call_sale.parquet'


def get(path, **q):
    url = f"{BASE}/{path}?" + urllib.parse.urlencode({**q, 'format': 'ndjson'})
    try:
        with urllib.request.urlopen(url, timeout=120) as r:
            body = r.read().decode()
    except urllib.error.HTTPError as e:                  # 472 = no data
        return [], f"HTTP {e.code}"
    try:
        return [json.loads(l) for l in body.splitlines() if l.strip()], None
    except json.JSONDecodeError:
        return [], body.strip()[:120]


cov = pd.read_parquet('data/equity/springflyer/option_coverage.parquet')
t = cov[cov.has_chain].copy()
for c in ('date', 'exit_date_10', 'expiration'):
    t[c] = pd.to_datetime(t[c])
t = t[t.exit_date_10.notna()].reset_index(drop=True)          # closed trades only

con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='4GB'")
con.register('t', t[['ticker', 'date', 'exit_date_10', 'expiration']])
spl = con.execute("SELECT DISTINCT t.ticker, t.date FROM t JOIN splits s ON s.ticker = t.ticker AND s.execution_date > t.date "
                  "AND s.execution_date <= GREATEST(t.exit_date_10, t.expiration)").df()
spl['date'] = pd.to_datetime(spl.date)
# the stock's RAW close on every day we settle against (the exit date and the expiration day, or the session before it)
px = con.execute("""SELECT t.ticker, t.date,
    (SELECT close FROM daily_episodes_causal d WHERE d.ticker = t.ticker AND d.date <= t.exit_date_10 ORDER BY d.date DESC LIMIT 1) AS close_exit,
    (SELECT close FROM daily_episodes_causal d WHERE d.ticker = t.ticker AND d.date <= t.expiration ORDER BY d.date DESC LIMIT 1) AS close_expiry
  FROM t""").df()
px['date'] = pd.to_datetime(px.date)
t = t.merge(px, on=['ticker', 'date'])
t['split_in_hold'] = t.set_index(['ticker', 'date']).index.isin(spl.set_index(['ticker', 'date']).index)


def chain(sym, exp, day):
    rows, err = get('history/eod', symbol=sym, expiration=exp.strftime('%Y%m%d'), right='call',
                    start_date=day.strftime('%Y%m%d'), end_date=day.strftime('%Y%m%d'))
    if not rows:
        return None
    e = pd.DataFrame(rows)[['strike', 'bid', 'ask']]
    return e.groupby('strike').last()


def nearest(ch, target, above=None):
    k = ch.index.values
    if above is not None:
        k = k[k > above]
    return None if len(k) == 0 else float(k[np.argmin(np.abs(k - target))])


def leg_pnl(k, side, e0, e1, settle, close0, mid):
    """P&L per share (bp of close0) of ONE leg: side −1 = sold, +1 = bought. settle = intrinsic price or None (quote it)."""
    if k is None or k not in e0.index:
        return np.nan
    b0, a0 = e0.loc[k, 'bid'], e0.loc[k, 'ask']
    p0 = (b0 + a0) / 2 if mid else (b0 if side < 0 else a0)
    if settle is not None:
        p1 = max(0.0, settle - k)
    else:
        if e1 is None or k not in e1.index:
            return np.nan
        b1, a1 = e1.loc[k, 'bid'], e1.loc[k, 'ask']
        p1 = (b1 + a1) / 2 if mid else (a1 if side < 0 else b1)
    return side * (p1 - p0) / close0 * 1e4


def sim(r):
    o = dict(ticker=r.ticker, date=r.date)
    expires_first = r.expiration <= r.exit_date_10
    exit_day = r.expiration if expires_first else r.exit_date_10
    e0 = chain(r.ticker, r.expiration, r.date)
    e1 = None if expires_first else chain(r.ticker, r.expiration, exit_day)
    settle = r.close_expiry if expires_first else None
    if e0 is None:
        return {**o, 'err': 'no D chain'}
    ka = nearest(e0, r.close); ko = nearest(e0, 1.25 * r.close, above=r.close); kl = nearest(e0, 1.5 * r.close, above=ka)
    o.update(expires_first=expires_first, k_atm=ka, k_otm=ko, k_long=kl,
             atm_bid=e0.loc[ka, 'bid'], atm_ask=e0.loc[ka, 'ask'],
             missing_exit=(not expires_first) and (e1 is None or ka not in e1.index))
    for mid in (False, True):
        sfx = '_mid' if mid else ''
        o['atm' + sfx] = leg_pnl(ka, -1, e0, e1, settle, r.close, mid)
        o['otm' + sfx] = leg_pnl(ko, -1, e0, e1, settle, r.close, mid)
        o['spread' + sfx] = o['atm' + sfx] + leg_pnl(kl, +1, e0, e1, settle, r.close, mid)
    if kl is not None:                                # the spread's max risk at entry: width − credit (bid − ask)
        credit = e0.loc[ka, 'bid'] - e0.loc[kl, 'ask']
        o['spread_credit_bp'] = credit / r.close * 1e4
        o['spread_risk_bp'] = ((kl - ka) - credit) / r.close * 1e4
    return o


with cf.ThreadPoolExecutor(4) as ex:
    res = pd.DataFrame(list(ex.map(sim, t.itertuples())))
d = t.merge(res, on=['ticker', 'date'])
d['stock'] = -d.r10
os.makedirs(os.path.dirname(OUT), exist_ok=True); d.to_parquet(OUT, index=False)


def pf(r):
    r = np.asarray(r, float); r = r[~np.isnan(r)]; g, l = r[r > 0].sum(), -r[r < 0].sum()
    return g / l if l > 0 else float('inf')


print(f"trades with a chain and a closed 10-day exit: {len(t)}; split during the hold (dropped): {t.split_in_hold.sum()}; "
      f"no D chain on re-request: {(d.get('err') == 'no D chain').sum() if 'err' in d else 0}; "
      f"the ATM contract missing on the exit day: {int(d.missing_exit.fillna(False).sum())}; expired on/before the exit: {int(d.expires_first.fillna(False).sum())}")
u = d[~d.split_in_hold & d.atm.notna()].copy()
print(f"scored: {len(u)} trades; ATM credit at the bid: median {np.median(u.atm_bid / u.close * 1e4):.0f} bp of the stock, "
      f"zero-bid ATM calls {int((u.atm_bid <= 0).sum())}\n")
print("| structure (same trades) | n | win% | mean bp | median bp | PF | worst bp | best bp | p5 bp |\n|---|---|---|---|---|---|---|---|---|")
for lab, col in [('stock short (no borrow fee)', 'stock'), ('sold ATM call, bid/ask', 'atm'), ('sold ATM call, mid', 'atm_mid'),
                 ('sold +25% OTM call, bid/ask', 'otm'), ('sold +25% OTM call, mid', 'otm_mid'),
                 ('bear call spread ATM / +50%, bid/ask', 'spread'), ('bear call spread, mid', 'spread_mid')]:
    x = u[col].dropna().values
    print(f"| {lab} | {len(x)} | {(x > 0).mean() * 100:.0f}% | {x.mean():+.0f} | {np.median(x):+.0f} | **{pf(x):.2f}** | {x.min():+.0f} | {x.max():+.0f} | {np.percentile(x, 5):+.0f} |")
s = u[u.spread.notna() & (u.spread_risk_bp > 0)]
print(f"\nbear call spread on its MAX RISK (width − credit): n {len(s)}, median credit {s.spread_credit_bp.median():.0f} bp, median risk "
      f"{s.spread_risk_bp.median():.0f} bp; return on risk mean {(s.spread / s.spread_risk_bp).mean() * 100:+.1f}%, median "
      f"{(s.spread / s.spread_risk_bp).median() * 100:+.1f}%; stock short on the same trades mean {s.stock.mean():+.0f} bp")
print("\n### the 10 worst stock-short trades, every structure (bp of the stock)")
print(u.nsmallest(10, 'stock')[['ticker', 'date', 'close', 'k_atm', 'atm_bid', 'atm_ask', 'stock', 'atm', 'otm', 'spread']]
      .assign(date=lambda x: x.date.dt.date).round(2).to_string(index=False))
print("\n### by the stock short's outcome (mean bp)")
u['outcome'] = pd.cut(u.stock, [-1e9, -2000, 0, 2000, 1e9], labels=['stock rose > 20%', 'rose 0-20%', 'fell 0-20%', 'fell > 20%'])
print(u.groupby('outcome', observed=True)[['stock', 'atm', 'otm', 'spread']].agg(['size', 'mean']).round(0).to_string())

print("\n### HOLD TO EXPIRY: the sold call settles at intrinsic on its expiration day (no exit spread); the stock short held to the same day")
u['stock_exp'] = (u.close - u.close_expiry) / u.close * 1e4
u['atm_exp'] = (u.atm_bid - np.maximum(0, u.close_expiry - u.k_atm)) / u.close * 1e4
u['atm_exp_mid'] = ((u.atm_bid + u.atm_ask) / 2 - np.maximum(0, u.close_expiry - u.k_atm)) / u.close * 1e4
print(f"median days to expiry {u.dte.median():.0f} (the 10-day exit is ~14 calendar days)")
print("| structure | n | win% | mean bp | median bp | PF | worst bp | p5 bp |\n|---|---|---|---|---|---|---|---|")
for lab, col in [('stock short to the expiry day', 'stock_exp'), ('sold ATM call to expiry, sold at the bid', 'atm_exp'),
                 ('sold ATM call to expiry, sold at the mid', 'atm_exp_mid'), ('(the 10-day exit, for reference) stock short', 'stock'),
                 ('(the 10-day exit) sold ATM call, bid/ask', 'atm')]:
    x = u[col].dropna().values
    print(f"| {lab} | {len(x)} | {(x > 0).mean() * 100:.0f}% | {x.mean():+.0f} | {np.median(x):+.0f} | **{pf(x):.2f}** | {x.min():+.0f} | {np.percentile(x, 5):+.0f} |")
print("\n### to expiry, by the stock's outcome (mean bp)")
u['outcome_exp'] = pd.cut(u.stock_exp, [-1e9, -2000, 0, 2000, 1e9], labels=['stock rose > 20%', 'rose 0-20%', 'fell 0-20%', 'fell > 20%'])
print(u.groupby('outcome_exp', observed=True)[['stock_exp', 'atm_exp']].agg(['size', 'mean']).round(0).to_string())
