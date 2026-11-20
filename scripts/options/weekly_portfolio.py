#!/usr/bin/env python3
"""THE WEEKLY SKEW PORTFOLIO (user 2026-11-20): the short-dated skew system as an account, 2013-2026.
Trades: short_dated.py + hedge_daily.py, the 25-delta short sold at the BID at the close, held to expiry, DAILY delta hedge
($0.006 per share traded), no earnings in the hold, half-spread <= 5%, sd_common.drop_bad. Signal: the leg's skew
(IV25/IV_ATM) >= the prior years' 90th percentile per (kind, leg) (SH-28/34); stock CALLS vetoed at a 52-week closing high
(SH-35). One trade per name, day and leg: the expiry nearest 5 trading days (3-7 allowed).
Margin per contract (estimates): Reg T premium + max(r x S - OTM, 10% x (S call / K put)), r = 15% broad ETFs (SPY QQQ IWM DIA),
20% else; PM = the worst intrinsic loss of the option + its delta hedge over +-8% (broad ETFs) / +-15% (else).
ACCOUNT: each day's signals ranked by skew percentile; a position gets contracts = floor(5% of capital / margin) (>= 1),
taken while the open margin stays <= 80% of capital (margin released at expiry). P&L booked at expiry; no compounding
(fixed capital). Reports %/yr, max drawdown, worst month, months / years positive, Sharpe of daily booked P&L.
    cd research && python3 scripts/options/weekly_portfolio.py > logs/options_weekly_portfolio.log
"""
import os, sys
import duckdb, numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sd_common import drop_bad
pd.set_option('display.width', 250)
BROAD = {'SPY', 'QQQ', 'IWM', 'DIA'}
T = pd.read_parquet('data/options/short_dated/trades.parquet')
T = T[T.inst.isin(['P25', 'C25']) & ~T.earn]
T = drop_bad(T, quiet=True)
H = pd.read_parquet('data/options/short_dated/hedge.parquet', columns=['root', 'date', 'exp', 'K', 'r', 'inst', 'hd', 'turn'])
D = T.merge(H, on=['root', 'date', 'exp', 'K', 'r', 'inst'])
del T, H
D['mid'] = (D.bid + D.ask) / 2
D = D[D.mid >= .05].copy()
D['hs'] = (D.ask - D.bid) / 2 / D.mid; D['year'] = D.date.dt.year
D['skw'] = np.where(D.inst == 'P25', D.iv25p, D.iv25c) / D.iv_atm
D['pskw'] = np.nan
for (k, i), g in D.groupby(['kind', 'inst']):
    for y in sorted(g.year.unique()):
        p = np.sort(g.loc[g.year < y, 'skw'].dropna().values)
        if len(p) < 2000: continue
        m = g.index[(g.year == y) & g.skw.notna()]
        D.loc[m, 'pskw'] = np.searchsorted(p, D.loc[m, 'skw'].values, 'right') / len(p)
X = D[(D.pskw >= .9) & (D.hs <= .05) & D.tdte.between(3, 7)].copy()
del D
# 52-week high veto for stock calls
u = duckdb.sql("select distinct root, underlying from 'data/options/universe_daily_full.parquet'").df().groupby('root').underlying.first()
X['und'] = X.root.map(lambda r: u.get(r, r))
c = duckdb.connect('data/trading.db', read_only=True)
tl = ','.join(repr(t) for t in X.und.unique())
da = c.sql(f"select ticker, date, close, n from daily_adjusted where ticker in ({tl}) and date >= '2011-01-01' order by ticker, date").df()
da['date'] = pd.to_datetime(da.date)
da['seg'] = (da.groupby('ticker').date.diff().dt.days > 45).groupby(da.ticker).cumsum()
da['pa'] = da.close * da.n
da['mx'] = da.groupby(['ticker', 'seg']).pa.transform(lambda x: x.shift(1).rolling(252, min_periods=200).max())
da['hi52'] = da.pa >= da.mx
X = X.merge(da[['ticker', 'date', 'hi52']].rename(columns={'ticker': 'und'}), on=['und', 'date'], how='left')
X = X[~((X.kind == 'stock') & (X.inst == 'C25') & (X.hi52 == True))]
X['d5'] = (X.tdte - 5).abs()
X = X.sort_values(['d5', 'tdte']).drop_duplicates(['root', 'date', 'inst']).copy()
X['pl'] = 100 * (X.bid - X.settle + X.hd - X.turn * 0.006)                       # $ per contract
rt = np.where(X.root.isin(BROAD), 0.15, 0.20); mv = np.where(X.root.isin(BROAD), 0.08, 0.15)
otm = np.where(X.r == 'C', np.maximum(X.K - X.S, 0), np.maximum(X.S - X.K, 0))
X['regt'] = 100 * (X.bid + np.maximum(rt * X.S - otm, 0.10 * np.where(X.r == 'C', X.S, X.K)))
worst = np.zeros(len(X))
for m in np.arange(-0.15, 0.15 + 1e-9, 0.01):
    Sx = X.S.values * (1 + m)
    intr = np.where(X.r.values == 'C', np.maximum(Sx - X.K.values, 0), np.maximum(X.K.values - Sx, 0))
    loss = intr - X.bid.values - X.dl.values * (Sx - X.S.values)
    worst = np.where(np.abs(m) <= mv + 1e-9, np.maximum(worst, loss), worst)
X['pm'] = np.maximum(100 * worst, 50.0)
# STRESS loss per contract (2026-11-20, after the margin-sized run blew up): the worst loss of the option + its delta hedge
# at intrinsic over +-25% (stocks), +-10% (broad ETFs), +-15% (other ETFs)
sv = np.where(X.root.isin(BROAD), 0.10, np.where(X.kind == 'etf', 0.15, 0.25))
worst = np.zeros(len(X))
for m in np.arange(-0.25, 0.25 + 1e-9, 0.01):
    Sx = X.S.values * (1 + m)
    intr = np.where(X.r.values == 'C', np.maximum(Sx - X.K.values, 0), np.maximum(X.K.values - Sx, 0))
    worst = np.where(np.abs(m) <= sv + 1e-9, np.maximum(worst, intr - X.bid.values - X.dl.values * (Sx - X.S.values)), worst)
X['stress'] = 100 * worst
X['xd'] = pd.to_datetime(X.xd)
X = X[X.year >= 2013].sort_values(['date', 'pskw'], ascending=[True, False])
print(f'signal trades (one per name / day / leg, 3-7 DTE): {len(X):,}; per contract: mean ${X.pl.mean():.1f}, median ${X.pl.median():.1f}, '
      f'{100 * X.pl.sum() / (100 * X.bid).sum():+.1f}% of premium, PF {X.pl[X.pl > 0].sum() / -X.pl[X.pl < 0].sum():.2f}, '
      f'Reg T margin median ${X.regt.median():,.0f}, PM ${X.pm.median():,.0f}')
print(X.groupby(['kind', 'inst']).pl.agg(['size', 'mean', 'median']).round(1).to_string())


def account(regime, cap, per_pos=0.05, total=0.80):
    open_ = []                              # (expiry, margin)
    booked = {}; ntr = 0; mused = []
    for d, g in X.groupby('date', sort=True):
        open_ = [(e, m) for e, m in open_ if e > d]
        used = sum(m for _, m in open_)
        for _, t in g.iterrows():
            mc = t[regime]
            n = max(1, int(cap * per_pos // mc))
            if used + n * mc > cap * total: continue
            used += n * mc; open_.append((t.xd, n * mc)); ntr += 1
            booked[t.xd] = booked.get(t.xd, 0.0) + n * t.pl
        mused.append(used / cap)
    s = pd.Series(booked).sort_index()
    days = pd.bdate_range(X.date.min(), X.xd.max()); s = s.reindex(days, fill_value=0.0)
    eq = cap + s.cumsum(); yrs = (days.max() - days.min()).days / 365.25
    dd = float((eq - eq.cummax()).min()); mon = s.groupby(s.index.to_period('M')).sum(); yr = s.groupby(s.index.year).sum()
    print(f'\n{regime.upper()} ${cap / 1e3:.0f}k (<= {100 * per_pos:.0f}% margin per position, <= {100 * total:.0f}% total): {ntr:,} positions, '
          f'mean margin used {100 * np.mean(mused):.0f}%')
    print(f'   {100 * s.sum() / yrs / cap:+.1f}%/yr  maxDD {100 * dd / cap:.1f}%  worst month {100 * mon.min() / cap:+.1f}%  '
          f'months+ {100 * (mon > 0).mean():.0f}%  years+ {(yr > 0).sum()}/{len(yr)}  Sharpe(daily booked) {s.mean() / s.std() * np.sqrt(252):.2f}')
    print('   ' + ' '.join(f'{y}:{100 * v / cap:+.1f}%' for y, v in yr.items()))


POS = []


def account_stress(cap, pos_risk, tot_risk, regt_cap=0.80, min_px=0.0, max_n=10**9):
    """contracts = floor(pos_risk x cap / stress) (skip if 0); open stress <= tot_risk x cap; open Reg T margin <= regt_cap x cap"""
    open_ = []                              # (expiry, stress, margin)
    booked = {}; ntr = 0; su = []; mu = []
    for d, g in X.groupby('date', sort=True):
        open_ = [o for o in open_ if o[0] > d]
        s_used = sum(o[1] for o in open_); m_used = sum(o[2] for o in open_)
        for _, t in g.iterrows():
            if t.S < min_px: continue
            n = min(int(cap * pos_risk // t.stress) if t.stress > 0 else 0, max_n)
            if n < 1: continue
            if s_used + n * t.stress > cap * tot_risk or m_used + n * t.regt > cap * regt_cap: continue
            s_used += n * t.stress; m_used += n * t.regt; open_.append((t.xd, n * t.stress, n * t.regt)); ntr += 1
            booked[t.xd] = booked.get(t.xd, 0.0) + n * t.pl
            POS.append((t.root, t.date, t.inst, n, t.pl, t.S, t.bid, t.stress, t.kind))
        su.append(s_used / cap); mu.append(m_used / cap)
    s = pd.Series(booked).sort_index()
    days = pd.bdate_range(X.date.min(), X.xd.max()); s = s.reindex(days, fill_value=0.0)
    eq = cap + s.cumsum(); yrs = (days.max() - days.min()).days / 365.25
    dd = float((eq - eq.cummax()).min()); mon = s.groupby(s.index.to_period('M')).sum(); yr = s.groupby(s.index.year).sum()
    print(f'\n${cap / 1e3:.0f}k, stress per position <= {100 * pos_risk:.1f}%, total open stress <= {100 * tot_risk:.0f}%, Reg T margin <= '
          f'{100 * regt_cap:.0f}%: {ntr:,} positions, mean open stress {100 * np.mean(su):.0f}%, mean Reg T margin {100 * np.mean(mu):.0f}%')
    print(f'   {100 * s.sum() / yrs / cap:+.1f}%/yr  maxDD {100 * dd / cap:.1f}%  worst month {100 * mon.min() / cap:+.1f}%  '
          f'months+ {100 * (mon > 0).mean():.0f}%  years+ {(yr > 0).sum()}/{len(yr)}  Sharpe(daily booked) {s.mean() / s.std() * np.sqrt(252):.2f}')
    print('   ' + ' '.join(f'{y}:{100 * v / cap:+.1f}%' for y, v in yr.items()))


print(f'\n== STRESS-SIZED (stress per contract median ${X.stress.median():,.0f}, p90 ${X.stress.quantile(.9):,.0f})')
for pr, tr in [(0.005, 0.20), (0.01, 0.20), (0.01, 0.40), (0.02, 0.40)]:
    POS.clear()
    account_stress(100_000, pr, tr)
    if (pr, tr) == (0.01, 0.20): P1 = pd.DataFrame(POS, columns=['root', 'date', 'inst', 'n', 'pl', 'S', 'bid', 'stress', 'kind'])
P1['usd'] = P1.n * P1.pl; P1['year'] = P1.date.dt.year
print('\n== DIAGNOSIS ($100k, 1% / 20%): where the P&L comes from')
print('by price band of the underlying:'); print(P1.groupby(pd.cut(P1.S, [0, 10, 20, 50, 100, 1e9])).agg(pos=('usd', 'size'), usd=('usd', 'sum'), contracts=('n', 'mean')).round(0).to_string())
for y in [2021, 2022, 2023, 2024]:
    z = P1[P1.year == y]; top = z.groupby('root').usd.sum().sort_values()
    print(f'{y}: total ${z.usd.sum():,.0f} over {len(z)} positions; top winners ' + ', '.join(f'{r} ${v:,.0f}' for r, v in top.tail(6)[::-1].items())
          + '; worst ' + ', '.join(f'{r} ${v:,.0f}' for r, v in top.head(3).items()))
big = P1.sort_values('usd')
print('largest single positions:', '; '.join(f'{r.root} {r.date:%Y-%m-%d} {r.inst} n={r.n} S={r.S:.2f} bid={r.bid:.2f} ${r.usd:,.0f}' for _, r in pd.concat([big.head(5), big.tail(5)]).iterrows()))

# REALISTIC (2026-11-20): the P&L above is dominated by sub-$10 / sub-$20 meme names (AMC 2021 1,670 contracts) - not a
# tradable book. Underlying >= $20 (and >= $50), at most 20 contracts per position.
print('\n== REALISTIC: underlying >= min price, <= 20 contracts per position')
for mp in (20, 50):
    for pr, tr in [(0.01, 0.20), (0.02, 0.30)]:
        print(f'-- underlying >= ${mp}')
        POS.clear(); account_stress(100_000, pr, tr, min_px=mp, max_n=20)
        P2 = pd.DataFrame(POS, columns=['root', 'date', 'inst', 'n', 'pl', 'S', 'bid', 'stress', 'kind']); P2['usd'] = P2.n * P2.pl
        top = P2.groupby('root').usd.sum().sort_values()
        print('   top names ' + ', '.join(f'{r} ${v:,.0f}' for r, v in top.tail(5)[::-1].items()) + '; worst ' + ', '.join(f'{r} ${v:,.0f}' for r, v in top.head(3).items())
              + f'; by kind/leg ' + ', '.join(f'{k}/{i} ${v:,.0f}' for (k, i), v in P2.groupby(['kind', 'inst']).usd.sum().items()))
