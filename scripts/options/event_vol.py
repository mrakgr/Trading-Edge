#!/usr/bin/env python3
"""EVENT-VOL LONG: buy the earnings straddle when the IMPLIED earnings move is cheap vs the stock's own history
(user 2026-11-11; the Volatility Vibes long-vol play, generalized).

Decomposition (per day t, ATM straddle mid IVs, Black-Scholes r=q=0, T in TRADING days / 252):
  E1 = the first expiry on/after the reaction day R (holds the event), E2 = the next listed expiry after E1 with
  E2 - E1 <= 45 calendar days and E2 before the NEXT reaction day (holds the same single event).
  total variance w_i = iv_i^2 T_i = sa2 * T_i + se2     (the event = a JUMP, no day of its own)
  ambient  sa2 = (w2 - w1) / (T2 - T1)      = the forward variance E1 -> E2 (the event cancels)
  event    se2 = w1 - sa2 * T1;  imp_move = sqrt(se2)   (the implied earnings-day move, fraction of S; NaN if se2 <= 0)
  check    iv0 = the ATM IV of the last expiry BEFORE R with >= 2 trading days (pure ambient when it exists)
History (known at entry): hist_rms = RMS of the stock's log close-to-close move on its previous 4-8 reaction days
  (daily_adjusted total-return V; reaction days strictly before entry).  ratio = imp_move / hist_rms.
Realized: real_move = |log V_R / V_{R-1}|.

Trade: entry at the close k = 5 / 10 / 15 trading days before R: BUY the ATM straddle (strike K nearest S) of E1.
Exits (all at a day's close NBBO, the position's own K/E1 quotes):
  A  R-1 close (before the event; VV's)           B  R close (just through the event)
  C  E1's expiry (settles at intrinsic)            D  target: the first day t in (t0, R-1] with ratio_t >= 1 (the event
                                                      priced fair) -> sell; else hold through -> R close
  E  P&L target: the first day in (t0, R-1] with mid >= 1.25 x entry mid -> sell; else R-1
Fill f: buy at mid + f*half-spread, sell at mid - f*half-spread (expiry: intrinsic, no spread). f = 0, .25, .5, 1.
Delta hedge (secondary): short delta_t = 2N(d1)-1 of the straddle (its own IV that day) shares at each close t until
  the exit, hedge P&L = sum -delta_t (S_{t+1} - S_t); stock costs ignored.  hedged = unhedged + hedge_X.
P&L per share, as % of S_t0 and of the premium paid.  Excluded: a split in (t0, exit].

    cd research && python3 scripts/options/event_vol.py > logs/options_event_vol.log 2>&1; echo "exit $?"
writes data/options/event_vol/trades.parquet (one row per event x k)
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
from multiprocessing import Pool
from scipy.special import ndtr

ap = argparse.ArgumentParser()
ap.add_argument('--eod', default='data/options/eod_universe')
ap.add_argument('--out', default='data/options/event_vol')
ap.add_argument('--roots', default='')
ap.add_argument('--workers', type=int, default=6)
ap.add_argument('--earnings', default='data/options/earnings_8k202.parquet')
ap.add_argument('--releases-only', action='store_true', help='keep is_release filings (earnings_scheduled.py)')
a = ap.parse_args()
KS = [1, 5, 10, 15]; FS = [0, 0.25, 0.5, 1]   # k=1: entry at R-1's close = the VV short's entry (exits B, C only)

t0_ = time.time()
tick = sorted({l.split('\t')[0].strip() for l in open('data/options/universe_tickers.tsv') if l.strip()})
ROOT2T = {t.replace('.', ''): t for t in tick}
roots = sorted({os.path.basename(f).split('__')[0] for f in glob.glob(f'{a.eod}/*.parquet')})
if a.roots: roots = a.roots.split(',')

agg_files = sorted(f for f in glob.glob('data/daily_aggregates/*.csv.gz') if os.path.basename(f)[:10] >= '2022-06-01')
TD = np.array(sorted(os.path.basename(f)[:10] for f in agg_files), dtype='datetime64[D]')
tl = ','.join(f"'{t}'" for t in tick)
spot = duckdb.sql(f"""select ticker, cast(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) as date) as date,
                      close from read_csv({agg_files}, filename=true, columns={{'ticker':'VARCHAR','volume':'DOUBLE',
                      'open':'DOUBLE','close':'DOUBLE','high':'DOUBLE','low':'DOUBLE','window_start':'BIGINT',
                      'transactions':'BIGINT'}}, header=true) where ticker in ({tl})""").df()
spot['date'] = pd.to_datetime(spot.date)
SPOT = {t: g.set_index('date').close.sort_index() for t, g in spot.groupby('ticker')}

c = duckdb.connect('data/trading.db', read_only=True)
da = c.sql(f"""select ticker, date, close * n + cum_div V from daily_adjusted where ticker in ({tl}) and date >= '2014-06-01'
               order by ticker, date""").df()
da['date'] = pd.to_datetime(da.date)
VADJ = {t: g.set_index('date').V for t, g in da.groupby('ticker')}
sp = c.sql(f"select ticker, execution_date from splits where ticker in ({tl}) and execution_date >= '2022-06-01'").df()
SPL = {t: np.array(pd.to_datetime(g.execution_date).values, dtype='datetime64[D]') for t, g in sp.groupby('ticker')}

# reaction days: all EDGAR history (2015 ->) on the FULL session calendar (daily_adjusted SPY)
TDALL = np.array(sorted(c.sql("select distinct date from daily_adjusted where ticker = 'SPY' and date >= '2014-06-01'").df().date),
                 dtype='datetime64[D]')
e = pd.read_parquet(a.earnings)
if a.releases_only: e = e[e.is_release]
e = e[['ticker', 'session', 'et_date']]
ed = np.array(pd.to_datetime(e.et_date).values, dtype='datetime64[D]')
ii = np.where(e.session.values == 'AMC', np.searchsorted(TDALL, ed, 'right'), np.searchsorted(TDALL, ed, 'left'))
e = e[ii < len(TDALL)].copy(); e['R'] = TDALL[ii[ii < len(TDALL)]]
EARN = {t: np.unique(np.array(g.R.values, dtype='datetime64[D]')) for t, g in e.groupby('ticker')}
yz = pd.read_parquet('data/options/yz_vol.parquet', columns=['ticker', 'date', 'yz30'])
YZ = {t: g.set_index('date').yz30 for t, g in yz.groupby('ticker')}
print(f'inputs {time.time() - t0_:.0f}s: {len(roots)} roots, TD {TD[0]}..{TD[-1]}, earnings for {len(EARN)}', flush=True)


def iv_straddle(price, S, K, T):
    lo = np.full(len(price), 1e-4); hi = np.full(len(price), 10.0); sT = np.sqrt(T); lk = np.log(S / K)
    for _ in range(50):
        m = (lo + hi) / 2; v = m * sT
        d1 = (lk + 0.5 * v * v) / v; d2 = d1 - v
        cl = S * ndtr(d1) - K * ndtr(d2)
        up = 2 * cl - S + K > price
        hi = np.where(up, m, hi); lo = np.where(up, lo, m)
    iv = (lo + hi) / 2
    return np.where((price > np.abs(S - K) + 1e-9) & (iv < 9.9) & (iv > 2e-4), iv, np.nan)


def tdi(d):  # index of the last session <= d
    return np.searchsorted(TD, np.asarray(d, dtype='datetime64[D]'), 'right') - 1


def run(root):
    t = ROOT2T.get(root, root)
    if t not in SPOT or t not in EARN or t not in VADJ: return []
    fs = sorted(glob.glob(f'{a.eod}/{root}__*.parquet'))
    q = duckdb.sql(f"""select distinct cast(date as date) date, cast(expiration as date) exp, strike, "right" r, bid, ask
                       from read_parquet({fs}) where ask > 0 and bid >= 0 and ask >= bid""").df()
    q['date'] = pd.to_datetime(q.date); q['exp'] = pd.to_datetime(q.exp)
    k = ['date', 'exp', 'strike']
    ch = q[q.r == 'C'].drop(columns='r').merge(q[q.r == 'P'].drop(columns='r'), on=k, suffixes=('_c', '_p')).drop_duplicates(k)
    s = SPOT[t]; ch['S'] = ch.date.map(s); ch = ch.dropna(subset=['S'])
    ch['ti'] = tdi(ch.date.values); ch['tdte'] = tdi(ch.exp.values) - ch.ti
    ch = ch[ch.tdte >= 1].copy()
    if ch.empty: return []
    ch['mid'] = (ch.bid_c + ch.ask_c + ch.bid_p + ch.ask_p) / 2
    ch['dist'] = (ch.strike - ch.S).abs()
    atm = ch.sort_values(['date', 'exp', 'dist', 'strike']).drop_duplicates(['date', 'exp']).copy()
    atm['iv'] = iv_straddle(atm.mid.values, atm.S.values, atm.strike.values, atm.tdte.values / 252)
    A = {d: g.set_index('exp') for d, g in atm.groupby('date')}
    POS = ch.set_index(k).sort_index()
    V = VADJ[t]; vi = V.index.values.astype('datetime64[D]'); vv = V.values
    def vat(d):
        j = np.searchsorted(vi, d, 'right') - 1
        return vv[j] if j >= 0 and vi[j] == d else np.nan
    R_all = EARN[t]
    # reaction-day log moves of every event (for the history)
    mv = {}
    for R in R_all:
        j = np.searchsorted(vi, R)
        if j < len(vi) and vi[j] == R and j > 0: mv[R] = abs(np.log(vv[j] / vv[j - 1]))
    splits = SPL.get(t, np.array([], 'datetime64[D]'))
    rows = []

    def decomp(d, R, Rnext):
        g = A.get(pd.Timestamp(d))
        if g is None: return None
        ex = g.index.values.astype('datetime64[D]')
        i1 = np.searchsorted(ex, R)
        if i1 >= len(ex): return None
        e1 = ex[i1]; c2 = [x for x in ex[i1 + 1:] if (x - e1).astype(int) <= 45 and (Rnext is None or x < Rnext)]
        r1 = g.loc[pd.Timestamp(e1)]
        out = dict(e1=pd.Timestamp(e1), iv1=r1.iv, T1=r1.tdte, S=r1.S)
        pre = [x for x in ex[:i1] if g.loc[pd.Timestamp(x)].tdte >= 2]
        out['iv0'] = g.loc[pd.Timestamp(pre[-1])].iv if pre else np.nan
        if c2:
            r2 = g.loc[pd.Timestamp(c2[0])]
            T1, T2 = r1.tdte / 252, r2.tdte / 252; w1, w2 = r1.iv ** 2 * T1, r2.iv ** 2 * T2
            sa2 = (w2 - w1) / (T2 - T1); se2 = w1 - sa2 * T1
            out.update(e2=pd.Timestamp(c2[0]), iv2=r2.iv, T2=r2.tdte, amb_vol=np.sqrt(sa2) if sa2 > 0 else np.nan,
                       se2=se2, imp_move=np.sqrt(se2) if se2 > 0 else np.nan)
        return out

    for n_, R in enumerate(R_all):
        iR = np.searchsorted(TD, R)
        if iR >= len(TD) or TD[iR] != R or iR < 16: continue
        Rnext = R_all[n_ + 1] if n_ + 1 < len(R_all) else None
        prev = [mv[x] for x in R_all[:n_] if x in mv][-8:]
        for kk in KS:
            t0 = TD[iR - kk]
            if R_all[:n_].size and R_all[n_ - 1] >= t0: continue  # previous event inside the window
            hist = [mv[x] for x in R_all[:n_] if x in mv and x < t0][-8:]
            hist_rms = np.sqrt(np.mean(np.square(hist))) if len(hist) >= 4 else np.nan
            d0 = decomp(t0, R, Rnext)
            if d0 is None or not np.isfinite(d0['iv1']): continue
            E1 = d0['e1']
            g0 = A[pd.Timestamp(t0)].loc[E1]; K = g0.strike; S0 = g0.S
            row = dict(root=root, ticker=t, R=pd.Timestamp(R), k=kk, t0=pd.Timestamp(t0), E1=E1, K=K, S0=S0,
                       hist_rms=hist_rms, n_hist=len(hist), real_move=mv.get(R, np.nan),
                       yz30=YZ.get(t, pd.Series(dtype=float)).get(pd.Timestamp(t0), np.nan),
                       **{f'{x}': d0.get(x, np.nan) for x in ['iv1', 'T1', 'iv2', 'T2', 'iv0', 'amb_vol', 'se2', 'imp_move']})
            row['ratio0'] = row['imp_move'] / hist_rms
            iE = tdi(E1)
            path = []  # (date, bid_st, ask_st, S, iv_pos, ratio_t)
            for j in range(iR - kk, min(iE, len(TD) - 1) + 1):
                d = TD[j]
                try: p = POS.loc[(pd.Timestamp(d), E1, K)]
                except KeyError: p = None
                Sd = s.get(pd.Timestamp(d), np.nan)
                if p is None:
                    path.append((d, np.nan, np.nan, Sd, np.nan, np.nan)); continue
                bid = p.bid_c + p.bid_p; ask = p.ask_c + p.ask_p; T = (iE - j) / 252
                ivp = iv_straddle(np.array([(bid + ask) / 2]), np.array([Sd]), np.array([K]), np.array([max(T, 1 / 252)]))[0] if T > 0 else np.nan
                rt = np.nan
                if j < iR:
                    dd = decomp(d, R, Rnext)
                    if dd is not None and np.isfinite(dd.get('imp_move', np.nan)): rt = dd['imp_move'] / hist_rms
                path.append((d, bid, ask, Sd, ivp, rt))
            P = pd.DataFrame(path, columns=['d', 'bid', 'ask', 'S', 'iv', 'ratio'])
            if P.empty or not np.isfinite(P.bid.iloc[0]): continue
            row['ent_bid'], row['ent_ask'] = P.bid.iloc[0], P.ask.iloc[0]
            # exits: index into P
            iA, iB = kk - 1, kk
            ST = s[s.index <= E1]; SE = ST.iloc[-1] if len(ST) else np.nan
            iD = next((i for i in range(1, kk) if np.isfinite(P.ratio.iloc[i]) and P.ratio.iloc[i] >= 1), iB)
            em = (P.bid.iloc[0] + P.ask.iloc[0]) / 2
            iE_ = next((i for i in range(1, kk) if np.isfinite(P.bid.iloc[i]) and (P.bid.iloc[i] + P.ask.iloc[i]) / 2 >= 1.25 * em), iA)
            row['exit_D_day'] = iD; row['exit_E_day'] = iE_
            for X, ix in [('A', iA), ('B', iB), ('D', iD), ('E', iE_)]:
                row[f'x_bid_{X}'] = P.bid.iloc[ix] if ix < len(P) else np.nan
                row[f'x_ask_{X}'] = P.ask.iloc[ix] if ix < len(P) else np.nan
            row['x_C'] = abs(SE - K)
            # delta hedge, closes t0 .. exit-1
            dl = np.where(np.isfinite(P.iv), 2 * ndtr((np.log(P.S / K) + 0.5 * P.iv ** 2 * np.maximum((iE - (iR - kk + np.arange(len(P)))) / 252, 1e-9))
                                                     / (P.iv * np.sqrt(np.maximum((iE - (iR - kk + np.arange(len(P)))) / 252, 1e-9)))) - 1, np.nan)
            dl = pd.Series(dl).ffill().fillna(0).values
            dS = np.diff(P.S.values, append=np.nan)
            hp = -dl * dS
            for X, ix in [('A', iA), ('B', iB), ('C', len(P) - 1), ('D', iD), ('E', iE_)]:
                row[f'hedge_{X}'] = np.nansum(hp[:ix]) if ix <= len(P) else np.nan
            exit_last = TD[min(iE, len(TD) - 1)]
            row['split_in_hold'] = bool(((splits > t0) & (splits <= exit_last)).any())
            row['E1_settled'] = E1 <= pd.Timestamp(TD[-1])
            rows.append(row)
    return rows


if __name__ == '__main__':
    os.makedirs(a.out, exist_ok=True)
    with Pool(a.workers) as pool:
        out = []
        for i, r in enumerate(pool.imap_unordered(run, roots), 1):
            out += r
            if i % 50 == 0 or i == len(roots): print(f'{i}/{len(roots)} roots, {len(out)} rows, {time.time() - t0_:.0f}s', flush=True)
    T = pd.DataFrame(out)
    mid = lambda b, a_: (b + a_) / 2
    for f in FS:
        tg = int(f * 100)
        ent = mid(T.ent_bid, T.ent_ask) + f * (T.ent_ask - T.ent_bid) / 2
        T[f'cost_{tg}'] = ent
        for X in 'ABDE':
            T[f'pnl_{X}_{tg}'] = mid(T[f'x_bid_{X}'], T[f'x_ask_{X}']) - f * (T[f'x_ask_{X}'] - T[f'x_bid_{X}']) / 2 - ent
        T[f'pnl_C_{tg}'] = T.x_C - ent
        # SHORT straddle: sell at mid - f*hs, buy back at mid + f*hs (expiry: intrinsic)
        cred = mid(T.ent_bid, T.ent_ask) - f * (T.ent_ask - T.ent_bid) / 2
        T[f'cred_{tg}'] = cred
        for X in 'ABDE':
            T[f'spnl_{X}_{tg}'] = cred - (mid(T[f'x_bid_{X}'], T[f'x_ask_{X}']) + f * (T[f'x_ask_{X}'] - T[f'x_bid_{X}']) / 2)
        T[f'spnl_C_{tg}'] = cred - T.x_C
    T.to_parquet(f'{a.out}/trades.parquet', index=False)
    print(f'wrote {len(T)} rows -> {a.out}/trades.parquet  {time.time() - t0_:.0f}s')
