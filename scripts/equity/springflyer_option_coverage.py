#!/usr/bin/env python3
"""SpringFlyer S11 — do the book's names HAVE options on the signal day? (user 2026-11-06: sell calls on the SpringFlyer
shorts; ThetaData, options STANDARD = history from 2016-01-04, measured: every earlier date is refused "PROFESSIONAL")

For every book signal (signals_spec_v2.parquet, in_book10, 2016-01-04 →) one EOD request on day D for every call:
  has_chain        the symbol is in /v3/option/list/symbols AND D's EOD returned call rows
  n_exp            expiries listed on D
  the ATM call     strike nearest D's raw close, at the first expiry ON OR AFTER exit_date_10 (the 10-day timestop's date)
                   (else the last listed): its EOD bid/ask (ThetaData's EOD quote = the last NBBO of D), spread % of the
                   mid, the mid as % of the stock's close, days to expiry
⚠ a symbol in today's list is not proof the D chain was the same company (ticker reuse) — the D request answers that.
Needs the Theta Terminal running (127.0.0.1:25503; the key from ~/.config/thetadata/credentials via THETADATA_API_KEY).

    cd research && python3 scripts/equity/springflyer_option_coverage.py
writes data/equity/springflyer/option_coverage.parquet
"""
import concurrent.futures as cf, datetime as dt, json, os, urllib.error, urllib.parse, urllib.request
import numpy as np, pandas as pd

BASE = 'http://127.0.0.1:25503/v3/option'
SIG = 'data/equity/springflyer/signals_spec_v2.parquet'
OUT = 'data/equity/springflyer/option_coverage.parquet'


def get(path, **q):
    url = f"{BASE}/{path}?" + urllib.parse.urlencode({**q, 'format': 'ndjson'})
    try:
        with urllib.request.urlopen(url, timeout=120) as r:
            body = r.read().decode()
    except urllib.error.HTTPError as e:                  # 472 = ThetaData's NO DATA (no chain that day); others recorded
        return [], f"HTTP {e.code}: {e.read().decode(errors='replace').strip()[:120]}"
    try:
        return [json.loads(l) for l in body.splitlines() if l.strip()], None
    except json.JSONDecodeError:
        return [], body.strip()[:160]


syms = {r['symbol'] for r in get('list/symbols')[0]}
s = pd.read_parquet(SIG)
s = s[s.in_book10 & (pd.to_datetime(s.date) >= '2016-01-04')].reset_index(drop=True)
print(f"option symbols listed: {len(syms):,}; book signals 2016-01-04 →: {len(s)} on {s.ticker.nunique()} names, "
      f"{s.ticker.isin(syms).sum()} with the symbol in today's list")


def probe(r):
    out = dict(ticker=r.ticker, date=r.date, listed=r.ticker in syms, has_chain=False, n_exp=0, err=None)
    if not out['listed']:
        return out
    d = pd.Timestamp(r.date).strftime('%Y%m%d')
    rows, err = get('history/eod', symbol=r.ticker, expiration='*', right='call', start_date=d, end_date=d)
    out['err'] = err
    if not rows:
        return out
    e = pd.DataFrame(rows); e['expiration'] = pd.to_datetime(e.expiration)
    out.update(has_chain=True, n_exp=e.expiration.nunique(), n_calls=len(e))
    x10 = pd.Timestamp(r.exit_date_10) if pd.notna(r.exit_date_10) else pd.Timestamp(r.date) + pd.Timedelta(days=14)
    later = sorted(x for x in e.expiration.unique() if x >= x10)
    exp = later[0] if later else e.expiration.max()
    c = e[e.expiration == exp].copy(); c['dist'] = (c.strike - r.close).abs(); a = c.nsmallest(1, 'dist').iloc[0]
    mid = (a.bid + a.ask) / 2
    out.update(expiration=exp.date(), dte=(exp - pd.Timestamp(r.date)).days, atm_strike=a.strike, bid=a.bid, ask=a.ask,
               spread_pct=(a.ask - a.bid) / mid * 100 if mid > 0 else np.nan, mid_pct_of_close=mid / r.close * 100,
               atm_volume=a.volume, n_strikes_at_exp=len(c))
    return out


with cf.ThreadPoolExecutor(4) as ex:                      # the account's 4 concurrent requests
    res = pd.DataFrame(list(ex.map(probe, s.itertuples())))
res = s[['ticker', 'date', 'yr', 'close', 'dv20_prior', 'r5', 'r10', 'exit_date_10']].merge(res, on=['ticker', 'date'])
os.makedirs(os.path.dirname(OUT), exist_ok=True); res.to_parquet(OUT, index=False)
errs = res.err.dropna(); print(errs.str[:8].value_counts().to_string())
print(f"wrote {OUT}; request errors: {len(errs)}" + (f" e.g. {errs.iloc[0]}" if len(errs) else ''))


def pf(r):
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return g / l if l > 0 else float('inf')


print("\n| year | book | symbol listed | chain on D | % with chain | median ATM spread % | median ATM mid % of close | median dte | stock-short PF r10 (chain) | (no chain) |")
print("|---|---|---|---|---|---|---|---|---|---|")
for y, g in list(res.groupby('yr')) + [('all', res)]:
    c = g[g.has_chain]; n = g[~g.has_chain]
    print(f"| {y} | {len(g)} | {g.listed.sum()} | {len(c)} | {len(c) / len(g) * 100:.0f}% | {c.spread_pct.median():.1f} | "
          f"{c.mid_pct_of_close.median():.1f} | {c.dte.median():.0f} | {pf(-c.r10.dropna().values):.2f} | {pf(-n.r10.dropna().values):.2f} |")
print("\n### chain on D by the stock's raw close")
res['band'] = pd.cut(res.close, [2, 5, 10, 20, 50, 1e9], labels=['$2-5', '$5-10', '$10-20', '$20-50', '$50+'])
print(res.groupby('band', observed=True).agg(book=('ticker', 'size'), with_chain=('has_chain', 'sum'),
      median_spread_pct=('spread_pct', 'median'), median_mid_pct=('mid_pct_of_close', 'median')).round(1).to_string())
