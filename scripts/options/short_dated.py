#!/usr/bin/env python3
"""SHORT-DATED SELLING on liquid chains (user 2026-11-13: liquid names, IV rich vs RV, steep term slope / skew, active or
abnormally growing volume; short-dated and 0-DTE suit; PF on the OPTION premium, not notional).

Names: the universe stocks on their in-universe days + liquid ETFs (every day). Entry at the close of t, every expiry
1..10 TRADING days out (EOD data: 1-DTE entered at the close is the shortest; true 0-DTE needs intraday quotes).
Sold, held to expiry, settled at intrinsic on the expiry's close (a Saturday-dated monthly -> the Friday before):
  STR = ATM straddle (strike nearest the close) · AP / AC = ATM put / call · P25 / C25 = the strike whose |delta| is
  nearest .25 (delta from its own mid IV, BS r=q=0, T in trading days / 252).
Per trade: bid, ask (summed for STR), settle (intrinsic), so the reports price any f; dl = the option's delta at t (STR:
C + P) - the report's delta-hedged P&L adds dl x (Sx - S) to the short (a static hedge held to expiry).
Features at t (no lookahead): iv = ATM IV of this expiry (mean of C and P), iv30 = ATM IV of the expiry nearest 30
calendar days (20-45), rv10 / rv20 = close-to-close (total-return) realized vol, iv25p / iv25c (this expiry), vol /
vol5 / vol20 = the root's total option volume today / mean of the last 5 / the 20 days BEFORE t (root_volume_full),
earn = a release's reaction day (AMC -> next trading day) in (t, expiry]; split in (t, expiry] -> dropped.
    cd research && python3 scripts/options/short_dated.py > logs/options_short_dated.log 2>&1
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
ap.add_argument('--out', default='data/options/short_dated')
ap.add_argument('--roots', default='')
ap.add_argument('--etfs', default=ETFS)
ap.add_argument('--workers', type=int, default=4)
ap.add_argument('--max-tdte', type=int, default=10)
a = ap.parse_args()
t0_ = time.time()
I = load(a.eod, a.roots)
TD = I['TD']
SPOT, SPL = dict(I['SPOT']), dict(I['SPL'])
RET = {r: g.set_index('date').r for r, g in I['da'].groupby('ticker')}
UI = duckdb.sql("select root, cast(date as date) d from 'data/options/universe_daily_full.parquet' where in_universe").df()
UI['d'] = pd.to_datetime(UI.d)
UDAYS = {r: pd.DatetimeIndex(g.d.values) for r, g in UI.groupby('root')}
KIND = {r: 'stock' for r in I['roots']}
# ETFs: spot + returns straight from daily_adjusted, every day from 2012-06
etfs = [x for x in a.etfs.split(',') if x and glob.glob(f'{a.eod}/{x}__*.parquet')]
if etfs:
    tl = ','.join(f"'{x}'" for x in etfs)
    da = I['c'].sql(f"select ticker, date, close, n, cum_div from daily_adjusted where ticker in ({tl}) and date >= '2011-06-01' order by ticker, date").df()
    da['date'] = pd.to_datetime(da.date); da = add_tr(da)
    sp = I['c'].sql(f"select ticker, execution_date d from splits where ticker in ({tl})").df(); sp['d'] = pd.to_datetime(sp.d)
    for r, g in da.groupby('ticker'):
        SPOT[r] = g.set_index('date').close; RET[r] = g.set_index('date').r
        SPL[r] = np.array(sp[sp.ticker == r].d.values, dtype='datetime64[D]'); KIND[r] = 'etf'
        UDAYS[r] = pd.DatetimeIndex(g.date[g.date >= '2012-06-01'].values)
roots = sorted(KIND)
E = I['E_RAW']
E = E[E.is_release | E['items'].fillna('').str.contains('2.02')].copy()
ei = np.searchsorted(TD, E.et_date.values.astype('datetime64[D]'), 'left')
E['R'] = TD[np.minimum(ei + (E.session == 'AMC').astype(int).values, len(TD) - 1)]
EARN = {r: np.unique(g.R.values.astype('datetime64[D]')) for r, g in E.groupby('ticker')}
RV = duckdb.sql(f"""select root, cast(date as date) date, volume from 'data/options/root_volume_full.parquet'
                    where root in ({','.join(repr(r) for r in roots)})""").df()
RV['date'] = pd.to_datetime(RV.date)
VOLS = {r: g.set_index('date').volume for r, g in RV.groupby('root')}
del RV
print(f'inputs {time.time() - t0_:.0f}s: {len(roots)} roots ({len(etfs)} ETFs)', flush=True)


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


def features(root):
    """per-date: rv10, rv20, vol, vol5, vol20 (prior)"""
    r = RET[root].astype(float); lr = np.log1p(r)
    f = pd.DataFrame({'rv10': lr.rolling(10).std() * np.sqrt(252), 'rv20': lr.rolling(20).std() * np.sqrt(252)})
    v = VOLS.get(root)
    if v is not None:
        v = v.reindex(f.index)
        f['vol'] = v; f['vol5'] = v.shift(1).rolling(5, min_periods=4).mean(); f['vol20'] = v.shift(1).rolling(20, min_periods=15).mean()
    return f


def run(root):
    days = UDAYS.get(root); S = SPOT.get(root)
    if days is None or S is None or len(days) == 0: return None
    F = features(root)
    spl = SPL.get(root, np.array([], 'datetime64[D]')); earn = EARN.get(root, np.array([], 'datetime64[D]'))
    files = sorted(glob.glob(f'{a.eod}/{root}__*.parquet'))
    out = []
    for y in sorted({int(os.path.basename(f).split('__')[1][:4]) for f in files}):
        if not (days.year == y).any(): continue
        fy = [f for f in files if os.path.basename(f).split('__')[1][:4] == str(y)]
        q = duckdb.sql(f"""select cast(date as date) date, cast(expiration as date) exp, strike K, "right" r, bid, ask
                           from read_parquet({fy}) where bid > 0 and ask >= bid
                           and cast(expiration as date) - cast(date as date) between 1 and 50""").df()
        if q.empty: continue
        q['date'] = pd.to_datetime(q.date); q = q[q.date.isin(days)]
        q['S'] = q.date.map(S); q = q.dropna(subset=['S'])
        if q.empty: continue
        q['exp'] = pd.to_datetime(q.exp)
        ti = np.searchsorted(TD, q.date.values.astype('datetime64[D]'), 'right') - 1
        te = np.searchsorted(TD, q.exp.values.astype('datetime64[D]'), 'right') - 1     # the last trading day <= exp
        q['tdte'] = te - ti; q = q[q.tdte >= 1]
        q['xd'] = TD[np.searchsorted(TD, q.exp.values.astype('datetime64[D]'), 'right') - 1]
        q['cdte'] = (q.exp - q.date).dt.days
        T = q.tdte.values / 252
        q['mid'] = (q.bid + q.ask) / 2
        call = (q.r == 'C').values
        q['iv'] = implied(q.mid.values, q.S.values, q.K.values, T, call)
        sT = q.iv.values * np.sqrt(T); d1 = (np.log(q.S.values / q.K.values) + 0.5 * sT * sT) / sT
        q['dl'] = np.where(call, ndtr(d1), ndtr(d1) - 1)
        # ATM per (date, exp): the strike nearest S with both sides quoted
        q['dist'] = (q.K - q.S).abs()
        both = q.groupby(['date', 'exp', 'K']).r.transform('size') == 2
        b = q[both]
        atmK = b.loc[b.groupby(['date', 'exp']).dist.idxmin(), ['date', 'exp', 'K']].rename(columns={'K': 'Katm'})
        q = q.merge(atmK, on=['date', 'exp'])
        A = q[q.K == q.Katm]
        ivatm = A.groupby(['date', 'exp']).iv.mean().rename('iv_atm').reset_index()
        ivatm['cdte'] = (ivatm.exp - ivatm.date).dt.days
        # iv30: the expiry nearest 30 calendar days within 20-45
        c30 = ivatm[(ivatm.cdte >= 20) & (ivatm.cdte <= 45)].dropna(subset=['iv_atm']).copy()
        c30['dd'] = (c30.cdte - 30).abs()
        iv30 = c30.loc[c30.groupby('date').dd.idxmin(), ['date', 'iv_atm', 'cdte']].rename(columns={'iv_atm': 'iv30', 'cdte': 'cdte30'})
        fr = q[q.tdte <= a.max_tdte]
        if fr.empty: continue
        rows = []
        # straddle + ATM legs
        Af = fr[fr.K == fr.Katm]
        for inst, sel in (('AP', Af.r == 'P'), ('AC', Af.r == 'C')):
            x = Af[sel][['date', 'exp', 'xd', 'tdte', 'cdte', 'K', 'r', 'S', 'bid', 'ask', 'iv', 'dl']].assign(inst=inst)
            rows.append(x)
        st = Af.groupby(['date', 'exp']).agg(xd=('xd', 'first'), tdte=('tdte', 'first'), cdte=('cdte', 'first'), K=('K', 'first'),
                                              S=('S', 'first'), bid=('bid', 'sum'), ask=('ask', 'sum'), iv=('iv', 'mean'), dl=('dl', 'sum'), n=('r', 'size')).reset_index()
        st = st[st.n == 2].drop(columns='n').assign(r='S', inst='STR')
        rows.append(st)
        # 25-delta legs
        for inst, rr, tgt in (('P25', 'P', -.25), ('C25', 'C', .25)):
            w = fr[(fr.r == rr) & fr.dl.notna()].copy()
            w['dd'] = (w.dl - tgt).abs()
            w = w.loc[w.groupby(['date', 'exp']).dd.idxmin()]
            w = w[w.dd < .10]
            rows.append(w[['date', 'exp', 'xd', 'tdte', 'cdte', 'K', 'r', 'S', 'bid', 'ask', 'iv', 'dl']].assign(inst=inst))
            sk = w[['date', 'exp', 'iv']].rename(columns={'iv': f'iv25{rr.lower()}'})
            if inst == 'P25': skp = sk
            else: skc = sk
        T_ = pd.concat(rows, ignore_index=True)
        T_ = T_.merge(ivatm[['date', 'exp', 'iv_atm']], on=['date', 'exp'], how='left').merge(iv30, on='date', how='left')
        T_ = T_.merge(skp, on=['date', 'exp'], how='left').merge(skc, on=['date', 'exp'], how='left')
        # settle at the expiry's last trading day close
        T_['Sx'] = pd.to_datetime(T_.xd).map(S)
        T_ = T_.dropna(subset=['Sx'])
        intr = lambda K, Sx, r: np.where(r == 'C', np.maximum(Sx - K, 0), np.where(r == 'P', np.maximum(K - Sx, 0), np.abs(Sx - K)))
        T_['settle'] = intr(T_.K.values, T_.Sx.values, T_.r.values)
        d0 = T_.date.values.astype('datetime64[D]'); d1_ = pd.to_datetime(T_.xd).values.astype('datetime64[D]')
        T_['split'] = (np.searchsorted(spl, d1_, 'right') - np.searchsorted(spl, d0, 'right')) > 0
        T_['earn'] = (np.searchsorted(earn, d1_, 'right') - np.searchsorted(earn, d0, 'right')) > 0
        T_ = T_[~T_.split].drop(columns='split')
        T_ = T_.merge(F, left_on='date', right_index=True, how='left')
        T_['root'] = root; T_['kind'] = KIND[root]
        out.append(T_)
        del q, fr
    if not out: return None
    return pd.concat(out, ignore_index=True)


if __name__ == '__main__':
    os.makedirs(a.out, exist_ok=True)
    R = []
    with Pool(a.workers, maxtasksperchild=20) as pool:
        for i, r in enumerate(pool.imap_unordered(run, roots), 1):
            if r is not None: R.append(r)
            if i % 50 == 0 or i == len(roots): print(f'{i}/{len(roots)} roots  {time.time() - t0_:.0f}s', flush=True)
    R = pd.concat(R, ignore_index=True)
    R.to_parquet(f'{a.out}/trades.parquet', index=False)
    print(f'wrote {len(R):,} trades  {time.time() - t0_:.0f}s')
