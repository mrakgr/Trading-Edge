#!/usr/bin/env python3
"""PEAD on upside earnings breakouts: stock vs long call vs short put (user 2026-11-11).

Events: EDGAR 8-K 2.02 earnings (data/options/earnings_8k202.parquet, 444 tickers, 2015-06 ->) -> reaction day R
  (BMO/RTH: that session, AMC: the next). Features at R's CLOSE (all known then):
  ret_r      total-return close-to-close on R   (V = close*n + cum_div, daily_adjusted: split- and dividend-consistent)
  gap_r      R's open vs R-1's close (same V)
  vol_ratio  R's volume / mean volume of the 30 sessions BEFORE R   (volume / n: split-consistent shares)
  dvol30     mean close*volume of those 30 sessions ($)
  hi52       V_R >= max V of the 252 sessions before R (new 52-week closing high)
  runup20    V_{R-1} / V_{R-21} - 1
  xs_r       ret_r - SPY's ret on R
Forward (entry at R's close): r_h = V_{R+h}/V_R - 1, xs_h = r_h - SPY's, h = 5, 10, 15, 20 sessions; split_h flags an
  n change in (R, R+h].
Options (ThetaData EOD NBBO = the last quote of the day; data/options/eod_universe, 2022-12 ->, DTE <= 90):
  at R's close the ATM strike (nearest S, both rights quoted) of
    tight  the first expiry >= the exit date            (expiring ON the exit date -> settles at intrinsic)
    buf    the first expiry >= the exit date + 14 days
  long call: buy at R close, sell at R+h close; short put: sell at R close, buy back at R+h close.
  Fill f: each trade at mid -/+ f * half-spread (f=0 mid, 1 = natural).
P&L per share of underlying in % of S_R (1 contract = 100 shares, same notional as 100 shares of stock); also the call
as % of premium and the put as % of strike (cash-secured).

    cd research && python3 scripts/options/pead.py --build     # build events + options
    cd research && python3 scripts/options/pead.py             # report
writes data/options/pead/{events,options}.parquet
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40)
ap = argparse.ArgumentParser(); ap.add_argument('--build', action='store_true')
ap.add_argument('--earnings', default='data/options/earnings_8k202.parquet')
ap.add_argument('--out', default='data/options/pead')
ap.add_argument('--no-options', action='store_true', help='stock only (the expanded universe)')
ap.add_argument('--min-md60', type=float, default=0, help='report: R-1 median 60d $ volume floor')
a = ap.parse_args()
OUT = a.out; H = [5, 10, 15, 20]; os.makedirs(OUT, exist_ok=True)


def resolve_reused(e):
    """Tickers whose filings come from >1 CIK (a reused ticker: AA = Alcoa Inc -> Alcoa Corp 2016): find each switch
    date by bisecting MASSIVE's point-in-time CIK between the query dates, keep a filing only while its CIK held the
    ticker. Cached in OUT/cik_spans.parquet."""
    import json, urllib.request, urllib.error
    if 'query_date' not in e.columns: return e
    multi = e.groupby('ticker').cik.nunique(); multi = multi[multi > 1].index
    if not len(multi): return e
    cache = f'{OUT}/cik_spans.parquet'
    if os.path.exists(cache): sp = pd.read_parquet(cache)
    else:
        key = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']
        def cik_at(t, d):
            for att in range(5):
                try:
                    with urllib.request.urlopen(f'https://api.massive.com/v3/reference/tickers/{t}?date={d:%Y-%m-%d}&apiKey={key}', timeout=30) as r:
                        c_ = json.load(r).get('results', {}).get('cik')
                        return int(c_) if c_ else None
                except urllib.error.HTTPError: return None
                except Exception: time.sleep(3 * (att + 1))   # timeouts: retry
            return None
        rows = []
        for t in multi:
            q = e[e.ticker == t][['query_date', 'cik']].drop_duplicates().sort_values('query_date')
            pts = [(pd.Timestamp(d), c_) for d, c_ in zip(q.query_date, q.cik)]
            for (d0, c0), (d1, c1) in zip(pts, pts[1:]):
                if c0 == c1: continue
                lo, hi = d0, d1
                while (hi - lo).days > 1:
                    mid = lo + (hi - lo) / 2; mid = pd.Timestamp(mid.date())
                    if cik_at(t, mid) == c0: lo = mid
                    else: hi = mid
                rows.append((t, c0, c1, hi))
        sp = pd.DataFrame(rows, columns=['ticker', 'cik_old', 'cik_new', 'switch'])
        sp.to_parquet(cache, index=False)
    keep = np.ones(len(e), bool); fd = pd.to_datetime(e.et_date)
    for r in sp.itertuples():
        m = (e.ticker == r.ticker).values
        keep &= ~(m & (e.cik == r.cik_old).values & (fd >= r.switch).values)
        keep &= ~(m & (e.cik == r.cik_new).values & (fd < r.switch).values)
    print(f'reused tickers {len(multi)}: switches {len(sp)}, filings dropped {(~keep).sum()}', flush=True)
    return e[keep]

if a.build:
    t0 = time.time()
    e = pd.read_parquet(a.earnings)
    if 'is_release' in e.columns: e = e[e.is_release]   # earnings_scheduled.py: scheduled quarterly releases only
    e = resolve_reused(e)[['ticker', 'session', 'et_date']]
    c = duckdb.connect('data/trading.db', read_only=True)

    def load(tl):
        d = c.sql(f"""select ticker, date, open, close, volume, n, cum_div from daily_adjusted
                      where ticker in ({','.join(f"'{t}'" for t in tl)}) and date >= '2014-01-01' order by ticker, date""").df()
        d['date'] = pd.to_datetime(d.date); return d

    def feats(d):
        d['tdi'] = d.date.map(tdi)
        d['V'] = d.close * d.n + d.cum_div
        d['Vo'] = d.open * d.n + d.cum_div
        d['vol_adj'] = d.volume / d.n
        g = d.groupby('ticker', sort=False)
        d['V1'] = g.V.shift(1); d['tdi1'] = g.tdi.shift(1)
        d['ret_r'] = d.V / d.V1 - 1
        d['gap_r'] = (d.Vo - d.cum_div + g.cum_div.shift(1)) / d.V1 - 1  # open on R's share basis; dividend on R is negligible
        d['avgv30'] = g.vol_adj.transform(lambda s: s.shift(1).rolling(30, min_periods=25).mean())
        d['dvol30'] = (d.close * d.volume).groupby(d.ticker).transform(lambda s: s.shift(1).rolling(30, min_periods=25).mean())
        d['md60'] = (d.close * d.volume).groupby(d.ticker).transform(lambda s: s.shift(1).rolling(60, min_periods=40).median())
        d['vol_ratio'] = d.vol_adj / d.avgv30
        d['max252'] = g.V.transform(lambda s: s.shift(1).rolling(252, min_periods=200).max())
        d['hi52'] = d.V >= d.max252
        d['runup20'] = g.V.shift(1) / g.V.shift(21) - 1
        for h in H:
            d[f'V_{h}'] = g.V.shift(-h); d[f'tdi_{h}'] = g.tdi.shift(-h); d[f'n_{h}'] = g.n.shift(-h)
            d[f'r_{h}'] = d[f'V_{h}'] / d.V - 1
            d[f'split_{h}'] = d[f'n_{h}'] != d.n
            d[f'gapdays_{h}'] = d[f'tdi_{h}'] - d.tdi - h  # >0: missing sessions in the hold
        return d

    # chunked by ticker: the whole md500k universe at once (13.4M rows x ~40 columns) was OOM-killed on 15 GB
    s0 = load(['SPY'])
    TD = np.array(sorted(s0.date.unique()), dtype='datetime64[D]')
    ed = np.array(pd.to_datetime(e.et_date).values, dtype='datetime64[D]')
    side = np.where(e.session.values == 'AMC', 'right', 'left')
    i = np.where(side == 'right', np.searchsorted(TD, ed, 'right'), np.searchsorted(TD, ed, 'left')); ok = i < len(TD)
    e = e[ok].copy(); e['R'] = pd.to_datetime(TD[i[ok]])
    e = e.drop_duplicates(['ticker', 'R'])
    tdi = {t: k for k, t in enumerate(pd.to_datetime(TD))}
    spy = feats(s0).set_index('date')
    tl = sorted(set(e.ticker)); evs = []; nrows = 0
    for j in range(0, len(tl), 500):
        d = feats(load(tl[j:j + 500])); nrows += len(d)
        ev = e.merge(d.rename(columns={'date': 'R'}), on=['ticker', 'R'], how='inner'); del d
        ev = ev[(ev.tdi1 == ev.tdi - 1)]  # R-1 is the previous session
        ev['xs_r'] = ev.ret_r - ev.R.map(spy.ret_r)
        for h in H:
            ev[f'xs_{h}'] = ev[f'r_{h}'] - ev.R.map(spy[f'r_{h}'])
            ev.loc[ev[f'gapdays_{h}'] != 0, [f'r_{h}', f'xs_{h}']] = np.nan
            ev[f'exit_{h}'] = pd.to_datetime(TD[np.minimum(ev.tdi.values + h, len(TD) - 1)])
            ev.loc[ev.tdi + h >= len(TD), [f'r_{h}', f'xs_{h}']] = np.nan
        evs.append(ev.drop(columns=[c_ for c_ in ev.columns if c_.startswith(('V_', 'tdi_', 'n_', 'gapdays_'))] + ['Vo', 'V1', 'tdi1']))
        print(f'  {min(j + 500, len(tl))}/{len(tl)} tickers, {nrows:,} rows, {time.time()-t0:.0f}s', flush=True)
    ev = pd.concat(evs, ignore_index=True)
    print(f'loaded {nrows} rows, {len(e)} events', flush=True)
    ev['S'] = ev.close
    ev.to_parquet(f'{OUT}/events.parquet', index=False)
    print(f'events {len(ev)} ({ev.R.min().date()} .. {ev.R.max().date()}), {time.time()-t0:.0f}s', flush=True)
    if a.no_options: sys.exit(0)

    # ---------- options ----------
    roots = {os.path.basename(f).split('__')[0] for f in glob.glob('data/options/eod_universe/*.parquet')}
    ev['root'] = ev.ticker.str.replace('.', '', regex=False)
    eo = ev[ev.root.isin(roots) & (ev.R >= '2022-12-01')].copy()
    K = eo[['root', 'R']].drop_duplicates()
    q = duckdb.sql("""select q.root, q.date::date R, q.expiration::date exp, q.strike, q.right, q.bid, q.ask
        from read_parquet('data/options/eod_universe/*.parquet') q join K on q.root = K.root and q.date::date = K.R::date
        where q.ask > 0 and q.bid >= 0 and q.ask >= q.bid""").df().drop_duplicates(['root', 'R', 'exp', 'strike', 'right'])
    q['R'] = pd.to_datetime(q.R); q['exp'] = pd.to_datetime(q.exp)
    ch = q[q.right == 'C'].drop(columns='right').merge(q[q.right == 'P'].drop(columns='right'),
                                                       on=['root', 'R', 'exp', 'strike'], suffixes=('_c', '_p'))
    print(f'entry chains {len(ch)} rows on {len(K)} root-days, {time.time()-t0:.0f}s', flush=True)
    ch = ch.merge(eo[['root', 'R', 'S']].drop_duplicates(['root', 'R']), on=['root', 'R'])
    ch['dist'] = (ch.strike - ch.S).abs()
    atm = ch.sort_values(['root', 'R', 'exp', 'dist', 'strike']).drop_duplicates(['root', 'R', 'exp'])
    exps = atm.groupby(['root', 'R']).exp.apply(lambda s: np.sort(s.values)).to_dict()
    rows = []
    for r in eo.itertuples():
        xs = exps.get((r.root, r.R))
        if xs is None: continue
        for h in H:
            ex = getattr(r, f'exit_{h}')
            if pd.isna(getattr(r, f'r_{h}')) or getattr(r, f'split_{h}'): continue
            for var, lo in [('tight', ex), ('buf', ex + pd.Timedelta(days=14))]:
                k = np.searchsorted(xs, np.datetime64(lo))
                if k < len(xs): rows.append((r.root, r.ticker, r.R, h, var, ex, pd.Timestamp(xs[k])))
    O = pd.DataFrame(rows, columns=['root', 'ticker', 'R', 'h', 'var', 'exit', 'exp'])
    O = O.merge(atm[['root', 'R', 'exp', 'strike', 'S', 'bid_c', 'ask_c', 'bid_p', 'ask_p']], on=['root', 'R', 'exp'])
    X = O[['root', 'exit', 'exp', 'strike']].drop_duplicates()
    qx = duckdb.sql("""select q.root, q.date::date "exit", q.expiration::date exp, q.strike, q.right, q.bid, q.ask
        from read_parquet('data/options/eod_universe/*.parquet') q
        join X on q.root = X.root and q.date::date = X."exit"::date and q.expiration::date = X.exp::date and q.strike = X.strike""").df()
    qx = qx.drop_duplicates(['root', 'exit', 'exp', 'strike', 'right']); qx['exit'] = pd.to_datetime(qx.exit); qx['exp'] = pd.to_datetime(qx.exp)
    for rt in ['C', 'P']:
        O = O.merge(qx[qx.right == rt].drop(columns='right').rename(columns={'bid': f'xbid_{rt.lower()}', 'ask': f'xask_{rt.lower()}'}),
                    on=['root', 'exit', 'exp', 'strike'], how='left')
    # spot at exit (raw close) for the intrinsic settle when the option expires on the exit date
    O = O.merge(d[['ticker', 'date', 'close']].rename(columns={'date': 'exit', 'close': 'S_x'}), on=['ticker', 'exit'], how='left')
    O['expires_at_exit'] = O.exp == O.exit
    O['dte'] = (O.exp - O.R).dt.days
    O.to_parquet(f'{OUT}/options.parquet', index=False)
    print(f'options rows {len(O)}, exit quotes found C {O.xbid_c.notna().mean():.3f} P {O.xbid_p.notna().mean():.3f}, '
          f'expires at exit {O.expires_at_exit.mean():.3f}, {time.time()-t0:.0f}s', flush=True)
    sys.exit(0)

# ======================= report =======================
ev = pd.read_parquet(f'{OUT}/events.parquet')
ev = ev[ev.dvol30.notna() & ev.vol_ratio.notna()]
if 'md60' in ev.columns: ev = ev[ev.md60.fillna(0) >= a.min_md60]
months = (ev.R.max() - ev.R.min()).days / 30.44
print(f'events with features {len(ev)}, {ev.ticker.nunique()} tickers, {ev.R.min().date()} .. {ev.R.max().date()}')
ev['rb'] = pd.cut(ev.xs_r, [-9, -.10, -.05, -.02, .02, .05, .10, .20, 9], labels=['<-10', '-10..-5', '-5..-2', '-2..2', '2..5', '5..10', '10..20', '>20'])
ev['vb'] = pd.cut(ev.vol_ratio, [0, 1.5, 2.5, 4, 999], labels=['<1.5', '1.5-2.5', '2.5-4', '>4'])
for h in [5, 20]:
    print(f'\n--- mean SPY-excess return over the next {h} sessions (%), entry at R close; rows = R-day excess move, cols = R volume / 30d avg ---')
    print((ev.pivot_table(index='rb', columns='vb', values=f'xs_{h}', aggfunc='mean', observed=False) * 100).round(2).to_string())
    print('n:'); print(ev.pivot_table(index='rb', columns='vb', values=f'xs_{h}', aggfunc='count', observed=False).to_string())

def stats(x):
    x = x.dropna(); return pd.Series({'n': len(x), 'mean%': 100 * x.mean(), 'med%': 100 * x.median(), 'win%': 100 * (x > 0).mean(),
                                      't': x.mean() / x.std() * np.sqrt(len(x)) if len(x) > 2 else np.nan})
BO = {'up5_v2.5': (ev.xs_r >= .05) & (ev.vol_ratio >= 2.5),
      'up10_v4': (ev.xs_r >= .10) & (ev.vol_ratio >= 4),
      'up5_v2.5_hi52': (ev.xs_r >= .05) & (ev.vol_ratio >= 2.5) & ev.hi52,
      'gap_hold: gap>=5 & close>open & v2.5': (ev.gap_r >= .05) & (ev.ret_r > ev.gap_r) & (ev.vol_ratio >= 2.5),
      'down5_v2.5 (mirror)': (ev.xs_r <= -.05) & (ev.vol_ratio >= 2.5),
      'all': ev.xs_r.notna()}
print('\n=== stock, SPY-excess return by breakout definition and horizon (2015-06 ->) ===')
for nm, m in BO.items():
    print(f'\n{nm}'); print(pd.DataFrame({h: stats(ev.loc[m, f'xs_{h}']) for h in H}).T.round(2).to_string())
print('\n=== up5_v2.5 by year, xs_20 ===')
m = BO['up5_v2.5']; print(ev[m].groupby(ev[m].R.dt.year).xs_20.apply(stats).unstack().round(2).to_string())

print('\n=== trades per month by definition ===')
for nm, m in BO.items(): print(f'{nm:40s} {m.sum() / months:6.1f}/month')
m = (ev.xs_r > .20) & (ev.vol_ratio > 1.5) & (ev.vol_ratio <= 4)
print('\n=== big move: xs_r > 20%, vol 1.5-4x, by year (xs_20) ===')
print(ev[m].groupby(ev[m].R.dt.year).xs_20.apply(stats).unstack().round(2).to_string())
print('all years'); print(stats(ev.loc[m, 'xs_20']).round(2).to_string())
m = (ev.xs_r > .20) & (ev.vol_ratio > 1.5)
print('\n=== big move: xs_r > 20%, vol > 1.5x, by year (xs_20) ===')
print(ev[m].groupby(ev[m].R.dt.year).xs_20.apply(stats).unstack().round(2).to_string())

# ---------- options vs stock on the same events ----------
if not os.path.exists(f'{OUT}/options.parquet'): sys.exit(0)
O = pd.read_parquet(f'{OUT}/options.parquet')
O = O.merge(ev[['ticker', 'R', 'xs_r', 'ret_r', 'gap_r', 'vol_ratio', 'hi52'] + [f'r_{h}' for h in H] + [f'xs_{h}' for h in H]], on=['ticker', 'R'])
O['r_stock'] = O.apply(lambda r: r[f'r_{int(r.h)}'], axis=1)
O = O[O.xbid_c.notna() | O.expires_at_exit]
def px(b, a_, f, side): return (b + a_) / 2 + side * f * (a_ - b) / 2
for f in [0, 0.5, 1]:
    t = int(f * 100)
    ec = px(O.bid_c, O.ask_c, f, +1); ep = px(O.bid_p, O.ask_p, f, -1)
    xc = np.where(O.expires_at_exit, np.maximum(O.S_x - O.strike, 0), px(O.xbid_c, O.xask_c, f, -1))
    xp = np.where(O.expires_at_exit, np.maximum(O.strike - O.S_x, 0), px(O.xbid_p, O.xask_p, f, +1))
    O[f'call_{t}'] = (xc - ec) / O.S; O[f'put_{t}'] = (ep - xp) / O.S
    O[f'call_prem_{t}'] = (xc - ec) / ec; O[f'put_k_{t}'] = (ep - xp) / O.strike
O['call_mid_pctS'] = (O.ask_c + O.bid_c) / 2 / O.S * 100; O['put_mid_pctS'] = (O.ask_p + O.bid_p) / 2 / O.S * 100
O.to_parquet(f'{OUT}/options_pnl.parquet', index=False)
def cmp(m, label):
    rows = []
    for (h, var), g in O[m].groupby(['h', 'var']):
        r = {'h': h, 'var': var, 'n': len(g), 'dte': g.dte.median(), 'callpx%S': g.call_mid_pctS.median(), 'putpx%S': g.put_mid_pctS.median(),
             'stock': 100 * g.r_stock.mean(), 'stock_sr': g.r_stock.mean() / g.r_stock.std()}
        for f in [0, 50, 100]:
            r[f'call{f}'] = 100 * g[f'call_{f}'].mean(); r[f'put{f}'] = 100 * g[f'put_{f}'].mean()
        r['call_sr50'] = g.call_50.mean() / g.call_50.std(); r['put_sr50'] = g.put_50.mean() / g.put_50.std()
        r['call%prem50'] = 100 * g.call_prem_50.mean(); r['put_win50'] = 100 * (g.put_50 > 0).mean(); r['call_win50'] = 100 * (g.call_50 > 0).mean()
        rows.append(r)
    print(f'\n=== {label}: per share, % of S_R (stock = raw total return; call0/50/100 = fill f; sr = mean/sd per trade) ===')
    print(pd.DataFrame(rows).round(2).to_string(index=False))
cmp(O.xs_r.notna(), 'ALL earnings events with options (2022-12 ->)')
cmp((O.xs_r >= .05) & (O.vol_ratio >= 2.5), 'up5_v2.5')
cmp((O.xs_r >= .10) & (O.vol_ratio >= 4), 'up10_v4')
