#!/usr/bin/env python3
"""Volatility Vibes baseline (user 2026-11-11): the three VV features per name-day, and EVERY ATM short straddle and
long calendar, held to expiration. Post-hoc analysis in SQL on the outputs.

Features, per (root, date), all known at date's 16:00 close (EOD NBBO + that day's bar):
  ATM per expiry   the strike nearest the underlying's regular-session close S (Massive day aggs) with both legs quoted
  iv(exp)          Black-Scholes r=q=0 implied vol of the ATM straddle MID, T = calendar DTE/365
  term structure   linear in DTE over the expiries with dte>=1 and a valid iv, flat beyond both ends
  iv30, iv45       the term structure at 30 / 45 DTE
  ts_slope_0_45    (iv45 - iv_front) / (45 - dte_front)   (VV; NaN when the front is already >= 45 DTE)
  rv30             Yang-Zhang, 30 days ending at date, annualized x252 (yz_vol.py; includes date's bar)
  iv30_rv30        iv30 / rv30
  avg_vol30        mean share volume of the 30 days ending at date (daily_adjusted, raw);  avg_dvol30 = mean close*volume

Trades, entry at date's close, one row per (root, date, expiry):
  straddle         SELL the ATM straddle; settles at intrinsic |S_T - K| on expiry's close (S_T = last close <= expiry)
                   pnl_mid = mid credit - payoff,  pnl_nat = bid credit - payoff   ($/share; x100 per contract)
  calendar         short front (the straddle's expiry, strike K) + long back at the SAME K, back = the expiry closest to
                   front+30 DTE within [front+21, front+45]. At the front's expiry: the front settles at intrinsic, the
                   back is SOLD at that day's quote. Call, put and both (= straddle calendar). mid: mid in, mid out;
                   nat: buy back at ask / sell front at bid, exit back at bid.
Earnings: EDGAR 8-K 2.02 -> reaction day (BMO/RTH: that trading day, AMC: the next). A trade holds the event when
  entry < reaction <= expiry (calendars: <= front expiry). has_earn_src = False for the 86 6-K filers (unknown!).
Splits: split_in_hold flags an execution date in (entry, exit] -> strikes/deliverables changed: exclude.

    cd research && python3 scripts/options/vv_backtest.py > logs/options_vv_backtest.log 2>&1; echo "exit $?"
writes data/options/vv/{features,straddles,calendars}.parquet
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
from multiprocessing import Pool
from scipy.special import ndtr

ap = argparse.ArgumentParser()
ap.add_argument('--eod', default='data/options/eod_universe')
ap.add_argument('--out', default='data/options/vv')
ap.add_argument('--roots', default='', help='comma list (test)')
ap.add_argument('--workers', type=int, default=6)
ap.add_argument('--max-front-dte', type=int, default=60, help='calendar fronts up to this DTE')
a = ap.parse_args()

# ---------- shared inputs (loaded once, inherited by fork) ----------
t0 = time.time()
tick = sorted({l.split('\t')[0].strip() for l in open('data/options/universe_tickers.tsv') if l.strip()})
ROOT2T = {t.replace('.', ''): t for t in tick}
roots = sorted({os.path.basename(f).split('__')[0] for f in glob.glob(f'{a.eod}/*.parquet')})
if a.roots: roots = a.roots.split(',')

agg_files = sorted(glob.glob('data/daily_aggregates/*.csv.gz'))
agg_files = [f for f in agg_files if os.path.basename(f)[:10] >= '2022-10-01']
TD = np.array(sorted(os.path.basename(f)[:10] for f in agg_files), dtype='datetime64[D]')
tl = ','.join(f"'{t}'" for t in tick)
spot = duckdb.sql(f"""select ticker, cast(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) as date) as date,
                      close from read_csv({agg_files}, filename=true, columns={{'ticker':'VARCHAR','volume':'DOUBLE',
                      'open':'DOUBLE','close':'DOUBLE','high':'DOUBLE','low':'DOUBLE','window_start':'BIGINT',
                      'transactions':'BIGINT'}}, header=true) where ticker in ({tl})""").df()
spot['date'] = pd.to_datetime(spot.date)
SPOT = {t: g.set_index('date').close.sort_index() for t, g in spot.groupby('ticker')}

yz = pd.read_parquet('data/options/yz_vol.parquet',
                     columns=['ticker', 'date', 'close', 'volume', 'yz20', 'yz30', 'gap_days_30', 'max_on_30'])
yz = yz.sort_values(['ticker', 'date'])
yz['avg_vol30'] = yz.groupby('ticker').volume.transform(lambda s: s.rolling(30).mean())
yz['avg_dvol30'] = (yz.close * yz.volume).groupby(yz.ticker).transform(lambda s: s.rolling(30).mean())
YZ = {t: g.drop(columns=['ticker', 'close', 'volume']).set_index('date') for t, g in yz.groupby('ticker')}

e = pd.read_parquet('data/options/earnings_8k202.parquet', columns=['ticker', 'session', 'et_date'])
ed = np.array(pd.to_datetime(e.et_date).values, dtype='datetime64[D]')
side = np.where(e.session.values == 'AMC', 'right', 'left')
e['react'] = [TD[min(np.searchsorted(TD, d, s), len(TD) - 1)] for d, s in zip(ed, side)]
EARN = {t: np.unique(np.array(g.react.values, dtype='datetime64[D]')) for t, g in e.groupby('ticker')}

c = duckdb.connect('data/trading.db', read_only=True)
sp = c.sql(f"select ticker, execution_date from splits where ticker in ({tl}) and execution_date >= '2022-10-01'").df()
SPL = {t: np.array(pd.to_datetime(g.execution_date).values, dtype='datetime64[D]') for t, g in sp.groupby('ticker')}
ud = pd.read_parquet('data/options/universe_daily.parquet', columns=['date', 'underlying', 'in_universe'])
ud['date'] = pd.to_datetime(ud.date)
UNI = {t: set(g.date[g.in_universe]) for t, g in ud.groupby('underlying')}
print(f"inputs loaded {time.time() - t0:.0f}s: {len(roots)} roots, {len(TD)} trading days {TD[0]}..{TD[-1]}, "
      f"spot for {len(SPOT)} tickers, earnings for {len(EARN)}, splits rows {len(sp)}", flush=True)


def iv_straddle(price, S, K, T):
    """BS r=q=0 vol of a straddle price; NaN at/below intrinsic or >= 10."""
    lo = np.full(len(price), 1e-4); hi = np.full(len(price), 10.0); sT = np.sqrt(T); lk = np.log(S / K)
    for _ in range(50):
        m = (lo + hi) / 2; v = m * sT
        d1 = (lk + 0.5 * v * v) / v; d2 = d1 - v
        cl = S * ndtr(d1) - K * ndtr(d2)
        st = 2 * cl - S + K
        up = st > price
        hi = np.where(up, m, hi); lo = np.where(up, lo, m)
    iv = (lo + hi) / 2
    return np.where((price > np.abs(S - K) + 1e-9) & (iv < 9.9) & (iv > 2e-4), iv, np.nan)


def count_in(arr, lo, hi):  # events with lo < x <= hi
    if arr is None or len(arr) == 0: return np.zeros(len(lo), int)
    return np.searchsorted(arr, hi, 'right') - np.searchsorted(arr, lo, 'right')


def next_after(arr, d):
    if arr is None or len(arr) == 0: return np.full(len(d), np.datetime64('NaT'), 'datetime64[D]')
    i = np.searchsorted(arr, d, 'right')
    out = np.full(len(d), np.datetime64('NaT'), 'datetime64[D]'); ok = i < len(arr); out[ok] = arr[i[ok]]
    return out


def td_index(d):  # trading-day index of a date (or of the last trading day <= d)
    return np.searchsorted(TD, d, 'right') - 1


def run(root):
    t = ROOT2T.get(root, root)
    if t not in SPOT: return root, 'no spot', 0, 0
    fs = sorted(glob.glob(f'{a.eod}/{root}__*.parquet'))
    q = duckdb.sql(f"""select distinct cast(date as date) date, cast(expiration as date) exp, strike, "right" r, bid, ask, volume
                       from read_parquet({fs})""").df()
    q['date'] = pd.to_datetime(q.date); q['exp'] = pd.to_datetime(q.exp)
    q = q[(q.ask > 0) & (q.bid >= 0) & (q.ask >= q.bid)]
    C = q[q.r == 'C'].drop(columns='r'); P = q[q.r == 'P'].drop(columns='r')
    k = ['date', 'exp', 'strike']
    ch = C.merge(P, on=k, suffixes=('_c', '_p')).drop_duplicates(k)
    s = SPOT[t]
    ch['S'] = ch.date.map(s)
    ch = ch.dropna(subset=['S'])
    ch['dte'] = (ch.exp - ch.date).dt.days
    ch = ch[ch.dte >= 1]
    if ch.empty: return root, 'empty', 0, 0
    # ATM per (date, exp)
    ch['dist'] = (ch.strike - ch.S).abs()
    atm = ch.sort_values(k[:2] + ['dist', 'strike']).drop_duplicates(k[:2]).drop(columns='dist').reset_index(drop=True)
    atm['st_mid'] = (atm.bid_c + atm.ask_c + atm.bid_p + atm.ask_p) / 2
    atm['st_bid'] = atm.bid_c + atm.bid_p
    atm['st_ask'] = atm.ask_c + atm.ask_p
    atm['iv'] = iv_straddle(atm.st_mid.values, atm.S.values, atm.strike.values, atm.dte.values / 365.0)
    # ---- features per date ----
    v = atm.dropna(subset=['iv']).sort_values(['date', 'dte'])
    rows = []
    for d, g in v.groupby('date', sort=False):
        x = g.dte.values.astype(float); y = g.iv.values
        iv30 = np.interp(30, x, y); iv45 = np.interp(45, x, y)
        rows.append((d, len(x), x[0], y[0], iv30, iv45, (iv45 - y[0]) / (45 - x[0]) if x[0] < 45 else np.nan))
    F = pd.DataFrame(rows, columns=['date', 'n_exp', 'dte_front', 'iv_front', 'iv30', 'iv45', 'ts_slope_0_45'])
    F = F.merge(YZ.get(t, pd.DataFrame(columns=['yz20', 'yz30', 'gap_days_30', 'max_on_30', 'avg_vol30', 'avg_dvol30']))
                .rename(columns={'yz30': 'rv30', 'yz20': 'rv20'}), left_on='date', right_index=True, how='left')
    F['iv30_rv30'] = F.iv30 / F.rv30
    F.insert(0, 'root', root); F.insert(1, 'ticker', t)
    dd = np.array(F.date.values, dtype='datetime64[D]')
    F['in_universe'] = F.date.isin(UNI.get(t, set()))
    F['has_earn_src'] = t in EARN
    nr = next_after(EARN.get(t), dd)
    F['next_react'] = pd.to_datetime(nr)
    F['tdays_to_react'] = np.where(np.isnat(nr), np.nan, td_index(nr) - td_index(dd))
    # ---- trades ----
    sp_idx = s.index.values.astype('datetime64[D]'); sp_val = s.values
    def settle(expd):  # last close <= expiry, and the date used
        i = np.searchsorted(sp_idx, expd, 'right') - 1
        ok = i >= 0
        px = np.where(ok, sp_val[np.clip(i, 0, None)], np.nan); dt = np.where(ok, sp_idx[np.clip(i, 0, None)], np.datetime64('NaT'))
        return px, dt
    last_spot = sp_idx[-1]
    T = atm.copy()
    ed_ = np.array(T.exp.values, dtype='datetime64[D]'); en_ = np.array(T.date.values, dtype='datetime64[D]')
    T = T[ed_ <= last_spot].reset_index(drop=True)
    ed_ = np.array(T.exp.values, dtype='datetime64[D]'); en_ = np.array(T.date.values, dtype='datetime64[D]')
    ST, sdt = settle(ed_)
    T['S_T'] = ST; T['settle_date'] = pd.to_datetime(sdt)
    T['stale_settle_days'] = (ed_ - sdt).astype(int)
    T['payoff'] = (T.S_T - T.strike).abs()
    T['pnl_mid'] = T.st_mid - T.payoff
    T['pnl_nat'] = T.st_bid - T.payoff
    T['earn_in_hold'] = count_in(EARN.get(t), en_, ed_)
    T['split_in_hold'] = count_in(np.sort(SPL.get(t, np.array([], 'datetime64[D]'))), en_, ed_) > 0
    T['ticker'] = t
    S_cols = ['root', 'ticker', 'date', 'exp', 'dte', 'strike', 'S', 'bid_c', 'ask_c', 'bid_p', 'ask_p', 'volume_c', 'volume_p',
              'st_mid', 'st_bid', 'st_ask', 'iv', 'S_T', 'settle_date', 'stale_settle_days', 'payoff', 'pnl_mid', 'pnl_nat',
              'earn_in_hold', 'split_in_hold']
    T['root'] = root
    STR = T[S_cols]
    # calendars: front = each atm row (dte <= max), back = expiry closest to front+30 in [front+21, front+45]
    fr = T[T.dte <= a.max_front_dte][['root', 'ticker', 'date', 'exp', 'dte', 'S', 'iv', 'S_T', 'settle_date',
                                      'earn_in_hold', 'split_in_hold']]
    ex = ch[['date', 'exp', 'dte']].drop_duplicates()
    cb = fr.merge(ex.rename(columns={'exp': 'exp_b', 'dte': 'dte_b'}), on='date')
    cb = cb[(cb.dte_b >= cb.dte + 21) & (cb.dte_b <= cb.dte + 45)]
    cb['gapd'] = (cb.dte_b - cb.dte - 30).abs()
    cb = cb.sort_values(['date', 'exp', 'gapd', 'dte_b']).drop_duplicates(['date', 'exp']).drop(columns='gapd')
    # the strike: nearest S among those quoted (both rights) in the front AND the back (weekly half-strikes vs monthly $5)
    leg = ch[['date', 'exp', 'strike', 'bid_c', 'ask_c', 'bid_p', 'ask_p']]
    cb = cb.merge(leg, on=['date', 'exp'], how='inner')
    cb = cb.merge(leg.rename(columns={'exp': 'exp_b', 'bid_c': 'bbid_c', 'ask_c': 'bask_c', 'bid_p': 'bbid_p', 'ask_p': 'bask_p'}),
                  on=['date', 'exp_b', 'strike'], how='inner')
    cb['dist'] = (cb.strike - cb.S).abs()
    cb = cb.sort_values(['date', 'exp', 'dist', 'strike']).drop_duplicates(['date', 'exp']).drop(columns='dist')
    # exit: back leg quote on the settle date (last trading day <= front expiry)
    cb = cb.merge(leg.rename(columns={'date': 'settle_date', 'exp': 'exp_b', 'bid_c': 'xbid_c', 'ask_c': 'xask_c',
                                      'bid_p': 'xbid_p', 'ask_p': 'xask_p'}), on=['settle_date', 'exp_b', 'strike'], how='left')
    iv_b = iv_straddle(((cb.bbid_c + cb.bask_c + cb.bbid_p + cb.bask_p) / 2).values, cb.S.values, cb.strike.values,
                       cb.dte_b.values / 365.0)
    cb['iv_b'] = iv_b
    intr_c = (cb.S_T - cb.strike).clip(lower=0); intr_p = (cb.strike - cb.S_T).clip(lower=0)
    for r_, ic in (('c', intr_c), ('p', intr_p)):
        fm = (cb[f'bid_{r_}'] + cb[f'ask_{r_}']) / 2; bm = (cb[f'bbid_{r_}'] + cb[f'bask_{r_}']) / 2
        xm = (cb[f'xbid_{r_}'] + cb[f'xask_{r_}']) / 2
        cb[f'debit_mid_{r_}'] = bm - fm
        cb[f'debit_nat_{r_}'] = cb[f'bask_{r_}'] - cb[f'bid_{r_}']
        cb[f'pnl_mid_{r_}'] = xm - ic - cb[f'debit_mid_{r_}']
        cb[f'pnl_nat_{r_}'] = cb[f'xbid_{r_}'] - ic - cb[f'debit_nat_{r_}']
    for m in ('debit_mid', 'debit_nat', 'pnl_mid', 'pnl_nat'):
        cb[f'{m}_s'] = cb[f'{m}_c'] + cb[f'{m}_p']
    eb = np.array(cb.exp_b.values, dtype='datetime64[D]'); ef = np.array(cb.exp.values, dtype='datetime64[D]')
    cb['earn_in_back_only'] = count_in(EARN.get(t), ef, eb)
    cb['exit_quote_ok'] = cb.xask_c.notna() & cb.xask_p.notna()
    os.makedirs(f'{a.out}/parts', exist_ok=True)
    F.to_parquet(f'{a.out}/parts/F_{root}.parquet', index=False)
    STR.to_parquet(f'{a.out}/parts/S_{root}.parquet', index=False)
    cb.to_parquet(f'{a.out}/parts/C_{root}.parquet', index=False)
    return root, 'ok', len(STR), len(cb)


if __name__ == '__main__':
    n = 0; tot = [0, 0]
    with Pool(a.workers) as pool:
        for r, st, ns, nc in pool.imap_unordered(run, roots):
            n += 1; tot[0] += ns; tot[1] += nc
            if st != 'ok': print(f"  {r}: {st}", flush=True)
            if n % 50 == 0: print(f"{n}/{len(roots)} roots, {tot[0]:,} straddles {tot[1]:,} calendars, {time.time() - t0:.0f}s", flush=True)
    for nm, p in (('features', 'F'), ('straddles', 'S'), ('calendars', 'C')):
        duckdb.sql(f"copy (select * from read_parquet('{a.out}/parts/{p}_*.parquet', union_by_name=true)) to '{a.out}/{nm}.parquet' (format parquet)")
        print(nm, duckdb.sql(f"select count(*) from '{a.out}/{nm}.parquet'").fetchone()[0], flush=True)
    print(f"done {time.time() - t0:.0f}s")
