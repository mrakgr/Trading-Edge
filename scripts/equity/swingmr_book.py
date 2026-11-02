#!/usr/bin/env python3
"""SWING MR — the BOOK (user 2026-11-02: "one position per name, a cap on positions held at once, equal sizing ... Yeah,
let's do this").

A day-by-day portfolio over the SW sampler rows:
  candidates = the rows passing --setup (SQL over swingmr rows ⋈ breadth), entered at D's CLOSE (x1_rc; the 15:59
  decision), out at the x1 exit (the first close above the 5-day SMA, ≤ 10 days). (The next-open entry is not booked
  yet: its mark to market needs the entry on D+1.)
  ONE position per name: a name held (or exiting today) takes no new entry. At most K positions; when more candidates
  than free slots arrive, the LOWEST --rank first (default z_20; -med60 = the most liquid first). Each position = 1/K of the REALIZED equity at
  its entry (compounding). Cost = --cost-bp per round trip off every trade.
  ⭐ MARK TO MARKET (feedback: exit-date equity curves hide the drawdown): every open position is valued at each day's
  close from daily_episodes_causal (cn = close·n, dividends as cash), so the drawdown includes open positions.
Output: the summary, the year table, and <out>_trades.parquet / <out>_equity.parquet.

  cd research && python -u scripts/equity/swingmr_book.py --setup "z_20 <= -2.19" --k 10 --out data/equity/swingmr/book_z20_k10 > logs/swingmr_book.log 2>&1; echo exit $?
"""
import argparse
import os
import sys
import time

import duckdb
import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import data_v2  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument('--setup', required=True, help='SQL over the rows (+ br20/br50/br200 and their *_prev).')
ap.add_argument('--k', type=int, default=10)
ap.add_argument('--rank', default='z_20', help='An SQL expression over the rows; the LOWEST first when the slots bind (e.g. z_20, or -med60 = the most liquid first).')
ap.add_argument('--cost-bp', type=float, default=10.0)
ap.add_argument('--max-gross', type=float, default=float('inf'), help='Cap the sum of the open notionals (at entry) at this × the realized equity; a new position is cut to the room left (2.0 = Reg T overnight).')
ap.add_argument('--size', default='1.0', help='An SQL expression over the rows: the position = size × equity / K (user 2026-11-02: size up the strong cells).')
ap.add_argument('--out', required=True)
ap.add_argument('--quiet', action='store_true')
a = ap.parse_args()
T0 = time.time()
def log(*x):
    if not a.quiet: print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

con = data_v2.attach(duckdb.connect(), 'main')
con.execute("SET memory_limit='4GB'; SET threads=4")
ret = 'x1_rc'
C = con.execute(f"""SELECT r.ticker, r.date, r.x1_date, r.{ret} ret, ({a.rank}) rk, r.x1_k, ({a.size})::DOUBLE sz
    FROM read_parquet('data/equity/swingmr/rows/*.parquet') r JOIN read_parquet('data/equity/swingmr/breadth.parquet') b USING (date)
    WHERE ({a.setup}) AND r.{ret} IS NOT NULL ORDER BY r.date, rk""").df()
cal = pd.to_datetime(con.execute("SELECT date FROM read_parquet('data/equity/swingmr/breadth.parquet') ORDER BY 1").df().date).to_numpy()
log(f'{len(C):,} candidate rows, {len(cal):,} sessions')

# ---- the day loop
by_day = {d: g for d, g in C.groupby('date', sort=True)}
held = {}                                     # ticker → (entry date, exit date, notional, ret)
equity = 1.0
trades = []
for d in cal:
    d = pd.Timestamp(d)
    exiting_today = set()
    for tk in [t for t, p in held.items() if p[1] <= d]:
        e0, x0, notion, r = held.pop(tk)
        pnl = notion * (r - a.cost_bp) / 1e4
        equity += pnl
        trades.append((tk, e0, x0, notion, r, pnl))
        exiting_today.add(tk)
    g = by_day.get(d)
    if g is not None and len(held) < a.k:
        for row in g.itertuples(index=False):
            if len(held) >= a.k:
                break
            if row.ticker in held or row.ticker in exiting_today:
                continue
            room = a.max_gross * equity - sum(p[2] for p in held.values())
            notion = min(row.sz * equity / a.k, room)
            if notion <= 0.01 * equity / a.k:
                continue
            held[row.ticker] = (d, pd.Timestamp(row.x1_date), notion, row.ret)
for tk, (e0, x0, notion, r) in held.items():                # still open at the data's end: closed at their exit row
    pnl = notion * (r - a.cost_bp) / 1e4
    trades.append((tk, e0, x0, notion, r, pnl))
T = pd.DataFrame(trades, columns=['ticker', 'entry', 'exit', 'notional', 'ret_bp', 'pnl']).sort_values('entry')
log(f'{len(T):,} trades')

# ---- mark to market: each trade's value at every close between its entry and exit
con.register('T', T[['ticker', 'entry', 'exit', 'notional', 'pnl']].reset_index().rename(columns={'index': 'tid'}))
px = 'e.close'
M = con.execute(f"""
    WITH tr AS (SELECT T.*, e.episode, e.close * e.n cn0, {px} * e.n p0, e.cum_div cd0 FROM T
                JOIN daily_episodes_causal e ON e.ticker = T.ticker AND e.date = T.entry),
    path AS (SELECT tr.tid, d.date, tr.notional, tr.pnl, tr.exit,
                    tr.notional * ((d.close * d.n - tr.p0) + (d.cum_div - tr.cd0)) / tr.p0 unreal
             FROM tr JOIN daily_episodes_causal d ON d.ticker = tr.ticker AND d.episode = tr.episode
                     AND d.date >= tr.entry AND d.date < tr.exit)
    SELECT date, sum(unreal) unreal, count(*) open_n FROM path GROUP BY date ORDER BY date""").df()
real = T.groupby('exit').pnl.sum().reindex(pd.to_datetime(cal), fill_value=0.0).cumsum()
E = pd.DataFrame({'date': pd.to_datetime(cal)})
E['realized'] = 1.0 + real.to_numpy()
E = E.merge(M.assign(date=pd.to_datetime(M.date)), on='date', how='left').fillna({'unreal': 0.0, 'open_n': 0})
# ⚠ the entry day's cost is charged at the exit; the open positions carry their mark only
E['equity'] = E.realized + E.unreal
E['dd'] = E.equity / E.equity.cummax() - 1
G = con.execute("""SELECT d.date, sum(T.notional) gross FROM (SELECT DISTINCT date FROM read_parquet('data/equity/swingmr/breadth.parquet')) d
                     JOIN T ON d.date >= T.entry AND d.date < T.exit GROUP BY d.date""").df()
E = E.merge(G.assign(date=pd.to_datetime(G.date)), on='date', how='left').fillna({'gross': 0.0})
E['gross_frac'] = E.gross / E.equity
E.to_parquet(a.out + '_equity.parquet', index=False)
T.to_parquet(a.out + '_trades.parquet', index=False)

yrs = (E.date.iloc[-1] - E.date.iloc[0]).days / 365.25
g, l = T.pnl[T.pnl > 0].sum(), -T.pnl[T.pnl < 0].sum()
cagr = E.equity.iloc[-1] ** (1 / yrs) - 1
print(f'## Book: setup `{a.setup}` · K = {a.k} · rank {a.rank} · size {a.size} · max gross {a.max_gross:g} · entry at the close · cost {a.cost_bp:g} bp\n')
print(f'- trades {len(T):,} ({len(T) / yrs:,.0f}/yr), win {100 * (T.ret_bp > a.cost_bp).mean():.1f} %, PF (net) {g / l:.2f}, '
      f'mean net {T.ret_bp.mean() - a.cost_bp:+.1f} bp, worst trade {T.ret_bp.min():+.0f} bp')
print(f'- equity ×{E.equity.iloc[-1]:.2f} over {yrs:.1f} yrs = CAGR {100 * cagr:+.1f} %, MAX DRAWDOWN (marked to market) '
      f'{100 * E.dd.min():.1f} % on {E.date[E.dd.idxmin()].date()}, mean positions open {E.open_n.mean():.1f} of {a.k}, '
      f'gross exposure mean {100 * E.gross_frac.mean():.0f} % / max {100 * E.gross_frac.max():.0f} % of equity\n')
E['yr'] = E.date.dt.year
T['yr'] = pd.to_datetime(T.exit).dt.year
Y = E.groupby('yr').agg(start=('equity', 'first'), end=('equity', 'last'), dd=('dd', 'min'), occ=('open_n', 'mean'))
prev = 1.0
print('| year | return | max DD in the year | trades | PF net | mean open |\n|---|---|---|---|---|---|')
for y, r in Y.iterrows():
    ty = T[T.yr == y]
    gy, ly = ty.pnl[ty.pnl > 0].sum(), -ty.pnl[ty.pnl < 0].sum()
    print(f'| {y} | {100 * (r.end / prev - 1):+.1f} % | {100 * r.dd:.1f} % | {len(ty):,} | {gy / ly if ly > 0 else float("inf"):.2f} | {r.occ:.1f} |')
    prev = r.end
print()
