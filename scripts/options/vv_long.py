#!/usr/bin/env python3
"""VV PRE-EARNINGS LONG VOL (Volatility Vibes video, user 2026-11-12): buy the ATM straddle ~14 days before a scheduled
earnings release, sell it at the close before the announcement; select on the implied earnings move vs its own history.

Entry t0 = the session closest to (announcement date - 14 calendar days), within +-4 days (ties -> the earlier one).
Exit = the close of R-1 = the last session before the reaction day R (BMO: the day before; AMC: the announcement day).
Instrument = the ATM straddle (strike nearest S) of the first MONTHLY expiry on/after R. Fills: buy mid + f*hs, sell mid - f*hs.

Implied earnings move at t0 (ATM straddle mid IVs, BS r=q=0, T in TRADING days/252), the event = ONE trading day:
  E1 = the first listed expiry on/after R:  iv1^2 T1 = s_amb^2 (T1 - d) + s_ev^2 d,  d = 1/252
  ambient (VV): the IV of the last expiry BEFORE R with >= 3 trading days to go if one exists ('pre'), else the forward
  vol E1 -> E2 (E2 = next expiry, <= 45 days after E1, before the next event): s_fwd^2 = (iv2^2 T2 - iv1^2 T1)/(T2 - T1).
  v_ev = iv1^2 T1 - s_amb^2 (T1 - d)  (= the event-day variance incl. a normal day);  iem = sqrt(2/pi) sqrt(v_ev)
  = the expected ABSOLUTE reaction-day move (fraction of S). iem_fwd / iem_pre = the same with one ambient source each.
Realized move of an event = |V_R / V_{R-1} - 1| (total-return adjusted close-to-close), daily_adjusted from 2014.
VV features (history = previous scheduled events of the name, strictly before this one):
  implied_to_last_implied_ratio  = iem / iem(previous event)      implied_hist_avg_implied_ratio = iem / mean(iem, all prev)
  gap_implied_last_realized      = iem - real(previous event)     gap_implied_hist_avg_realized  = iem - mean(real, all prev)
  (iem history needs options data -> from 2022-12; realized history goes back to 2015 EDGAR.)

    cd research && python3 scripts/options/vv_long.py > logs/options_vv_long.log 2>&1; echo "exit $?"
writes data/options/vv_long/trades.parquet (one row per event)
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); from tr_index import add_tr
from multiprocessing import Pool
from scipy.special import ndtr

ap = argparse.ArgumentParser()
ap.add_argument('--eod', default='data/options/eod_universe')
ap.add_argument('--out', default='data/options/vv_long')
ap.add_argument('--roots', default='')
ap.add_argument('--workers', type=int, default=6)
ap.add_argument('--full', action='store_true', help='market_full 2012-> with the PIT universe (survey_inputs.py)')
ap.add_argument('--earnings', default='data/options/earnings_sched.parquet')
ap.add_argument('--all-filings', action='store_true', help='keep non-release 2.02 filings too')
ap.add_argument('--offset', type=int, default=14, help='entry = announcement - offset calendar days')
a = ap.parse_args()
FS = [0, 0.25, 0.5, 1]; D1 = 1 / 252; SQ2PI = np.sqrt(2 / np.pi)

t0_ = time.time()
if a.full:
    from survey_inputs import load
    globals().update(load(a.eod, a.roots))
else:
    tick = sorted({l.split('\t')[0].strip() for l in open('data/options/universe_tickers.tsv') if l.strip()})
    ROOT2T = {t.replace('.', ''): t for t in tick}
    roots = sorted({os.path.basename(f).split('__')[0] for f in glob.glob(f'{a.eod}/*.parquet')})
    if a.roots: roots = a.roots.split(',')

    agg_files = sorted(f for f in glob.glob('data/daily_aggregates/*.csv.gz') if os.path.basename(f)[:10] >= '2022-06-01')
    TD = np.array(sorted(os.path.basename(f)[:10] for f in agg_files), dtype='datetime64[D]'); TDSET = {str(x) for x in TD}
    tl = ','.join(f"'{t}'" for t in tick)
    spot = duckdb.sql(f"""select ticker, cast(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})', 1) as date) as date,
                          close from read_csv({agg_files}, filename=true, columns={{'ticker':'VARCHAR','volume':'DOUBLE',
                          'open':'DOUBLE','close':'DOUBLE','high':'DOUBLE','low':'DOUBLE','window_start':'BIGINT',
                          'transactions':'BIGINT'}}, header=true) where ticker in ({tl})""").df()
    spot['date'] = pd.to_datetime(spot.date)
    SPOT = {t: g.set_index('date').close.sort_index() for t, g in spot.groupby('ticker')}

    c = duckdb.connect('data/trading.db', read_only=True)
    da = add_tr(c.sql(f"""select ticker, date, close, n, cum_div from daily_adjusted where ticker in ({tl}) and date >= '2014-06-01'
                   order by ticker, date""").df())   # causal total-return index (tr_index.py), NOT close*n + cum_div
    da['date'] = pd.to_datetime(da.date)
    VADJ = {t: g.set_index('date').V for t, g in da.groupby('ticker')}
    sp = c.sql(f"select ticker, execution_date from splits where ticker in ({tl}) and execution_date >= '2022-06-01'").df()
    SPL = {t: np.array(pd.to_datetime(g.execution_date).values, dtype='datetime64[D]') for t, g in sp.groupby('ticker')}
    TDALL = np.array(sorted(c.sql("select distinct date from daily_adjusted where ticker = 'SPY' and date >= '2014-06-01'").df().date),
                     dtype='datetime64[D]')
    TDALL = np.union1d(TDALL, TD)   # daily_adjusted can lag the daily aggregates by days
e = E_RAW.copy() if a.full else pd.read_parquet(a.earnings)
if not a.all_filings: e = e[e.is_release]
e = e[['ticker', 'session', 'et_date']].copy()
ed = np.array(pd.to_datetime(e.et_date).values, dtype='datetime64[D]')
ii = np.where(e.session.values == 'AMC', np.searchsorted(TDALL, ed, 'right'), np.searchsorted(TDALL, ed, 'left'))
ok = ii < len(TDALL); e = e[ok].copy(); e['R'] = TDALL[ii[ok]]; e['ann'] = ed[ok]
e = e.sort_values(['ticker', 'R']).drop_duplicates(['ticker', 'R'])
EARN = {t: (g.R.values.astype('datetime64[D]'), g.ann.values.astype('datetime64[D]')) for t, g in e.groupby('ticker')}
print(f'inputs {time.time() - t0_:.0f}s: {len(roots)} roots, TD {TD[0]}..{TD[-1]}, earnings for {len(EARN)}', flush=True)



def RSQL(r):   # --full: the root's universe window; earnings scripts: only dates near its events, no LEAPS (RAM)
    if not a.full: return ''
    w = f"cast(date as date) between '{WIN[r][0]}' and '{WIN[r][1]}' and "
    ev = EARN.get(r)
    if ev is None: return w
    Rs = ev[0] if isinstance(ev, tuple) else ev
    if not len(Rs): return w
    Rs = pd.to_datetime(Rs)
    rng = ' or '.join(f"cast(date as date) between '{(x - pd.Timedelta(days=30)).date()}' and '{(x + pd.Timedelta(days=60)).date()}'" for x in Rs)
    return w + f"({rng}) and cast(expiration as date) - cast(date as date) <= 150 and "

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


def is_monthly(x):
    x = pd.Timestamp(x)
    if x.weekday() == 5: return 16 <= x.day <= 22   # before 2015-02 the monthlies are dated the SATURDAY after the 3rd Friday
    if x.weekday() == 4: return 15 <= x.day <= 21
    if x.weekday() == 3:  # holiday Friday -> Thursday expiry
        f = np.datetime64((x + pd.Timedelta(days=1)).date(), 'D')
        return 15 <= x.day + 1 <= 21 and f not in TDSET
    return False


def monthly_after(R):
    """the standard monthly (3rd Friday) on/after R, as datetime64[D]"""
    d = pd.Timestamp(R)
    for k in range(3):
        m = (d + pd.offsets.MonthBegin(k)) if k else d.replace(day=1)
        m = m.replace(day=1); f = m + pd.Timedelta(days=(4 - m.weekday()) % 7 + 14)
        if f >= d: return np.datetime64(f.date(), 'D')


def run(root):
    t = ROOT2T.get(root, root)
    if t not in SPOT or t not in EARN or t not in VADJ: return []
    fs = sorted(glob.glob(f'{a.eod}/{root}__*.parquet'))
    q = duckdb.sql(f"""select distinct cast(date as date) date, cast(expiration as date) exp, strike, "right" r, bid, ask
                       from read_parquet({fs}) where {RSQL(root)}ask > 0 and bid >= 0 and ask >= bid""").df()
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
    R_all, ANN = EARN[t]
    mv = {}
    for R in R_all:
        j = np.searchsorted(vi, R)
        if j < len(vi) and vi[j] == R and j > 0: mv[R] = abs(vv[j] / vv[j - 1] - 1)
    splits = SPL.get(t, np.array([], 'datetime64[D]'))
    # realized VOL of every event's own trade window (daily_adjusted, 2015 ->; no options needed): entry = session closest
    # to ann - offset, window (t0, monthly expiry >= R] incl. the reaction day ('win') and (t0, R-1] ex-event ('pre')
    lr = np.diff(np.log(vv)); RV = {}
    for R, ann in zip(R_all, ANN):
        tg = ann - np.timedelta64(a.offset, 'D')
        j0 = np.searchsorted(vi, tg); cj = [j for j in (j0 - 1, j0) if 0 <= j < len(vi) and abs((vi[j] - tg).astype(int)) <= 4]
        jR = np.searchsorted(vi, R)
        if not cj or jR >= len(vi) or vi[jR] != R: continue
        j0 = min(cj, key=lambda j: (abs((vi[j] - tg).astype(int)), j))
        jE = np.searchsorted(vi, monthly_after(R), 'right') - 1
        if jR - j0 < 2 or jE < jR: continue
        rw = lr[j0:jE]; rp = lr[j0:jR - 1]
        RV[R] = (np.sqrt(252 * np.mean(rw ** 2)), np.sqrt(252 * np.mean(rp ** 2)) if len(rp) else np.nan)

    def decomp(d, R, Rnext):
        g = A.get(pd.Timestamp(d))
        if g is None: return None
        ex = g.index.values.astype('datetime64[D]')
        i1 = np.searchsorted(ex, R)
        if i1 >= len(ex): return None
        r1 = g.loc[pd.Timestamp(ex[i1])]
        if not np.isfinite(r1.iv): return None
        T1 = r1.tdte / 252; w1 = r1.iv ** 2 * T1
        out = dict(e1=pd.Timestamp(ex[i1]), iv1=r1.iv, T1=r1.tdte)
        pre = [x for x in ex[:i1] if g.loc[pd.Timestamp(x)].tdte >= 3]
        ivp = g.loc[pd.Timestamp(pre[-1])].iv if pre else np.nan
        out['iv_pre'] = ivp
        c2 = [x for x in ex[i1 + 1:] if (x - ex[i1]).astype(int) <= 45 and (Rnext is None or x < Rnext)]
        sf2 = np.nan
        if c2:
            r2 = g.loc[pd.Timestamp(c2[0])]
            T2 = r2.tdte / 252
            if np.isfinite(r2.iv) and T2 > T1: sf2 = (r2.iv ** 2 * T2 - w1) / (T2 - T1)
            out['iv2'] = r2.iv
        out['amb_fwd'] = np.sqrt(sf2) if sf2 > 0 else np.nan
        for nm, a2 in [('pre', ivp ** 2), ('fwd', sf2)]:
            v = w1 - a2 * (T1 - D1) if np.isfinite(a2) and a2 > 0 else np.nan
            out[f'iem_{nm}'] = SQ2PI * np.sqrt(v) if v > 0 else (0.0 if np.isfinite(v) else np.nan)
        out['iem'] = out['iem_pre'] if np.isfinite(ivp) else out['iem_fwd']
        out['amb'] = ivp if np.isfinite(ivp) else out['amb_fwd']
        return out

    rows = []
    for n_, (R, ann) in enumerate(zip(R_all, ANN)):
        iR = np.searchsorted(TD, R)
        if iR >= len(TD) or TD[iR] != R or iR < 20: continue
        Rnext = R_all[n_ + 1] if n_ + 1 < len(R_all) else None
        tgt = ann - np.timedelta64(a.offset, 'D')
        cand = [j for j in range(iR - 20, iR - 1) if abs((TD[j] - tgt).astype(int)) <= 4]
        if not cand: continue
        j0 = min(cand, key=lambda j: (abs((TD[j] - tgt).astype(int)), j))
        t0 = TD[j0]; iX = iR - 1
        if j0 >= iX: continue
        if n_ > 0 and R_all[n_ - 1] >= t0: continue
        d0 = decomp(t0, R, Rnext)
        if d0 is None: continue
        g0 = A[pd.Timestamp(t0)]
        ex = g0.index.values.astype('datetime64[D]')
        mon = [x for x in ex if x >= R and is_monthly(x)]
        if not mon: continue
        EM = pd.Timestamp(mon[0]); K = g0.loc[EM].strike; S0 = g0.loc[EM].S
        try:
            pe = POS.loc[(pd.Timestamp(t0), EM, K)]; px = POS.loc[(pd.Timestamp(TD[iX]), EM, K)]
        except KeyError: continue
        dX = decomp(TD[iX], R, Rnext)
        prev_R = R_all[:n_]
        reals = [mv[x] for x in prev_R if x in mv]
        rvw = [RV[x][0] for x in prev_R if x in RV]; rvp = [RV[x][1] for x in prev_R if x in RV and np.isfinite(RV[x][1])]
        lj = np.searchsorted(vi, t0, 'right') - 1; r30 = lr[max(lj - 21, 0):lj]
        row = dict(root=root, ticker=t, R=pd.Timestamp(R), ann=pd.Timestamp(ann), t0=pd.Timestamp(t0), exit=pd.Timestamp(TD[iX]),
                   cal_days=int((ann - t0).astype(int)), n_td=int(iX - j0), EM=EM, E1=d0['e1'], K=K, S0=S0, SX=px.S,
                   ent_bid=pe.bid_c + pe.bid_p, ent_ask=pe.ask_c + pe.ask_p, x_bid=px.bid_c + px.bid_p, x_ask=px.ask_c + px.ask_p,
                   real_move=mv.get(R, np.nan), last_real=mv.get(prev_R[-1], np.nan) if len(prev_R) else np.nan,
                   hist_avg_real=np.mean(reals) if reals else np.nan, n_real=len(reals),
                   iv_em=g0.loc[EM].iv, rv30=np.sqrt(252 * np.mean(r30 ** 2)) if len(r30) >= 15 else np.nan,
                   last_rv_win=RV[prev_R[-1]][0] if len(prev_R) and prev_R[-1] in RV else np.nan,
                   hist_avg_rv_win=np.mean(rvw) if rvw else np.nan, n_rv=len(rvw),
                   last_rv_pre=RV[prev_R[-1]][1] if len(prev_R) and prev_R[-1] in RV else np.nan,
                   hist_avg_rv_pre=np.mean(rvp) if rvp else np.nan,
                   iem_x=dX['iem'] if dX else np.nan, iv1_x=dX['iv1'] if dX else np.nan,
                   prev_R=pd.Timestamp(prev_R[-1]) if len(prev_R) else pd.NaT,
                   split_in_hold=bool(((splits > t0) & (splits <= TD[iX])).any()),
                   **{x: d0.get(x, np.nan) for x in ['iv1', 'T1', 'iv2', 'iv_pre', 'amb_fwd', 'amb', 'iem', 'iem_pre', 'iem_fwd']})
        rows.append(row)
    return rows


if __name__ == '__main__':
    os.makedirs(a.out, exist_ok=True)
    with Pool(a.workers) as pool:
        out = []
        for i, r in enumerate(pool.imap_unordered(run, roots), 1):
            out += r
            if i % 50 == 0 or i == len(roots): print(f'{i}/{len(roots)} roots, {len(out)} rows, {time.time() - t0_:.0f}s', flush=True)
    T = pd.DataFrame(out).sort_values(['ticker', 'R']).reset_index(drop=True)
    # implied history: previous event's iem only if that row IS the previous event; hist avg = expanding mean of prior rows
    g = T.groupby('ticker')
    T['last_iem'] = np.where(g.R.shift(1) == T.prev_R, g.iem.shift(1), np.nan)
    T['hist_avg_iem'] = g.iem.transform(lambda x: x.shift(1).expanding().mean())
    T['n_iem'] = g.iem.transform(lambda x: x.shift(1).expanding().count())
    T['implied_to_last_implied_ratio'] = T.iem / T.last_iem
    T['implied_hist_avg_implied_ratio'] = T.iem / T.hist_avg_iem
    T['gap_implied_last_realized'] = T.iem - T.last_real
    T['gap_implied_hist_avg_realized'] = T.iem - T.hist_avg_real
    mid = lambda b, a_: (b + a_) / 2
    for f in FS:
        tg = int(f * 100)
        ent = mid(T.ent_bid, T.ent_ask) + f * (T.ent_ask - T.ent_bid) / 2
        T[f'ret_{tg}'] = (mid(T.x_bid, T.x_ask) - f * (T.x_ask - T.x_bid) / 2) / ent - 1
    T.to_parquet(f'{a.out}/trades.parquet', index=False)
    print(f'wrote {len(T)} rows -> {a.out}/trades.parquet  {time.time() - t0_:.0f}s')
