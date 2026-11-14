#!/usr/bin/env python3
"""DAILY-REBALANCED DELTA HEDGE for the single-leg short_dated.py trades (user 2026-11-13: does rebalancing the hedge
every day beat the static one?).

For every contract a trade uses (AP / AC / P25 / C25), each day d from its first trade date to its expiry (xd):
delta_d = BS delta from that day's closing mid IV (r=q=0, T = trading days to xd / 252); no IV (price at / below
intrinsic) -> -1 / +1 when in the money, else 0; a day without a two-sided quote keeps the previous delta (the row is
absent: the increment runs to the contract's next quoted day). Hedge P&L per share of option (the hedge = +delta shares
for the SHORT option, rebalanced at each close):  sum over d in [t, xd) of delta_d x (S(next d) - S(d)).
  hd     = that sum, per trade (same sign convention as short_dated's static dl x (Sx - S));
  hs_chk = the same with delta frozen at the entry's (= the static hedge: a self-check);
  turn   = shares traded per share of option over the life: |delta_t| + sum |delta_d - delta_prev| + |delta_last|;
  last_q / vanish = the contract's last day with an ask; vanish = none on the expiry day nor the day before (a corporate
  action re-rooted it, e.g. WOLF -> WOLF1 2025-09-29: the spot-intrinsic settle is then wrong) -> sd_common drops it.
Writes <out>/hedge.parquet keyed (root, date, exp, K, r, inst).
    cd research && python3 scripts/options/hedge_daily.py > logs/options_hedge_daily.log 2>&1
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
from multiprocessing import Pool
from scipy.special import ndtr
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from survey_inputs import load
from tr_index import add_tr

ETFS = 'SPY,QQQ,IWM,DIA,GLD,TLT,SLV,XLF,EEM,HYG,SMH,XLE,USO,GDX,TQQQ,ARKK,FXI,EWZ,KRE,XBI'
ap = argparse.ArgumentParser()
ap.add_argument('--eod', default='data/options/market_full')
ap.add_argument('--trades', default='data/options/short_dated/trades.parquet')
ap.add_argument('--out', default='data/options/short_dated')
ap.add_argument('--roots', default='')
ap.add_argument('--etfs', default=ETFS)
ap.add_argument('--workers', type=int, default=4)
a = ap.parse_args()
t0_ = time.time()
I = load(a.eod, a.roots)
TD = I['TD']; SPOT = dict(I['SPOT'])
etfs = [x for x in a.etfs.split(',') if x]
if etfs:
    tl = ','.join(f"'{x}'" for x in etfs)
    da = I['c'].sql(f"select ticker, date, close from daily_adjusted where ticker in ({tl}) and date >= '2011-06-01' order by ticker, date").df()
    da['date'] = pd.to_datetime(da.date)
    for r, g in da.groupby('ticker'): SPOT[r] = g.set_index('date').close
sel = ','.join(repr(r) for r in (I['roots'] + etfs)) if (a.roots or a.etfs != ETFS) else None
TR = duckdb.sql(f"""select root, date, exp, xd, K, r, inst from '{a.trades}' where inst in ('AP','AC','P25','C25')
                    {f'and root in ({sel})' if sel else ''}""").df()
for c in ('date', 'exp', 'xd'): TR[c] = pd.to_datetime(TR[c])
TRg = dict(tuple(TR.groupby('root')))
roots = sorted(TRg)
print(f'inputs {time.time() - t0_:.0f}s: {len(roots)} roots, {len(TR):,} trades', flush=True)


def implied(price, S, K, T, call):
    lo = np.full(len(price), 1e-4); hi = np.full(len(price), 8.0); sT = np.sqrt(T); lk = np.log(S / K)
    for _ in range(40):
        m = (lo + hi) / 2; v = m * sT
        d1 = (lk + 0.5 * v * v) / v; c = S * ndtr(d1) - K * ndtr(d1 - v)
        p = np.where(call, c, c - S + K)
        up = p > price; hi = np.where(up, m, hi); lo = np.where(up, lo, m)
    iv = (lo + hi) / 2
    intr = np.where(call, np.maximum(S - K, 0), np.maximum(K - S, 0))
    return np.where((price > intr + 1e-9) & (price < np.where(call, S, K)) & (iv < 7.9) & (iv > 2e-4), iv, np.nan)


def run(root):
    T_ = TRg[root]; S = SPOT.get(root)
    if S is None: return None
    C = T_.groupby(['exp', 'K', 'r']).agg(first=('date', 'min'), xd=('xd', 'first')).reset_index()
    files = sorted(glob.glob(f'{a.eod}/{root}__*.parquet'))
    lo, hi = C['first'].min(), C.xd.max()
    files = [f for f in files if pd.Timestamp(os.path.basename(f).split('__')[1][:8]) <= hi
             and pd.Timestamp(os.path.basename(f).split('__')[1][:8]) >= lo - pd.Timedelta(days=100)]
    out = []
    for y in sorted({int(os.path.basename(f).split('__')[1][:4]) for f in files}):
        Cy = C[C['first'].dt.year == y]           # the contracts first traded in year y (paths may run into y+1)
        if Cy.empty: continue
        fy = [f for f in files if int(os.path.basename(f).split('__')[1][:4]) in (y, y + 1)]
        con = duckdb.connect(); con.register('cy', Cy)
        q = con.sql(f"""select cast(o.date as date) date, cast(o.expiration as date) exp, o.strike K, o."right" r, o.bid, o.ask,
                         cy.first, cy.xd
                  from read_parquet({fy}) o join cy on cast(o.expiration as date) = cast(cy.exp as date) and o.strike = cy.K
                       and o."right" = cy.r
                  where cast(o.date as date) >= cast(cy.first as date) and cast(o.date as date) <= cast(cy.xd as date)
                    and o.ask > 0""").df()
        con.close()
        if q.empty: continue
        for c in ('date', 'exp', 'first', 'xd'): q[c] = pd.to_datetime(q[c])
        # the contract's LAST quoted day (any ask): a corporate action moves it to a new root (WOLF -> WOLF1) before expiry
        lastq = q.groupby(['exp', 'K', 'r']).date.max().rename('last_q').reset_index()
        q = q[(q.bid > 0) & (q.ask >= q.bid)]
        if q.empty: continue
        q['S'] = q.date.map(S); q = q.dropna(subset=['S'])
        di = np.searchsorted(TD, q.date.values.astype('datetime64[D]'), 'right') - 1
        xi = np.searchsorted(TD, q.xd.values.astype('datetime64[D]'), 'right') - 1
        Tn = np.maximum(xi - di, 0.25) / 252
        call = (q.r == 'C').values
        mid = ((q.bid + q.ask) / 2).values
        iv = implied(mid, q.S.values, q.K.values, Tn, call)
        sT = iv * np.sqrt(Tn); d1 = (np.log(q.S.values / q.K.values) + 0.5 * sT * sT) / sT
        dl = np.where(call, ndtr(d1), ndtr(d1) - 1)
        itm = np.where(call, q.S.values > q.K.values, q.S.values < q.K.values)
        q['delta'] = np.where(np.isfinite(dl), dl, np.where(itm, np.where(call, 1.0, -1.0), 0.0))
        q = q.sort_values(['exp', 'K', 'r', 'date']).reset_index(drop=True)
        g = q.groupby(['exp', 'K', 'r'], sort=False)
        nxt = g.date.shift(-1)
        nxt = nxt.where(nxt.notna(), q.xd).where(lambda s: s <= q.xd, q.xd)
        q['Snext'] = nxt.map(S).values
        q['inc'] = (q.delta * (q.Snext - q.S)).fillna(0)
        q['cum'] = g.inc.cumsum(); q['cprev'] = q.cum - q.inc
        q['ddel'] = g.delta.diff().abs().fillna(0)
        q['cturn'] = g.ddel.cumsum()
        fin = g.agg(cfin=('cum', 'last'), tfin=('cturn', 'last'), dlast=('delta', 'last')).reset_index()
        T2 = T_[T_['date'].dt.year == y].merge(q[['exp', 'K', 'r', 'date', 'cprev', 'cturn', 'delta', 'S']], on=['exp', 'K', 'r', 'date'], how='inner')
        T2 = T2.merge(fin, on=['exp', 'K', 'r']).merge(lastq, on=['exp', 'K', 'r'], how='left')
        xi2 = np.searchsorted(TD, T2.xd.values.astype('datetime64[D]'), 'right') - 1
        T2['vanish'] = T2.last_q.values.astype('datetime64[D]') < TD[np.maximum(xi2 - 1, 0)]   # no quote on xd nor the day before
        T2['hd'] = T2.cfin - T2.cprev
        T2['hs_chk'] = T2.delta * (T2.xd.map(S) - T2.S)
        T2['turn'] = T2.delta.abs() + (T2.tfin - T2.cturn) + T2.dlast.abs()
        out.append(T2[['root', 'date', 'exp', 'K', 'r', 'inst', 'hd', 'hs_chk', 'turn', 'last_q', 'vanish']])
        del q
    return pd.concat(out, ignore_index=True) if out else None


if __name__ == '__main__':
    os.makedirs(a.out, exist_ok=True)
    R = []
    with Pool(a.workers, maxtasksperchild=20) as pool:
        for i, r in enumerate(pool.imap_unordered(run, roots), 1):
            if r is not None: R.append(r)
            if i % 50 == 0 or i == len(roots): print(f'{i}/{len(roots)} roots  {time.time() - t0_:.0f}s', flush=True)
    R = pd.concat(R, ignore_index=True)
    R.to_parquet(f'{a.out}/hedge.parquet', index=False)
    print(f'wrote {len(R):,} trades  {time.time() - t0_:.0f}s')
