"""Breakout BO-2 — an INDEPENDENT re-implementation of TradingEdge.Breakout's stop-and-reverse engine, compared leg for leg
with a run's output.

Independent parts: the window's σ from PREFIX SUMS (σ of bars [l, r] exactly, no incremental subtraction) and its left
edge by a plain greedy loop; the window high/low by slicing; the 1m low/high by a brute-force scan; the causal volume
reference re-derived from the bars and the fronts. The state machine is re-coded from the rules (Engine.fs header).

  cd research && ./TradingEdge.Breakout/bin/Release/net10.0/TradingEdge.Breakout --window sigma --roots ES --limit-days 40 -o data/futures/breakout/test_es
  cd research && python -u scripts/futures/breakout_check.py --run data/futures/breakout/test_es --root ES > logs/breakout_check.log 2> logs/breakout_check.err; echo exit $?
"""
import argparse
import sys

import duckdb
import numpy as np
import pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--run', required=True)
ap.add_argument('--root', required=True)
ap.add_argument('--bars', default='data/futures/intraday_1s_ticks')
ap.add_argument('--thr', default='1,2,3,4,6,8')
ap.add_argument('--floors', default='2,5,10,20')
ap.add_argument('--nvref', type=int, default=20)
a = ap.parse_args()
THR = [float(x) for x in a.thr.split(',')]
FL = [float(x) for x in a.floors.split(',')]
S0, ENTRY_END, FLAT = 34200, 54000, 57600

con = duckdb.connect()
con.execute("set memory_limit = '3GB'; set threads = 4")
got = con.execute(f"SELECT * FROM read_parquet('{a.run}/legs/*.parquet')").df()
tds = set(pd.to_datetime(got.td.unique()))
fr = con.execute(f"""
    WITH v AS (SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})\\.parquet$', 1) AS DATE) d, bucket >= 61200 nxt,
                      ticker, sum(volume) vol
               FROM read_parquet('{a.bars}/*.parquet', filename = true) WHERE left(ticker, length(ticker) - 3) = '{a.root}' GROUP BY ALL),
    s AS (SELECT CASE WHEN nxt THEN CAST(d + INTERVAL 1 DAY AS DATE) ELSE d END td, ticker, sum(vol) vol FROM v GROUP BY ALL),
    l AS (SELECT td, ticker, row_number() OVER (PARTITION BY td ORDER BY vol DESC, ticker) rk FROM s)
    SELECT td, lag(ticker) OVER (ORDER BY td) front FROM l WHERE rk = 1 ORDER BY td""").df().dropna()
fr = fr[pd.to_datetime(fr.td) <= max(tds)]
tick = dict(con.execute("SELECT contract, tick FROM read_parquet('data/futures/grids.parquet') WHERE tick IS NOT NULL").fetchall())


def bars_of(td, c):
    return con.execute(f"""SELECT bucket t, vwap::DOUBLE vwap, volume::DOUBLE v FROM read_parquet('{a.bars}/{pd.Timestamp(td):%Y-%m-%d}.parquet')
                           WHERE ticker = '{c}' AND bucket >= {S0} AND bucket < {FLAT} ORDER BY bucket""").df()


def session(td, c, vref):
    b = bars_of(td, c)
    t, p, v = b.t.to_numpy(), b.vwap.to_numpy(), b.v.to_numpy()
    n, tk = len(b), tick[c]
    x = (p - p[0]) / tk
    P0, P1, P2 = np.r_[0, np.cumsum(v)], np.r_[0, np.cumsum(v * x)], np.r_[0, np.cumsum(v * x * x)]
    def sig(l, r):
        sv = P0[r + 1] - P0[l]; m = (P1[r + 1] - P1[l]) / sv
        return np.sqrt(max(0.0, (P2[r + 1] - P2[l]) / sv - m * m))
    lo60 = np.full(n, np.nan); hi60 = np.full(n, np.nan)
    for i in range(n):
        j = np.searchsorted(t, t[i] - 60)
        if j < i:
            lo60[i], hi60[i] = p[j:i].min(), p[j:i].max()
    out = []
    for thr in THR:
        Lp = np.zeros(n, np.int64); l = 0              # the left edge after pushing bar i
        for i in range(n):
            while l < i and sig(l, i) > thr:
                l += 1
            Lp[i] = l
        for fl in FL:
            leg = None; pend = None; rel = False; last_cons = -1
            for i in range(n):
                if pend == 'enter':
                    leg['entry_t'], leg['entry_px'] = t[i], p[i]
                elif pend == 'flip':
                    out.append({**leg, 'exit_t': t[i], 'exit_px': p[i], 'reason': 'flip'})
                    leg = None if i == n - 1 else {**leg, 'leg': leg['leg'] + 1, 'side': -leg['side'], 'sig_t': leg['exit_sig_t'],
                                                    'entry_t': t[i], 'entry_px': p[i], 'released_t': None, 'exit_sig_t': None}
                elif pend == 'exit':
                    out.append({**leg, 'exit_t': t[i], 'exit_px': p[i], 'reason': 'trail'}); leg = None; rel = False
                pend = None
                if leg is None and i > 0 and t[i] <= ENTRY_END and i < n - 1:
                    L = Lp[i - 1]
                    vol = P0[i] - P0[L]
                    if L > last_cons and vol >= fl * vref:
                        hi, lo = p[L:i].max(), p[L:i].min()
                        s = 1 if p[i] > hi else (-1 if p[i] < lo else 0)
                        if s:
                            leg = dict(thr=thr, floor=fl, td=td, ep_t=t[i], leg=0, side=s, sig_t=t[i], entry_t=None, entry_px=None,
                                       released_t=None, exit_sig_t=None, cons_first_t=t[L], cons_bars=i - L, cons_vol=vol,
                                       cons_hi=hi, cons_lo=lo, cons_last=i - 1)
                            pend = 'enter'; rel = False; last_cons = i - 1
                            continue
                if leg is not None and pend is None and leg['entry_t'] is not None and i < n - 1:
                    if not rel and Lp[i] > leg['cons_last']:
                        rel = True; leg['released_t'] = t[i]
                    s = leg['side']
                    lvl = (leg['cons_lo'] if s > 0 else leg['cons_hi']) if not rel else (lo60[i] if s > 0 else hi60[i])
                    if s * (p[i] - lvl) < 0:
                        leg['exit_sig_t'] = t[i]; pend = 'exit' if rel else 'flip'
            if leg is not None and leg['entry_t'] is not None:
                out.append({**leg, 'exit_t': t[n - 1], 'exit_px': p[n - 1], 'reason': 'eod'})
    return out


vpm, rows = [], []
for td, c in zip(fr.td, fr.front):
    b = bars_of(td, c)
    if len(b) == 0:
        continue
    if len(vpm) >= a.nvref and pd.Timestamp(td) in tds and len(b) >= 3:
        rows += session(td, c, np.mean(vpm[-a.nvref:]))
    vpm.append(b.v.sum() / ((b.t.iloc[-1] - b.t.iloc[0] + 1) / 60.0))
    print(f'{td} {c} legs so far {len(rows)}', file=sys.stderr, flush=True)

exp = pd.DataFrame(rows)
key = ['thr', 'floor', 'td', 'ep_t', 'leg']
got['td'] = pd.to_datetime(got.td); exp['td'] = pd.to_datetime(exp.td)
print(f'engine {len(got):,} legs · reference {len(exp):,}')
m = got.merge(exp, on=key, how='outer', suffixes=('', '_x'), indicator=True)
print(m._merge.value_counts().to_string())
both = m[m._merge == 'both']
bad = 0
for col in ['side', 'sig_t', 'entry_t', 'entry_px', 'exit_sig_t', 'exit_t', 'exit_px', 'reason', 'released_t',
            'cons_first_t', 'cons_bars', 'cons_vol', 'cons_hi', 'cons_lo']:
    g, x = both[col], both[col + '_x']
    if col == 'reason':
        ne = g.astype(str) != x.astype(str)
    else:
        g, x = pd.to_numeric(g, errors='coerce').astype(float), pd.to_numeric(x, errors='coerce').astype(float)
        ne = ~(np.isclose(g, x, rtol=1e-9, atol=1e-9) | (g.isna() & x.isna()))
    print(f'{col:14s} mismatches {int(ne.sum()):,}')
    bad += int(ne.sum())
print('PASS' if bad == 0 and (m._merge != 'both').sum() == 0 else 'FAIL')
