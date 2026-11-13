#!/usr/bin/env python3
"""The 2012 -> 2026 survey of the three VV systems (user 2026-11-13), on the --full runs (survey_run.sh):
  A  STRADDLE HARVESTER: SHORT the ATM straddle at R-1's close, held to the E1 expiry (intrinsic) - event_vol k=1 (SH-21).
     Gate: ratio0 = implied event move / RMS of the previous 4-8 reaction moves in its TOP 10%.
  B  VV PRE-EARNINGS LONG: buy the ATM straddle of the first monthly >= R ~14 days before the announcement, sell at R-1
     (SH-22). Gate: all four (implied/last implied, implied/hist-avg implied, iv - last rv_win, iv - hist-avg rv_win) in
     their CHEAPEST 40%.
  C  SKEW VERTICALS: buy ~50d, sell the 25d/15d wing, ~10 DTE, held to expiry (SH-23). No earnings in the life, a
     debit floor (debit >= 10% of the width: SH-23's correction), gate ATM IV < RV20 < wing IV; veto own skew z >= 1.2.
P&L in % of the trade's OWN PREMIUM (credit received / debit paid; user 2026-11-13: not of the stock's notional).
PF = sum of winning returns / |sum of losing returns| (each trade's return = P&L / its premium).
Fills f: 0 = mid, .25 / .5 = that fraction of the half-spread paid on every opening and closing trade (expiry = intrinsic).
Universe: a trade counts when its root is in the PIT universe (universe_daily_full.parquet) on its entry day. Earnings
trades are deduped by (CIK, reaction day) (FB and META carry the same releases in 2021-22).
GATES ARE CAUSAL: a threshold is the percentile of every trade entered BEFORE the month of the entry (>= 300 of them);
earlier trades are left out of the gated rows.
    cd research && python3 scripts/options/survey_report.py > logs/options_survey_report.log
"""
import sys, numpy as np, pandas as pd, duckdb
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 40); pd.set_option('display.max_rows', 200)
D = 'data/options/full'
FS = [('mid', 0), ('f25', 25), ('f50', 50)]
U = duckdb.sql("""select root, cast(date as date) d, cik from 'data/options/universe_daily_full.parquet' where in_universe""").df()
U['d'] = pd.to_datetime(U.d)


def in_univ(T, col='t0'):
    m = T.merge(U.rename(columns={'d': col}), on=['root', col], how='left', indicator=True)
    out = m[m._merge == 'both'].drop(columns='_merge')
    print(f'  in the universe on the entry day: {len(out):,} of {len(T):,}')
    return out


def stats(r):
    r = r.dropna()
    if len(r) < 2: return dict(n=len(r))
    w, l = r[r > 0].sum(), -r[r < 0].sum()
    return dict(n=len(r), mean=100 * r.mean(), med=100 * r.median(), t=r.mean() / r.std() * np.sqrt(len(r)),
                win=100 * (r > 0).mean(), PF=w / l if l > 0 else np.inf)


def line(T, lab, w=46):
    s = {k: stats(T[f'ret_{f}']) for k, f in FS}
    n = s['mid'].get('n', 0)
    if n < 5: print(f'{lab:{w}s} n={n}'); return
    print(f'{lab:{w}s} n={n:6d} | ' + ' | '.join(f"{k} {v['mean']:+6.1f} med {v['med']:+6.1f} PF {v['PF']:4.2f} t {v['t']:5.2f}"
                                              for k, v in s.items()) + f" | win(f25) {s['f25']['win']:3.0f}%")


def by_year(T, lab, ycol):
    print(f'\n  {lab} by year (f=.25 unless noted):')
    rows = []
    for y, g in T.groupby(T[ycol].dt.year):
        s = stats(g.ret_25); m = stats(g.ret_0)
        if s.get('n', 0) < 2: rows.append(dict(year=y, n=s.get('n', 0))); continue
        rows.append(dict(year=y, n=s['n'], mid=round(m['mean'], 1), mean=round(s['mean'], 1), med=round(s['med'], 1),
                         PF=round(s['PF'], 2), t=round(s['t'], 2), win=round(s['win'])))
    print(pd.DataFrame(rows).to_string(index=False))


def expanding(T, col, q, dcol, min_n=300):
    """the q-quantile of col over all trades with entry before the first day of each trade's month; NaN until min_n"""
    T = T.sort_values(dcol); x = T[col].values; d = T[dcol].values.astype('datetime64[D]')
    months = pd.to_datetime(T[dcol]).dt.to_period('M')
    thr = pd.Series(np.nan, index=T.index)
    for m in months.unique():
        cut = np.datetime64(m.start_time.date(), 'D')
        prior = x[(d < cut) & np.isfinite(x)]
        if len(prior) >= min_n: thr[months.index[months == m]] = np.quantile(prior, q)
    return thr.reindex(T.index)


# ---------------------------------------------------------------- A: Straddle Harvester (short at R-1, to expiry)
print('=' * 100 + '\nA  STRADDLE HARVESTER: short ATM straddle, entry R-1 close, held to the E1 expiry (event_vol k=1)\n' + '=' * 100)
A = pd.read_parquet(f'{D}/event_vol/trades.parquet')
A = A[(A.k == 1) & ~A.split_in_hold & A.E1_settled].copy()
A = in_univ(A)
A = A.sort_values('root').drop_duplicates(['cik', 'R'])
for k_, f in FS:
    A[f'ret_{f}'] = A[f'spnl_C_{f}'] / A[f'cred_{f}']
A = A[(A.cred_25 > 0) & np.isfinite(A.ret_25)]
print(f'  trades {len(A):,}, names {A.root.nunique()}, R {A.R.min().date()}..{A.R.max().date()}, '
      f'median credit {100 * (A.cred_0 / A.S0).median():.1f}% of S, median half-spread {100 * ((A.ent_ask - A.ent_bid) / (A.ent_ask + A.ent_bid)).median():.1f}% of mid')
A['thr'] = expanding(A, 'ratio0', 0.9, 't0')
line(A, 'all')
line(A[np.isfinite(A.ratio0)], 'all with a ratio (>= 4 previous events)')
G = A[np.isfinite(A.thr) & (A.ratio0 >= A.thr)]
line(G, 'GATE ratio0 >= expanding p90')
line(G[G.S0 >= 10], '  gate, S >= $10')
line(G[G.S0 < 10], '  gate, S < $10')
line(A[np.isfinite(A.thr) & (A.ratio0 < A.thr)], 'the rest (after the warm-up)')
by_year(A, 'all', 'R'); by_year(G, 'GATE', 'R'); by_year(G[G.S0 >= 10], 'GATE S >= $10', 'R')

# ---------------------------------------------------------------- B: VV pre-earnings long straddle
print('\n' + '=' * 100 + '\nB  VV PRE-EARNINGS LONG: ATM straddle of the first monthly >= R, ~14 days before, sold at R-1\n' + '=' * 100)
B = pd.read_parquet(f'{D}/vv_long/trades.parquet')
B = B[~B.split_in_hold & np.isfinite(B.ret_0) & (B.ent_ask > 0)].copy()
B = in_univ(B)
B = B.sort_values('root').drop_duplicates(['cik', 'R'])
B['g1'] = B.iv_em - B.last_rv_win; B['g2'] = B.iv_em - B.hist_avg_rv_win
F4 = ['implied_to_last_implied_ratio', 'implied_hist_avg_implied_ratio', 'g1', 'g2']
print(f'  trades {len(B):,}, names {B.root.nunique()}, R {B.R.min().date()}..{B.R.max().date()}, '
      f'median half-spread {100 * ((B.ent_ask - B.ent_bid) / (B.ent_ask + B.ent_bid)).median():.1f}% of mid')
line(B, 'all')
ok = np.isfinite(B[F4]).all(axis=1)
B4 = B[ok].copy()
for c in F4: B4['thr_' + c] = expanding(B4, c, 0.4, 't0')
warm = np.isfinite(B4[['thr_' + c for c in F4]]).all(axis=1)
cheap = warm & np.logical_and.reduce([B4[c] <= B4['thr_' + c] for c in F4])
line(B4, 'all with the four features')
line(B4[cheap], 'GATE all four in the cheapest 40% (expanding)')
line(B4[warm & ~cheap], 'the rest (after the warm-up)')
by_year(B, 'all', 'R'); by_year(B4[cheap], 'GATE', 'R')

# ---------------------------------------------------------------- C: skew verticals
print('\n' + '=' * 100 + '\nC  SKEW VERTICALS: long ~50d / short the 25d or 15d wing, ~10 DTE, to expiry; no earnings in the life\n' + '=' * 100)
C = pd.read_parquet(f'{D}/skew_vert/trades.parquet')
C = C[~C.split_in_life & np.isfinite(C.ret_0) & (C.mid > 0.05) & ~C.earn_in_life].copy()
C = in_univ(C)
C['own_z'] = np.where(C.side == 'C', C.call_skew_z, C.put_skew_z)
C['floor'] = C.mid / C.width >= 0.10
C['gate'] = (C.iv_long < C.rv20) & (C.rv20 < C.iv_short)
print(f'  spreads {len(C):,}, names {C.root.nunique()}, t0 {C.t0.min().date()}..{C.t0.max().date()}; debit < 10% of the '
      f'width (dropped) {100 * (~C.floor).mean():.1f}%')
C = C[C.floor]
for f in [0, 25, 50]:   # EXCESS = the return minus the same leg's average on the same expiry (removes the market's direction)
    C[f'xs_{f}'] = C[f'ret_{f}'] - C.groupby(['leg', 'E'])[f'ret_{f}'].transform('mean')


def xline(x, lab, w=46):
    s = {k: stats(x[f'xs_{f}']) for k, f in FS}
    if s['mid'].get('n', 0) < 5: return
    nd = x.E.nunique()
    tc = {k: (lambda g: g.mean() / g.std() * np.sqrt(len(g)) if len(g) > 1 else np.nan)(x.groupby('E')[f'xs_{f}'].mean()) for k, f in FS}
    print(f'{lab:{w}s} n={s["mid"]["n"]:6d} | ' + ' | '.join(f"{k} {v['mean']:+6.1f} (date-t {tc[k]:5.2f})" for k, v in s.items()) + f' | {nd} expiries')


for leg in ['c25', 'c15', 'p25', 'p15']:
    x = C[C.leg == leg]
    print(f'\n -- {leg}')
    xline(x[x.gate & ~(x.own_z >= 1.2)], '  EXCESS: gate & not flat')
    xline(x[x.own_z >= 1.2], '  EXCESS: flat z >= 1.2')
    line(x, '  all')
    line(x[x.gate], '  GATE ATM < RV20 < wing')
    line(x[x.gate & ~(x.own_z >= 1.2)], '  GATE & not flat (skew z < 1.2)')
    line(x[x.own_z >= 1.2], '  flat skew z >= 1.2 (the veto)')
GC = C[C.gate & ~(C.own_z >= 1.2)]
for leg in ['c25', 'c15', 'p25', 'p15']:
    by_year(GC[GC.leg == leg], f'GATE & not flat, {leg}', 't0')
