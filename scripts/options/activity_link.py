#!/usr/bin/env python3
"""Does option ACTIVITY at entry change the three systems' returns? (user 2026-11-13: 'how options activity should
affect our strategies'). Activity = activity.py's root_day (in-universe days, volume > 0 rows). Features at the entry
close t0 (EOD volume is the full session: known at t0's close, the entry):
  liq20     mean daily contracts over the 20 previous sessions (log10) - how liquid the name's options are
  abn       contracts on t0 / liq20;   abn5 = mean of t0-4..t0 / mean of t0-24..t0-5
  atm_sh    ATM (|d| .40-.60, DTE <= 45) share of t0's contracts;  pcr = puts / calls on t0;  size = contracts / trade
  wing_abn  (C only) the spread's own wing (put wing for put spreads) contracts on t0 / its 20-session mean
  dskew5    (C only) the own 10-day skew today minus 5 sessions ago (negative = steepening; skew10.parquet)
Returns as in survey_report.py (% of the premium; f = .25). Deciles of each feature (equal counts); spearman.
    cd research && python3 scripts/options/activity_link.py > logs/options_activity_link.log
"""
import numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 250)
D = 'data/options/full'
U = duckdb.sql("select root, cast(date as date) d, cik from 'data/options/universe_daily_full.parquet' where in_universe").df()
U['d'] = pd.to_datetime(U.d)
R = pd.read_parquet('data/options/activity/root_day.parquet').sort_values(['root', 'date']).reset_index(drop=True)
g = R.groupby('root')
prev = lambda c, n: g[c].transform(lambda s: s.shift(1).rolling(n, min_periods=max(5, n // 2)).mean())
R['liq20'] = np.log10(prev('vol', 20))
R['abn'] = R.vol / 10 ** R.liq20
R['abn5'] = g.vol.transform(lambda s: s.rolling(5, min_periods=3).mean() / s.shift(5).rolling(20, min_periods=10).mean())
R['atm_sh'] = R.vol_atm / R.vol; R['pcr'] = R.vol_p / R.vol_c.replace(0, np.nan); R['size'] = R.vol / R.trades
R['pw_abn'] = R.vol_pw / prev('vol_pw', 20); R['cw_abn'] = R.vol_cw / prev('vol_cw', 20)
F = R[['root', 'date', 'liq20', 'abn', 'abn5', 'atm_sh', 'pcr', 'size', 'pw_abn', 'cw_abn']].rename(columns={'date': 't0'})


def stats(r):
    r = r.dropna(); w, l = r[r > 0].sum(), -r[r < 0].sum()
    return len(r), 100 * r.mean(), 100 * r.median(), r.mean() / r.std() * np.sqrt(len(r)), w / l if l > 0 else np.inf


def deciles(T, col, ret='ret_25', k=10, lab=''):
    x = T.dropna(subset=[col, ret])
    if len(x) < 200: return
    sp = x[col].corr(x[ret], method='spearman')
    x = x.assign(d=pd.qcut(x[col].rank(method='first'), k, labels=False) + 1)
    print(f'\n-- {lab}{col}: n {len(x)}, spearman {sp:+.3f}   (decile: range | n mean med t PF at f=.25 | mid mean)')
    for d_, y in x.groupby('d'):
        n, m, md, t, pf = stats(y[ret])
        print(f'  d{d_:2d} [{y[col].min():9.3f}, {y[col].max():9.3f}]  n {n:5d}  {m:+6.1f}  med {md:+6.1f}  t {t:5.2f}  PF {pf:4.2f}'
              f'  | mid {100 * y[ret.replace("25", "0")].mean():+6.1f}')


FEATS = ['liq20', 'abn', 'abn5', 'atm_sh', 'pcr', 'size']
# ---- A
A = pd.read_parquet(f'{D}/event_vol/trades.parquet')
A = A[(A.k == 1) & ~A.split_in_hold & A.E1_settled]
A = A.merge(U.rename(columns={'d': 't0'}), on=['root', 't0']).sort_values('root').drop_duplicates(['cik', 'R'])
for f in [0, 25, 50]: A[f'ret_{f}'] = A[f'spnl_C_{f}'] / A[f'cred_{f}']
A = A[(A.cred_25 > 0) & np.isfinite(A.ret_25)].merge(F, on=['root', 't0'], how='left')
print('=' * 90 + f'\nA  STRADDLE HARVESTER (short at R-1, to expiry): {len(A)} trades; features at R-1\n' + '=' * 90)
for c in FEATS: deciles(A, c)
# ---- B
B = pd.read_parquet(f'{D}/vv_long/trades.parquet')
B = B[~B.split_in_hold & np.isfinite(B.ret_0) & (B.ent_ask > 0)]
B = B.merge(U.rename(columns={'d': 't0'}), on=['root', 't0']).sort_values('root').drop_duplicates(['cik', 'R']).merge(F, on=['root', 't0'], how='left')
print('\n' + '=' * 90 + f'\nB  VV LONG (~14 d before -> R-1): {len(B)} trades; features at entry\n' + '=' * 90)
for c in FEATS: deciles(B, c)
# ---- C
C = pd.read_parquet(f'{D}/skew_vert/trades.parquet')
C = C[~C.split_in_life & np.isfinite(C.ret_0) & (C.mid > 0.05) & ~C.earn_in_life & (C.mid / C.width >= 0.1)]
C = C.merge(U.rename(columns={'d': 't0'}), on=['root', 't0']).merge(F, on=['root', 't0'], how='left')
for f in [0, 25]: C[f'xs_{f}'] = C[f'ret_{f}'] - C.groupby(['leg', 'E'])[f'ret_{f}'].transform('mean')
C['wing_abn'] = np.where(C.side == 'C', C.cw_abn, C.pw_abn)
C['own_z'] = np.where(C.side == 'C', C.call_skew_z, C.put_skew_z)
S = pd.read_parquet(f'{D}/skew_vert/skew10.parquet', columns=['root', 'date', 'call_skew', 'put_skew']).sort_values(['root', 'date'])
S['dcall5'] = S.call_skew - S.groupby('root').call_skew.shift(5); S['dput5'] = S.put_skew - S.groupby('root').put_skew.shift(5)
C = C.merge(S[['root', 'date', 'dcall5', 'dput5']].rename(columns={'date': 't0'}), on=['root', 't0'], how='left')
C['dskew5'] = np.where(C.side == 'C', C.dcall5, C.dput5)
C['gate'] = (C.iv_long < C.rv20) & (C.rv20 < C.iv_short)
print('\n' + '=' * 90 + f'\nC  SKEW VERTICALS: {len(C)} spreads; EXCESS returns (minus the same leg/expiry mean), f=.25\n' + '=' * 90)
for leg in ['c25', 'p25']:
    x = C[C.leg == leg]
    for c in ['liq20', 'abn', 'wing_abn', 'dskew5', 'size']: deciles(x, c, ret='xs_25', lab=f'{leg} ')
# the user's hypothesis: a skew pushed by FLOW (steepening on heavy wing volume) vs steepening on no volume
print('\n== steepening (dskew5 in its bottom 20%) split by wing volume (wing_abn), excess f=.25 ==')
for leg in ['c25', 'c15', 'p25', 'p15']:
    x = C[C.leg == leg].dropna(subset=['dskew5', 'wing_abn'])
    st = x[x.dskew5 <= x.dskew5.quantile(.2)]
    for lab, y in [('low wing vol (<= 0.8x)', st[st.wing_abn <= .8]), ('normal', st[(st.wing_abn > .8) & (st.wing_abn < 1.5)]),
                   ('heavy wing vol (>= 1.5x)', st[st.wing_abn >= 1.5])]:
        n, m, md, t, pf = stats(y.xs_25)
        print(f'  {leg} {lab:26s} n {n:5d}  excess {m:+6.1f}  t {t:5.2f}  | raw f25 {100 * y.ret_25.mean():+6.1f}')
