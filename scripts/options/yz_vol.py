#!/usr/bin/env python3
"""StraddleHarvester — Yang-Zhang realized volatility per universe name per day (user 2026-11-10: YZ, not
close-to-close), from data/trading.db `daily_adjusted` (raw OHLC + causal n + causal cum_div; docs/price_adjustment.md).

Per day t (log returns):
  overnight  o_t = ln( (O_t·n_t + C_t − C_{t−1}) / (Cl_{t−1}·n_{t−1}) )   ← the ONLY cross-day term: split- and
                                                                          dividend-correct, causal (events land at t's open)
  open→close c_t = ln(Cl_t / O_t)                                          ← same-day raw prices: no adjustment needed
  RS_t           = ln(H/Cl)·ln(H/O) + ln(L/Cl)·ln(L/O)                     ← Rogers-Satchell, same-day raw
Window of N days ending at t (t INCLUDED):
  σ²_YZ = var(o) + k·var(c) + (1−k)·mean(RS),  k = 0.34 / (1.34 + (N+1)/(N−1)),  var with N−1;  annualized ×252.
⚠ KNOWABILITY: the window includes day t's full bar, so yz_N on day t is known at t's 16:00 close — valid for an entry
at t's CLOSING option quotes (ThetaData's EOD NBBO), NOT for any entry earlier in day t. Use the previous row otherwise.
Also cc20 (close-to-close, adjusted) for comparison.
Flags (a reused ticker or a data hole inside the window — e.g. SPCX was an ETF before the 2026-06 listing):
  gap_days_N   the largest calendar-day gap between consecutive rows in the window
  max_on_N     the largest |overnight log return| in the window (no split/dividend explains it: those are adjusted)

    cd research && python3 scripts/options/yz_vol.py > logs/options_yz_vol.log 2>&1; echo "exit $?"
writes data/options/yz_vol.parquet
"""
import argparse
import duckdb, numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--tickers-file', default='data/options/universe_tickers.tsv')
ap.add_argument('--start', default='2022-06-01', help='first bar read (warm-up before the 2022-11 universe)')
ap.add_argument('--windows', default='10,20,60')
ap.add_argument('--out', default='data/options/yz_vol.parquet')
a = ap.parse_args()
W = [int(x) for x in a.windows.split(',')]
tickers = sorted({l.split('\t')[0].strip() for l in open(a.tickers_file) if l.strip()})

con = duckdb.connect('data/trading.db', read_only=True)
con.execute("SET memory_limit='4GB'")
con.execute("create temp table tk(ticker varchar)")
con.executemany("insert into tk values (?)", [(t,) for t in tickers])
d = con.sql(f"""select ticker, date, open, high, low, close, volume, n, cum_div from daily_adjusted
                where ticker in (select ticker from tk) and date >= '{a.start}' order by ticker, date""").df()
d['date'] = pd.to_datetime(d.date)
print(f"{d.ticker.nunique()} of {len(tickers)} tickers in daily_adjusted, {len(d):,} bars, "
      f"{d.date.min():%Y-%m-%d} → {d.date.max():%Y-%m-%d}; missing: {sorted(set(tickers) - set(d.ticker))}")
bad = (d[['open', 'high', 'low', 'close']] <= 0).any(axis=1) | d[['open', 'high', 'low', 'close']].isna().any(axis=1)
print(f"bars with a non-positive / missing price dropped: {int(bad.sum())}")
d = d[~bad].copy()

out = []
for t, g in d.groupby('ticker', sort=False):
    g = g.reset_index(drop=True)
    prev_cl_adj = (g.close * g.n).shift(1)
    o = np.log((g.open * g.n + g.cum_div - g.cum_div.shift(1)) / prev_cl_adj)
    c = np.log(g.close / g.open)
    rs = np.log(g.high / g.close) * np.log(g.high / g.open) + np.log(g.low / g.close) * np.log(g.low / g.open)
    cc = np.log((g.close * g.n + g.cum_div - g.cum_div.shift(1)) / prev_cl_adj)
    gap = g.date.diff().dt.days
    r = pd.DataFrame({'ticker': t, 'date': g.date, 'close': g.close, 'volume': g.volume, 'r_on': o, 'r_oc': c})
    for N in W:
        k = 0.34 / (1.34 + (N + 1) / (N - 1))
        v = o.rolling(N).var() + k * c.rolling(N).var() + (1 - k) * rs.rolling(N).mean()
        r[f'yz{N}'] = np.sqrt(v.clip(lower=0) * 252)
        r[f'gap_days_{N}'] = gap.rolling(N).max()
        r[f'max_on_{N}'] = o.abs().rolling(N).max()
    r['cc20'] = cc.rolling(20).std() * np.sqrt(252)
    out.append(r)
y = pd.concat(out, ignore_index=True)
y.to_parquet(a.out, index=False)

z = y.dropna(subset=['yz20'])
print(f"\nwrote {len(y):,} rows -> {a.out}")
print(f"yz20 (annualized): p5 {z.yz20.quantile(.05):.2f}  median {z.yz20.median():.2f}  p95 {z.yz20.quantile(.95):.2f}  "
      f"max {z.yz20.max():.2f};  yz20/cc20 median {(z.yz20 / z.cc20).median():.3f}")
for name, q in [('gap_days_20 > 5', z.gap_days_20 > 5), ('max_on_20 > ln 2', z.max_on_20 > np.log(2))]:
    print(f"rows flagged {name}: {int(q.sum()):,} on {z[q].ticker.nunique()} tickers: "
          f"{' '.join(z[q].groupby('ticker').size().sort_values(ascending=False).head(20).index)}")
for t in ['NVDA', 'AAPL', 'TSLA']:
    s = z[(z.ticker == t) & (z.date == z[z.ticker == t].date.max())].iloc[0]
    print(f"{t} {s.date:%Y-%m-%d}: yz10 {s.yz10:.3f} yz20 {s.yz20:.3f} yz60 {s.yz60:.3f} cc20 {s.cc20:.3f}")
