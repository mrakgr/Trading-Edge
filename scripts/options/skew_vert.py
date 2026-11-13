#!/usr/bin/env python3
"""SKEW VERTICALS (Volatility Vibes / oquants 'skew' video, user 2026-11-12): buy the ~50-delta option, sell the ~25 (or
~15) delta wing of the SAME expiry when the wing is rich (steep skew, vs its own history) and the ATM option is cheap vs
realized vol. Call spreads into steep call skew, put spreads into steep put skew. Short tenor: 5-15 calendar days,
held to expiry (settles at intrinsic = exercise / assignment, no exit spread). This script builds the TRADES + FEATURES;
the gates are applied in skew_vert_report.py.

Per root, per day: every option's IV from its NBBO mid (Black-Scholes, r=q=0, T = trading days/252, S = the unadjusted
close), its delta; per (day, expiry): ATM IV = mean of the call and put IV at the strike nearest S; c25 / p25 / c15 /
p15 = IV interpolated in delta. VV's skew: call_skew = (atm - c25)/atm, put_skew = (atm - p25)/atm (more negative =
steeper). Daily constant-tenor series = the expiry with calendar DTE closest to 10 in [5, 20] ('10d'); z-score of today's
10d skew vs the previous 60 sessions (>= 20), and its percentile in the previous 252.

Trades: one per (root, expiry E) at the session whose calendar DTE is closest to 10 within [5, 15]:
  CALL spread: long the call with delta nearest .50 (.40-.60), short delta nearest .25 (.15-.35) ['c25'] or .15
  (.08-.22) ['c15'];  PUT spread: the mirror ['p25', 'p15'].  Payoff = intrinsic at E's close (spot of the last
  session <= E). Debit at fill f = mid_L - mid_S + f (hs_L + hs_S).  ret_f = payoff/debit - 1 (return on capital at risk).
  fair_rv20 = the same spread at sigma = rv20 (BS) -> edge_rv20 = fair/mid - 1 (VV's 'price both legs at RV').
Features (all known at the entry close): atm/iv_long/iv_short, call/put skew of E and the 10d z / pct, rr = c25 - p25;
  rv10/rv20/rv60 (close-to-close, total-return adjusted); momentum: ret_63 / ret_126, OLS slope of ln(adj close) and of
  ln(adj volume) on CALENDAR time (days since the window start; per year) over the last 63 / 126 sessions, with R^2;
  earn_in_life = a scheduled reaction day in (t0, E]; split_in_life.

    cd research && python3 scripts/options/skew_vert.py > logs/options_skew_vert.log 2>&1; echo "exit $?"
writes data/options/skew_vert/{trades,skew10}.parquet
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); from tr_index import add_tr
from multiprocessing import Pool
from scipy.special import ndtr

ap = argparse.ArgumentParser()
ap.add_argument('--eod', default='data/options/eod_universe')
ap.add_argument('--out', default='data/options/skew_vert')
ap.add_argument('--roots', default='')
ap.add_argument('--workers', type=int, default=6)
ap.add_argument('--full', action='store_true', help='market_full 2012-> with the PIT universe (survey_inputs.py)')
ap.add_argument('--earnings', default='data/options/earnings_sched.parquet')
a = ap.parse_args()
FS = [0, 0.25, 0.5, 1]

t0_ = time.time()
if a.full:
    from survey_inputs import load
    globals().update(load(a.eod, a.roots, da_cols=('close', 'n', 'cum_div', 'volume')))
    DA = {r: g.set_index('date')[['V', 'vol_adj']].rename(columns={'vol_adj': 'vol'}) for r, g in da.groupby('ticker')}
else:
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
    da = add_tr(c.sql(f"""select ticker, date, close, n, cum_div, volume from daily_adjusted
                   where ticker in ({tl}) and date >= '2021-06-01' order by ticker, date""").df()).rename(columns={'vol_adj': 'vol'})
    da['date'] = pd.to_datetime(da.date)
    DA = {t: g.set_index('date')[['V', 'vol']] for t, g in da.groupby('ticker')}
    sp = c.sql(f"select ticker, execution_date from splits where ticker in ({tl}) and execution_date >= '2022-06-01'").df()
    SPL = {t: np.array(pd.to_datetime(g.execution_date).values, dtype='datetime64[D]') for t, g in sp.groupby('ticker')}
    TDALL = np.array(sorted(c.sql("select distinct date from daily_adjusted where ticker = 'SPY' and date >= '2014-06-01'").df().date),
                     dtype='datetime64[D]')
    TDALL = np.union1d(TDALL, TD)   # daily_adjusted can lag the daily aggregates by days
e = E_RAW.copy() if a.full else pd.read_parquet(a.earnings); e = e[e.is_release]
ed = np.array(pd.to_datetime(e.et_date).values, dtype='datetime64[D]')
ii = np.where(e.session.values == 'AMC', np.searchsorted(TDALL, ed, 'right'), np.searchsorted(TDALL, ed, 'left'))
ok = ii < len(TDALL); e = e[ok].copy(); e['R'] = TDALL[ii[ok]]
EARN = {t: np.unique(g.R.values.astype('datetime64[D]')) for t, g in e.groupby('ticker')}
print(f'inputs {time.time() - t0_:.0f}s: {len(roots)} roots, TD {TD[0]}..{TD[-1]}', flush=True)



def RSQL(r):   # --full: the root's universe window (RAM)
    if not a.full: return ''
    return f"cast(date as date) between '{WIN[r][0]}' and '{WIN[r][1]}' and "

def bs_call(S, K, T, v):
    sT = v * np.sqrt(T); d1 = (np.log(S / K) + 0.5 * sT * sT) / sT
    return S * ndtr(d1) - K * ndtr(d1 - sT)


def implied(price, S, K, T, is_call):
    """vectorized bisection; puts via parity (r = q = 0)"""
    cprice = np.where(is_call, price, price + S - K)
    lo = np.full(len(price), 1e-3); hi = np.full(len(price), 8.0)
    for _ in range(45):
        m = (lo + hi) / 2; up = bs_call(S, K, T, m) > cprice
        hi = np.where(up, m, hi); lo = np.where(up, lo, m)
    iv = (lo + hi) / 2
    intr = np.where(is_call, np.maximum(S - K, 0), np.maximum(K - S, 0))
    return np.where((price > intr + 1e-6) & (iv < 7.9) & (iv > 2e-3), iv, np.nan)


def tdi(d):
    return np.searchsorted(TD, np.asarray(d, dtype='datetime64[D]'), 'right') - 1


def interp_delta(g, target):
    """IV at |delta| = target by linear interpolation; needs a bracket"""
    d = g.ad.values; v = g.iv.values; o = np.argsort(d); d, v = d[o], v[o]
    if len(d) < 2 or d[0] > target or d[-1] < target: return np.nan
    return float(np.interp(target, d, v))


def ols(x, y):
    x = x - x.mean(); yy = y - y.mean(); sxx = (x * x).sum()
    if sxx <= 0: return np.nan, np.nan
    b = (x * yy).sum() / sxx; r = yy - b * x; sst = (yy * yy).sum()
    return b, (1 - (r * r).sum() / sst) if sst > 0 else np.nan


def run(root):
    t = ROOT2T.get(root, root)
    if t not in SPOT or t not in DA: return [], [], []
    fs = sorted(glob.glob(f'{a.eod}/{root}__*.parquet'))
    q = duckdb.sql(f"""select distinct cast(date as date) date, cast(expiration as date) exp, strike K, "right" r, bid, ask
                       from read_parquet({fs}) where {RSQL(root)}ask > 0 and bid > 0 and ask >= bid
                       and expiration - date between 1 and 25""").df()
    if q.empty: return [], [], []
    q['date'] = pd.to_datetime(q.date); q['exp'] = pd.to_datetime(q.exp)
    s = SPOT[t]; q['S'] = q.date.map(s); q = q.dropna(subset=['S'])
    q = q[(q.K / q.S > 0.4) & (q.K / q.S < 2.0)].copy()
    q['ti'] = tdi(q.date.values); q['tdte'] = tdi(q.exp.values) - q.ti; q = q[q.tdte >= 1].copy()
    if q.empty: return [], [], []
    q['cdte'] = (q.exp - q.date).dt.days
    q['T'] = q.tdte / 252; q['mid'] = (q.bid + q.ask) / 2; q['hs'] = (q.ask - q.bid) / 2
    ic = (q.r == 'C').values
    q['iv'] = implied(q.mid.values, q.S.values, q.K.values, q['T'].values, ic)
    sT = q.iv.values * np.sqrt(q['T'].values); d1 = (np.log(q.S.values / q.K.values) + 0.5 * sT * sT) / sT
    q['delta'] = np.where(ic, ndtr(d1), ndtr(d1) - 1); q['ad'] = q.delta.abs()
    q = q[np.isfinite(q.iv)]
    rows_s = []; SK = {}
    for (d, ex), g in q.groupby(['date', 'exp'], sort=True):
        S = g.S.iat[0]; gc = g[g.r == 'C']; gp = g[g.r == 'P']
        if gc.empty or gp.empty: continue
        Ks = np.unique(g.K.values); ka = Ks[np.argmin(np.abs(Ks - S))]
        a_ = [x for x in (gc[gc.K == ka].iv.values, gp[gp.K == ka].iv.values) if len(x)]
        atm = float(np.mean(np.concatenate(a_))) if a_ else np.nan
        rec = dict(date=d, exp=ex, cdte=g.cdte.iat[0], tdte=g.tdte.iat[0], S=S, atm=atm,
                   c25=interp_delta(gc, .25), p25=interp_delta(gp, .25), c15=interp_delta(gc, .15), p15=interp_delta(gp, .15))
        SK[(d, ex)] = rec; rows_s.append(rec)
    if not rows_s: return [], [], []
    sk = pd.DataFrame(rows_s)
    sk['call_skew'] = (sk.atm - sk.c25) / sk.atm; sk['put_skew'] = (sk.atm - sk.p25) / sk.atm
    # the daily 10d series: the expiry with cdte closest to 10 in [5, 20]
    s10 = sk[(sk.cdte >= 5) & (sk.cdte <= 20)].copy(); s10['dd'] = (s10.cdte - 10).abs()
    s10 = s10.sort_values(['date', 'dd', 'cdte']).drop_duplicates('date').set_index('date').sort_index()
    for col in ['call_skew', 'put_skew']:
        x = s10[col]; past = x.shift(1)
        m = past.rolling(60, min_periods=20).mean(); sd = past.rolling(60, min_periods=20).std()
        s10[f'{col}_z'] = (x - m) / sd
        s10[f'{col}_pct'] = [np.nan if i < 20 else (x.iloc[max(0, i - 252):i] < x.iloc[i]).mean() for i in range(len(x))]
    s10['root'] = root; s10['ticker'] = t
    # daily adjusted series for RV / momentum
    D = DA[t]; di = D.index.values.astype('datetime64[D]'); V = D.V.values; VOL = D.vol.values
    lr = np.diff(np.log(V))
    R_e = EARN.get(t, np.array([], 'datetime64[D]')); splits = SPL.get(t, np.array([], 'datetime64[D]'))
    # entries: per expiry, the session with cdte closest to 10 in [5, 15]
    ent = sk[(sk.cdte >= 5) & (sk.cdte <= 15)].copy(); ent['dd'] = (ent.cdte - 10).abs()
    ent = ent.sort_values(['exp', 'dd', 'date']).drop_duplicates('exp')
    Q = {k: g for k, g in q.groupby(['date', 'exp'])}
    trades = []; wings = []
    for _, en in ent.iterrows():
        d, ex = en.date, en.exp; g = Q.get((d, ex))
        if g is None: continue
        d64 = np.datetime64(d.date(), 'D'); e64 = np.datetime64(ex.date(), 'D')
        jE = tdi(e64)
        if jE < 0 or TD[jE] < e64 - np.timedelta64(4, 'D') or e64 > TD[-1]: continue
        SE = s.get(pd.Timestamp(TD[jE]), np.nan)
        if not np.isfinite(SE): continue
        j = np.searchsorted(di, d64, 'right') - 1
        if j < 130 or di[j] != d64: continue
        f = dict(root=root, ticker=t, t0=d, E=ex, cdte=en.cdte, tdte=en.tdte, S0=en.S, SE=SE, atm=en.atm,
                 c25=en.c25, p25=en.p25, c15=en.c15, p15=en.p15, call_skew=en.call_skew, put_skew=en.put_skew,
                 earn_in_life=bool(((R_e > d64) & (R_e <= e64)).any()),
                 split_in_life=bool(((splits > d64) & (splits <= e64)).any()))
        if d in s10.index:
            for col in ['call_skew_z', 'put_skew_z', 'call_skew_pct', 'put_skew_pct']: f[col] = s10.at[d, col]
        for n in (10, 20, 60): f[f'rv{n}'] = np.sqrt(252 * np.mean(lr[j - n:j] ** 2))
        for n in (63, 126):
            f[f'ret_{n}'] = V[j] / V[j - n] - 1
            x = (di[j - n:j + 1] - di[j - n]).astype(float)
            f[f'pslope_{n}'], f[f'pr2_{n}'] = ols(x, np.log(V[j - n:j + 1]))
            f[f'vslope_{n}'], f[f'vr2_{n}'] = ols(x, np.log(VOL[j - n:j + 1] + 1))
            f[f'pslope_{n}'] *= 365; f[f'vslope_{n}'] *= 365
        T = en.tdte / 252
        for side in ('C', 'P'):
            gs = g[g.r == side]
            if gs.empty: continue
            L = gs.iloc[(gs.ad - .5).abs().argsort()[:1]]
            if L.empty or abs(L.ad.iat[0] - .5) > .10: continue
            for nm, tgt, tol in ((f'{side.lower()}25', .25, .10), (f'{side.lower()}15', .15, .07)):
                cand = gs[(gs.K > L.K.iat[0]) if side == 'C' else (gs.K < L.K.iat[0])]
                if cand.empty: continue
                Sh = cand.iloc[(cand.ad - tgt).abs().argsort()[:1]]
                if abs(Sh.ad.iat[0] - tgt) > tol: continue
                KL, KS_ = L.K.iat[0], Sh.K.iat[0]
                pay = (max(SE - KL, 0) - max(SE - KS_, 0)) if side == 'C' else (max(KL - SE, 0) - max(KS_ - SE, 0))
                mid = L.mid.iat[0] - Sh.mid.iat[0]
                if mid <= 0: continue
                r = dict(f, side=side, leg=nm, KL=KL, KS=KS_, dL=L.delta.iat[0], dS=Sh.delta.iat[0], iv_long=L.iv.iat[0],
                         iv_short=Sh.iv.iat[0], mid=mid, hs=L.hs.iat[0] + Sh.hs.iat[0], width=abs(KS_ - KL), payoff=pay)
                for rvn in ('rv20', 'rv10'):
                    sg = f[rvn]
                    if side == 'C': fair = bs_call(en.S, KL, T, sg) - bs_call(en.S, KS_, T, sg)
                    else: fair = (bs_call(en.S, KL, T, sg) + KL - en.S) - (bs_call(en.S, KS_, T, sg) + KS_ - en.S)
                    r[f'edge_{rvn}'] = fair / mid - 1
                trades.append(r)
            # SHORT WING (user 2026-11-12): sell the ~25 / ~15-delta wing, buy a ~5-delta cap farther out, delta-hedge in
            # stock: daily at the close (BS delta at each leg's ENTRY IV, remaining trading days) and static (entry only)
            for nm, tgt, tol in ((f'{side.lower()}25', .25, .10), (f'{side.lower()}15', .15, .07)):
                W = gs.iloc[(gs.ad - tgt).abs().argsort()[:1]]
                if W.empty or abs(W.ad.iat[0] - tgt) > tol: continue
                KW = W.K.iat[0]
                cand = gs[(gs.K > KW) if side == 'C' else (gs.K < KW)]
                cand = cand[(cand.ad >= .01) & (cand.ad <= .09)]
                if cand.empty: continue
                Cp = cand.iloc[(cand.ad - .05).abs().argsort()[:1]]; KC = Cp.K.iat[0]
                pw = max(SE - KW, 0) if side == 'C' else max(KW - SE, 0)
                pc = max(SE - KC, 0) if side == 'C' else max(KC - SE, 0)
                # spot path t0 .. E (unadjusted closes; split_in_life rows are dropped in the report)
                path = s[(s.index >= d) & (s.index <= pd.Timestamp(TD[jE]))]
                Sp = path.values; tl_ = (jE - tdi(path.index.values.astype('datetime64[D]'))) / 252
                def dlt(K, iv):
                    tt = np.maximum(tl_, 1e-6); sT = iv * np.sqrt(tt)
                    dd = ndtr((np.log(Sp / K) + 0.5 * sT * sT) / sT)
                    return dd if side == 'C' else dd - 1
                net = -dlt(KW, W.iv.iat[0]) + dlt(KC, Cp.iv.iat[0])          # option delta of short wing + long cap
                dSp = np.diff(Sp)
                wings.append(dict(f, side=side, leg=nm, KW=KW, KC=KC, dW=W.delta.iat[0], dC=Cp.delta.iat[0],
                                  iv_w=W.iv.iat[0], iv_c=Cp.iv.iat[0], iv_atm_long=L.iv.iat[0], mid_w=W.mid.iat[0],
                                  hs_w=W.hs.iat[0], mid_c=Cp.mid.iat[0], hs_c=Cp.hs.iat[0], pay_w=pw, pay_c=pc,
                                  width=abs(KC - KW), hedge_daily=float(np.sum(-net[:-1] * dSp)) if len(dSp) else 0.0,
                                  hedge_static=float(-net[0] * (SE - Sp[0])) if len(Sp) else 0.0, n_path=len(Sp)))
    return trades, s10.reset_index().to_dict('records'), wings


if __name__ == '__main__':
    os.makedirs(a.out, exist_ok=True)
    out, s10, wg = [], [], []
    with Pool(a.workers) as pool:
        for i, (r, s_, w_) in enumerate(pool.imap_unordered(run, roots), 1):
            out += r; s10 += s_; wg += w_
            if i % 25 == 0 or i == len(roots): print(f'{i}/{len(roots)} roots, {len(out)} trades, {time.time() - t0_:.0f}s', flush=True)
    T = pd.DataFrame(out)
    for f in FS:
        deb = T.mid + f * T.hs
        T[f'ret_{int(f * 100)}'] = T.payoff / deb - 1
    T.to_parquet(f'{a.out}/trades.parquet', index=False)
    pd.DataFrame(s10).to_parquet(f'{a.out}/skew10.parquet', index=False)
    pd.DataFrame(wg).to_parquet(f'{a.out}/wings.parquet', index=False)
    print(f'wrote {len(wg)} short-wing trades -> {a.out}/wings.parquet')
    print(f'wrote {len(T)} trades -> {a.out}/trades.parquet  {time.time() - t0_:.0f}s')
