#!/usr/bin/env python3
"""0DTE: SELL EVERY MINUTE, analyse the features post hoc (user 2026-11-16; like the MR system).
Per (root, 0DTE day) from data/options/intraday_0dte/<ROOT>__<YYYYMMDD>.{quote,ohlc}.parquet (intraday_pull.py):
  minutes 09:35 .. 15:55 ET (the quote stamped hh:mm = the NBBO at that minute); instruments of the 0DTE chain:
  P25 / C25 = the strike whose |delta| is nearest .25 (within .10), AP / AC = the strike nearest the underlying (both sides
  quoted). T = minutes to 16:00 / (390 x 252) (trading time), BS r=q=0, IV from the NBBO mid.
  underlying: ETFs / stocks = the last 1-second VWAP at or before the minute (data/intraday_1s_slim); SPXW = put-call parity
  on the 0DTE chain (median of K + C - P over the 6 strikes nearest the previous estimate).
  settle = intrinsic at the close (ETFs / stocks: the official close, trading.db daily_adjusted; SPXW: parity at 16:00).
  hedge: rebalanced EVERY MINUTE to the contract's own delta (no IV -> -1/+1 in the money, else 0; a missing quote keeps
  the last delta); hd = sum delta_j x (S_j+1 - S_j) to the close; turn = shares traded per share of option.
Features at the entry minute: iv (the contract), iv_atm (0DTE), iv_atm_n (next expiry), fwd (forward vol 0DTE close ->
next expiry: (ivn^2 Tn - iv0^2 T0) / (Tn - T0), Tn = rest of today + 390 per trading day between, / (390 x 252)),
skew_p = iv(P25)/iv_atm, skew_c = iv(C25)/iv_atm, rv_open (1-min RV since 09:30, annualised), rv30 (last 30 min),
ret_open, from_hi / from_lo (vs the day's high / low so far), gap (open vs the previous close), mins_left,
vol_chain / vol_put_share (0DTE option volume so far, ohlc), vol_k (this contract's volume so far).
EXIT BEFORE THE BELL (2026-11-16): bid_x / ask_x / m_x = the contract's last quote in 15:50..15:59, hd_x = the hedge to
15:59 (buy the short back at ask_x; ETF / stock 0DTE options carry after-hours exercise risk to 17:30 if held).
AFTER-HOURS HEDGE (2026-11-16): hd_ah / turn_ah = a minute delta hedge 16:00 -> 17:30 (European to 17:30, per-minute
vol s_ah from the previous day's after-hours) for ETFs / stocks; P&L then settles at s1730.
Writes data/options/zdte/trades/<ROOT>.parquet (one row per root-day-minute-instrument).
    cd research && python3 scripts/options/zdte_trades.py [--roots SPY] [--days 5] > logs/options_zdte_trades.log 2>&1
"""
import argparse, glob, os, sys, time
import duckdb, numpy as np, pandas as pd
from multiprocessing import Pool
from scipy.special import ndtr

ap = argparse.ArgumentParser()
ap.add_argument('--src', default='data/options/intraday_0dte')
ap.add_argument('--out', default='data/options/zdte/trades')
ap.add_argument('--roots', default='')
ap.add_argument('--days', type=int, default=0, help='only the newest N days per root (testing)')
ap.add_argument('--workers', type=int, default=6)
a = ap.parse_args()
TD = None
YEARMIN = 390 * 252


def implied(price, S, K, T, call):
    lo = np.full(len(price), 1e-4); hi = np.full(len(price), 20.0); sT = np.sqrt(T); lk = np.log(S / K)
    for _ in range(45):
        m = (lo + hi) / 2; v = m * sT
        d1 = (lk + 0.5 * v * v) / v; c = S * ndtr(d1) - K * ndtr(d1 - v)
        p = np.where(call, c, c - S + K)
        up = p > price; hi = np.where(up, m, hi); lo = np.where(up, lo, m)
    iv = (lo + hi) / 2
    intr = np.where(call, np.maximum(S - K, 0), np.maximum(K - S, 0))
    return np.where((price > intr + 1e-6) & (price < np.where(call, S, K)) & (iv < 19.9) & (iv > 1e-3), iv, np.nan)


def delta(iv, S, K, T, call):
    sT = iv * np.sqrt(T); d1 = (np.log(S / K) + 0.5 * sT * sT) / sT
    return np.where(call, ndtr(d1), ndtr(d1) - 1)


DAYS_1S = sorted(os.path.basename(f)[:10] for f in glob.glob('data/intraday_1s_slim/*.parquet'))


def spot_1s(ticker, date):
    """1-second VWAPs 09:30-17:30, CLEANED (2026-11-16): a second more than 0.3% from its minute's median is dropped
    (after-hours has stray prints: QQQ 2026-11-06 at 601 / 615 / 667 vs 656)."""
    f = f'data/intraday_1s_slim/{date}.parquet'
    if not os.path.exists(f): return None
    x = duckdb.sql(f"select bucket, vwap from '{f}' where ticker = '{ticker}' and bucket between 34200 and 63000 order by bucket").df()
    if not len(x): return None
    med = x.groupby(x.bucket // 60).vwap.transform('median')
    x = x[(x.vwap / med - 1).abs() < 0.003]
    return x if len(x) else None


def prev_day(date):
    i = np.searchsorted(DAYS_1S, date)
    return DAYS_1S[i - 1] if i > 0 else None


def parity_spot(q0):
    """SPXW: per minute, median(K + C - P) over the 6 strikes nearest the running estimate."""
    w = q0.pivot_table(index=['m', 'strike'], columns='right', values='mid').dropna().reset_index()
    w['F'] = w.strike + w.C - w.P
    out, prev = {}, None
    for m, g in w.groupby('m', sort=True):
        ref = prev if prev is not None else g.loc[(g.C - g.P).abs().idxmin(), 'strike']
        g = g.iloc[(g.strike - ref).abs().argsort()[:6]]
        prev = float(np.median(g.F)); out[m] = prev
    return pd.Series(out)


def run(job):
    root, d = job
    date = f'{d[:4]}-{d[4:6]}-{d[6:]}'
    qf, of = f'{a.src}/{root}__{d}.quote.parquet', f'{a.src}/{root}__{d}.ohlc.parquet'
    q = pd.read_parquet(qf)
    q['ts'] = pd.to_datetime(q.timestamp)
    q['m'] = q.ts.dt.hour * 60 + q.ts.dt.minute                       # minute of day
    q = q[(q.m >= 570) & (q.m <= 960)]
    q['mid'] = (q.bid + q.ask) / 2
    # the exit quotes from the RAW rows (any ask): a far-OTM option often has NO bid at 15:59 - taking the exit only from
    # two-sided rows dropped exactly those trades (a lookahead selection, 2026-11-16 fix)
    qx = q[(q.expiration == date) & (q.m >= 950) & (q.m <= 959) & (q.ask > 0)]
    q = q[(q.bid > 0) & (q.ask >= q.bid)]
    q0 = q[q.expiration == date].copy(); qn = q[q.expiration != date].copy()
    if q0.empty: return None
    # underlying per minute (570 = 09:30 .. 960 = 16:00)
    mins = np.arange(570, 961)
    if root == 'SPXW':
        S = parity_spot(q0).reindex(mins).ffill()
        close = S.loc[960]
        s1730 = close                                                  # SPXW: cash-settled on the official 16:00 close
        S_ah = None
    else:
        sp = spot_1s(root, date)
        if sp is None: return None
        ah = sp[(sp.bucket > 63000 - 300) & (sp.bucket <= 63000)]          # 17:25-17:30: the median, robust
        # after-hours path per minute 16:00 .. 17:30 (for the AH delta hedge) and the PREVIOUS day's AH per-minute vol
        ahm = sp[(sp.bucket >= 57600) & (sp.bucket <= 63000)].assign(mm=lambda z: z.bucket // 60).groupby('mm').vwap.last()
        S_ah = ahm.reindex(np.arange(960, 1051)).ffill()
        s_ah = np.nan
        pdd = prev_day(date)
        if pdd is not None:
            pv = spot_1s(root, str(pdd))
            if pv is not None:
                pa = pv[(pv.bucket >= 57600) & (pv.bucket <= 63000)].assign(mm=lambda z: z.bucket // 60).groupby('mm').vwap.last()
                pa = pa.reindex(np.arange(960, 1051)).ffill()
                rr = np.log(pa).diff().dropna()
                if len(rr) > 30: s_ah = float(np.sqrt((rr ** 2).mean()))
        s1730 = float(ah.vwap.median()) if len(ah) else np.nan     # the exercise cut-off: holders decide on after-hours prices
        sp = sp[sp.bucket <= 57600].copy()
        sp['m'] = sp.bucket // 60
        S = sp.groupby('m').vwap.last().reindex(mins).ffill()        # last 1s VWAP up to the minute's end
        S.index = S.index + 1                                         # value at the START of minute m = up to m-1:59
        S = S.reindex(mins).ffill().bfill()
        c = duckdb.connect('data/trading.db', read_only=True)
        r = c.sql(f"select close from daily_adjusted where ticker = '{root}' and date = '{date}'").fetchall()
        pc = c.sql(f"select close from daily_adjusted where ticker='{root}' and date < '{date}' order by date desc limit 1").fetchall()
        c.close()
        prev_close = pc[0][0] if pc else np.nan
        if r: close = r[0][0]                                       # the official close
        else:                                                         # trading.db ends 2026-10-30: the closing auction second
            auc = sp[sp.bucket == 57600].vwap
            close = float(auc.iloc[0]) if len(auc) else float(sp[sp.bucket < 57600].vwap.iloc[-1])
        S.loc[960] = close
    if S.isna().all(): return None
    if root == 'SPXW':
        prev_close = np.nan
    left = (960 - q0.m).clip(lower=0.5)
    T0 = left / YEARMIN
    q0['S'] = q0.m.map(S); q0 = q0.dropna(subset=['S'])
    call0 = (q0.right == 'C').values
    q0['iv'] = implied(q0.mid.values, q0.S.values, q0.strike.values, (960 - q0.m).clip(lower=0.5).values / YEARMIN, call0)
    q0['dl'] = delta(q0.iv.values, q0.S.values, q0.strike.values, (960 - q0.m).clip(lower=0.5).values / YEARMIN, call0)
    itm = np.where(call0, q0.S > q0.strike, q0.S < q0.strike)
    q0['dlh'] = np.where(np.isfinite(q0.dl), q0.dl, np.where(itm, np.where(call0, 1.0, -1.0), 0.0))
    # hedge P&L per contract, rebalanced every minute: cum of dlh_j x (S_j+1 - S_j) from j to 959
    hp = q0.pivot_table(index='m', columns=['strike', 'right'], values='dlh').reindex(mins[:-1]).ffill()
    dS = (S.reindex(mins).shift(-1) - S.reindex(mins)).iloc[:-1].values
    inc = hp.values * dS[:, None]
    inc = np.nan_to_num(inc)
    tail = np.flip(np.cumsum(np.flip(inc, 0), 0), 0)                # sum from row j to the end
    ddl = np.abs(np.diff(np.nan_to_num(hp.values), axis=0, prepend=0)); ddl[0] = 0
    turn_tail = np.flip(np.cumsum(np.flip(ddl, 0), 0), 0)
    HD = pd.DataFrame(tail, index=hp.index, columns=hp.columns).stack(['strike', 'right'], future_stack=True).rename('hd')
    TN = pd.DataFrame(turn_tail, index=hp.index, columns=hp.columns).stack(['strike', 'right'], future_stack=True).rename('tturn')
    LASTD = hp.iloc[-1].rename('dlast')
    LASTINC = pd.Series(inc[-1], index=hp.columns).rename('inc_last')     # the 15:59 -> 16:00 hedge increment
    # exit before the bell (2026-11-16): the contract's last quote in 15:50..15:59 (normally 15:59)
    QX = qx.sort_values('m').groupby(['strike', 'right']).last()[['bid', 'ask', 'm']]
    QX.columns = ['bid_x', 'ask_x', 'm_x']
    # ATM per minute and the instruments
    q0['dist'] = (q0.strike - q0.S).abs()
    both = q0.groupby(['m', 'strike']).right.transform('size') == 2
    atmK = q0[both].loc[q0[both].groupby('m').dist.idxmin(), ['m', 'strike']].rename(columns={'strike': 'Katm'})
    q0 = q0.merge(atmK, on='m')
    A = q0[q0.strike == q0.Katm]
    ivatm = A.groupby('m').iv.mean().rename('iv_atm')
    sel = []
    for inst, rr in (('AP', 'P'), ('AC', 'C')):
        sel.append(A[A.right == rr].assign(inst=inst))
    for inst, rr, tgt in (('P25', 'P', -.25), ('C25', 'C', .25)):
        w = q0[(q0.right == rr) & q0.dl.notna()].copy(); w['dd'] = (w.dl - tgt).abs()
        w = w.loc[w.groupby('m').dd.idxmin()]; w = w[w.dd < .10]
        sel.append(w.assign(inst=inst))
    X = pd.concat(sel, ignore_index=True)
    X = X[(X.m >= 575) & (X.m <= 955)]
    X = X.join(HD, on=['m', 'strike', 'right']).join(TN, on=['m', 'strike', 'right']).join(LASTD, on=['strike', 'right'])
    X = X.join(LASTINC, on=['strike', 'right']).join(QX, on=['strike', 'right'])
    X['hd_x'] = X.hd - X.inc_last.fillna(0)                           # the hedge closed at 15:59 with the option
    X['ask_x'] = X.ask_x.fillna(0.0)                                  # no quote at all in 15:50..15:59 = nothing to buy back
    X['turn'] = X.dlh.abs() + X.tturn + X.dlast.abs()
    X['settle'] = np.where(X.right == 'C', np.maximum(close - X.strike, 0), np.maximum(X.strike - close, 0))
    X['close'] = close
    X['s1730'] = s1730 if np.isfinite(s1730) else close
    # ---- AFTER-HOURS DELTA HEDGE (user 2026-11-16): 16:00 -> 17:30, the option = European expiring at 17:30 (the holder
    # exercises if in the money then), per-minute vol s_ah from the PREVIOUS day's 16:00-17:30 (fallback: half of today's
    # last-hour per-minute vol); the hedge starts from the 15:59 position (dlast). hd_ah = sum delta_j (S_j+1 - S_j),
    # turn_ah = |delta_960 - dlast| + sum |d delta| + |delta_end| (shares per share of option, charged at AH cost).
    X['hd_ah'] = 0.0; X['turn_ah'] = 0.0; X['s_ah'] = np.nan
    if root != 'SPXW' and S_ah is not None and S_ah.notna().sum() > 30:
        if not np.isfinite(s_ah):
            lr = np.log(S.reindex(np.arange(900, 961))).diff().dropna()
            s_ah = 0.5 * float(np.sqrt((lr ** 2).mean())) if len(lr) else np.nan
        if np.isfinite(s_ah) and s_ah > 0:
            Sa = S_ah.ffill().bfill().values                             # minutes 960..1050
            n_left = (1050 - np.arange(960, 1051)).clip(min=1e-6)
            u = X[['strike', 'right', 'dlast']].drop_duplicates(['strike', 'right'])
            K = u.strike.values[None, :]; cl = (u.right.values == 'C')[None, :]
            sd = s_ah * np.sqrt(n_left)[:, None]
            d1 = np.log(Sa[:, None] / K) / sd + 0.5 * sd
            dl_ah = np.where(cl, ndtr(d1), ndtr(d1) - 1)
            dl_ah[-1] = np.where(cl, (Sa[-1] > K), -(Sa[-1] < K))[0] if False else np.where(cl[0], (Sa[-1] > K[0]).astype(float), -(Sa[-1] < K[0]).astype(float))
            dS = np.diff(Sa)[:, None]
            hd_ah = (dl_ah[:-1] * dS).sum(0)
            tr = np.abs(dl_ah[0] - u.dlast.fillna(0).values) + np.abs(np.diff(dl_ah, axis=0)).sum(0) + np.abs(dl_ah[-1])
            m_ = pd.DataFrame({'strike': u.strike.values, 'right': u.right.values, 'hd_ah': hd_ah, 'turn_ah': tr})
            X = X.drop(columns=['hd_ah', 'turn_ah']).merge(m_, on=['strike', 'right'], how='left')
            X['s_ah'] = s_ah
    # skew + forward
    sk = X.pivot_table(index='m', columns='inst', values='iv')
    X['iv_atm'] = X.m.map(ivatm)
    X['skew_p'] = X.m.map(sk.get('P25') / ivatm) if 'P25' in sk else np.nan
    X['skew_c'] = X.m.map(sk.get('C25') / ivatm) if 'C25' in sk else np.nan
    if not qn.empty:
        nexp = qn.expiration.iloc[0]
        tdn = int(np.busday_count(np.datetime64(date), np.datetime64(nexp)))   # trading days between (approx: weekdays)
        qn['S'] = qn.m.map(S); qn = qn.dropna(subset=['S'])
        Tn = ((960 - qn.m).clip(lower=0.5) + 390 * tdn) / YEARMIN
        qn['iv'] = implied(qn.mid.values, qn.S.values, qn.strike.values, Tn.values, (qn.right == 'C').values)
        qn['dist'] = (qn.strike - qn.S).abs()
        nb = qn[qn.iv.notna()]
        ka = nb.loc[nb.groupby('m').dist.idxmin(), ['m', 'strike']]
        ivn = nb.merge(ka, on=['m', 'strike']).groupby('m').iv.mean()
        X['iv_atm_n'] = X.m.map(ivn)
        T0x = (960 - X.m).clip(lower=0.5) / YEARMIN; Tnx = ((960 - X.m).clip(lower=0.5) + 390 * tdn) / YEARMIN
        fv = (X.iv_atm_n ** 2 * Tnx - X.iv_atm ** 2 * T0x) / (Tnx - T0x)
        X['fwd'] = np.sqrt(fv.where(fv > 0))
    else:
        X['iv_atm_n'] = np.nan; X['fwd'] = np.nan
    # underlying state
    r1 = np.log(S.reindex(mins)).diff()
    cs = (r1 ** 2).fillna(0).cumsum()
    X['rv_open'] = X.m.map(pd.Series(np.sqrt(cs.values * YEARMIN / np.maximum(cs.index.values - 570, 1)), index=cs.index))
    X['rv30'] = X.m.map(np.sqrt((cs - cs.shift(30)) * YEARMIN / 30))
    S_open = S.loc[570]
    hi, lo = S.reindex(mins).cummax(), S.reindex(mins).cummin()
    X['ret_open'] = X.m.map(S / S_open - 1)
    X['from_hi'] = X.m.map(S / hi - 1); X['from_lo'] = X.m.map(S / lo - 1)
    X['gap'] = S_open / prev_close - 1 if prev_close and np.isfinite(prev_close) else np.nan
    X['mins_left'] = 960 - X.m
    # option volume so far (0DTE chain), from the 1-minute trade bars
    if os.path.exists(of):
        o = pd.read_parquet(of, columns=['expiration', 'strike', 'right', 'timestamp', 'volume'])
        o = o[o.expiration == date]
        ts = pd.to_datetime(o.timestamp); o['m'] = ts.dt.hour * 60 + ts.dt.minute
        v = o.groupby('m').volume.sum().reindex(mins, fill_value=0).cumsum()
        vp = o[o.right == 'P'].groupby('m').volume.sum().reindex(mins, fill_value=0).cumsum()
        X['vol_chain'] = X.m.map(v.shift(1)); X['vol_put_share'] = X.m.map((vp / v.replace(0, np.nan)).shift(1))
        vk = o.groupby(['strike', 'right', 'm']).volume.sum().groupby(level=[0, 1]).cumsum()
        vk = vk.groupby(level=[0, 1]).shift(1).rename('vol_k').reset_index()
        X = pd.merge_asof(X.sort_values('m'), vk.sort_values('m'), on='m', by=['strike', 'right'], direction='backward')
    X['root'] = root; X['date'] = pd.Timestamp(date)
    keep = ['root', 'date', 'm', 'inst', 'strike', 'right', 'S', 'bid', 'ask', 'bid_size', 'ask_size', 'iv', 'dl', 'hd', 'turn',
            'settle', 'close', 's1730', 'dlast', 'bid_x', 'ask_x', 'm_x', 'hd_x', 'hd_ah', 'turn_ah', 's_ah', 'iv_atm', 'iv_atm_n', 'fwd', 'skew_p', 'skew_c', 'rv_open', 'rv30', 'ret_open', 'from_hi', 'from_lo',
            'gap', 'mins_left', 'vol_chain', 'vol_put_share', 'vol_k']
    return X[[k for k in keep if k in X.columns]]


if __name__ == '__main__':
    t0 = time.time()
    files = sorted(glob.glob(f'{a.src}/*.quote.parquet'))
    jobs = [(os.path.basename(f).split('__')[0], os.path.basename(f).split('__')[1][:8]) for f in files]
    if a.roots: jobs = [j for j in jobs if j[0] in a.roots.split(',')]
    if a.days:
        by = {}
        for r, d in sorted(jobs, key=lambda j: j[1], reverse=True): by.setdefault(r, []).append((r, d))
        jobs = [j for v in by.values() for j in v[:a.days]]
    os.makedirs(a.out, exist_ok=True)
    print(f'{len(jobs)} root-days', flush=True)
    res = {}
    with Pool(a.workers, maxtasksperchild=50) as pool:
        for i, X in enumerate(pool.imap_unordered(run, jobs, chunksize=4), 1):
            if X is not None and len(X): res.setdefault(X.root.iloc[0], []).append(X)
            if i % 200 == 0 or i == len(jobs): print(f'{i}/{len(jobs)}  {time.time() - t0:.0f}s', flush=True)
    for r, L in res.items():
        pd.concat(L, ignore_index=True).to_parquet(f'{a.out}/{r}.parquet', index=False)
    print(f'wrote {sum(len(x) for L in res.values() for x in L):,} rows for {len(res)} roots  {time.time() - t0:.0f}s')
