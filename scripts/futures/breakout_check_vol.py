"""Breakout BO-3 — an INDEPENDENT re-implementation of TradingEdge.Breakout --window volume, compared leg for leg.

Independent parts: each window's oldest bar by a binary search on the cumulative volume, its mean / σ / high / low computed
DIRECTLY on the slice (the partial bar weighted by the remainder; no prefix-sum subtraction), the session median σ and the
causal reference σ (the mean of the previous 20 sessions' medians), the 1m low/high by a brute-force scan, the volume
reference from the bars and the fronts. The state machine is re-coded from the rules (VolEngine.fs header).

  cd research && ./TradingEdge.Breakout/bin/Release/net10.0/TradingEdge.Breakout --window volume --roots ES --limit-days 60 -o data/futures/breakout/test_vol
  cd research && python -u scripts/futures/breakout_check_vol.py --run data/futures/breakout/test_vol --root ES > logs/breakout_check_vol.log 2> logs/breakout_check_vol.err; echo exit $?
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
ap.add_argument('--vol-mins', default='5,10,20,40')
ap.add_argument('--max-ratio', type=float, default=1.0)
ap.add_argument('--nvref', type=int, default=20)
ap.add_argument('--nsig', type=int, default=20)
ap.add_argument('--limit-days', type=int, required=True)
a = ap.parse_args()
VM = [float(x) for x in a.vol_mins.split(',')]
S0, ENTRY_END, FLAT = 34200, 54000, 57600

con = duckdb.connect()
con.execute("set memory_limit = '3GB'; set threads = 4")
got = con.execute(f"SELECT * FROM read_parquet('{a.run}/legs/*.parquet')").df()
fr = con.execute(f"""
    WITH v AS (SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})\\.parquet$', 1) AS DATE) d, bucket >= 61200 nxt,
                      ticker, sum(volume) vol
               FROM read_parquet('{a.bars}/*.parquet', filename = true) WHERE left(ticker, length(ticker) - 3) = '{a.root}' GROUP BY ALL),
    s AS (SELECT CASE WHEN nxt THEN CAST(d + INTERVAL 1 DAY AS DATE) ELSE d END td, ticker, sum(vol) vol FROM v GROUP BY ALL),
    l AS (SELECT td, ticker, row_number() OVER (PARTITION BY td ORDER BY vol DESC, ticker) rk FROM s)
    SELECT td, lag(ticker) OVER (ORDER BY td) front FROM l WHERE rk = 1 ORDER BY td""").df().dropna().head(a.limit_days)
tick = dict(con.execute("SELECT contract, tick FROM read_parquet('data/futures/grids.parquet') WHERE tick IS NOT NULL").fetchall())


def bars_of(td, c):
    return con.execute(f"""SELECT bucket t, vwap::DOUBLE vwap, volume::DOUBLE v FROM read_parquet('{a.bars}/{pd.Timestamp(td):%Y-%m-%d}.parquet')
                           WHERE ticker = '{c}' AND bucket >= {S0} AND bucket < {FLAT} ORDER BY bucket""").df()


def windows(t, p, v, tk, V):
    """per bar r: (warm, k, mean, σ ticks, hi, lo) of the window ending at r, computed on the slice"""
    n = len(p); cum = np.cumsum(v)
    out = [None] * n
    for r in range(n):
        if cum[r] < V:
            continue
        # k = the largest index with sum(v[k..r]) >= V  ⇔  cum[r] - cum[k-1] >= V
        k = int(np.searchsorted(cum, cum[r] - V, side='right'))   # first index with cum[k] > cum[r] - V
        rest = cum[r] - cum[k]                                     # bars k+1..r
        w = np.r_[V - rest, v[k + 1:r + 1]]
        xs = (p[k:r + 1] - p[0]) / tk
        m = (w * xs).sum() / V
        sg = np.sqrt(max(0.0, (w * xs * xs).sum() / V - m * m))
        out[r] = (k, p[0] + tk * m, sg, p[k:r + 1].max(), p[k:r + 1].min())
    return out, cum


def session(td, c, vref, refs, emit):
    b = bars_of(td, c)
    t, p, v = b.t.to_numpy(), b.vwap.to_numpy(), b.v.to_numpy()
    n, tk = len(b), tick[c]
    lo60 = np.full(n, np.nan); hi60 = np.full(n, np.nan)
    if emit:
        for i in range(n):
            j = np.searchsorted(t, t[i] - 60)
            if j < i:
                lo60[i], hi60[i] = p[j:i].min(), p[j:i].max()
    out, meds = [], []
    for vi, vm in enumerate(VM):
        V = vm * vref
        W, cum = windows(t, p, v, tk, V)
        sg = [w[2] for w in W if w is not None]
        meds.append(np.median(sg) if sg else np.nan)
        if not emit:
            continue
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
            if leg is None and i > 0 and t[i] <= ENTRY_END and i < n - 1 and W[i - 1] is not None and W[i - 1][0] > last_cons:
                k, m, sgm, hi, lo = W[i - 1]
                ratio = sgm / refs[vi]
                if ratio <= a.max_ratio:
                    s = 1 if p[i] > hi else (-1 if p[i] < lo else 0)
                    if s:
                        leg = dict(vol_min=vm, td=td, ep_t=t[i], leg=0, side=s, sig_t=t[i], entry_t=None, entry_px=None,
                                   released_t=None, exit_sig_t=None, cons_first_t=t[k], cons_bars=i - k, cons_sigma=sgm,
                                   cons_ratio=ratio, cons_hi=hi, cons_lo=lo, cons_last=i - 1, last_cum=cum[i - 1], V=V)
                        pend = 'enter'; rel = False; last_cons = i - 1
                        continue
            if leg is not None and pend is None and leg['entry_t'] is not None and i < n - 1:
                if not rel and cum[i] - leg['last_cum'] >= V:
                    rel = True; leg['released_t'] = t[i]
                s = leg['side']
                lvl = (leg['cons_lo'] if s > 0 else leg['cons_hi']) if not rel else (lo60[i] if s > 0 else hi60[i])
                if s * (p[i] - lvl) < 0:
                    leg['exit_sig_t'] = t[i]; pend = 'exit' if rel else 'flip'
        if leg is not None and leg['entry_t'] is not None:
            out.append({**leg, 'exit_t': t[n - 1], 'exit_px': p[n - 1], 'reason': 'eod'})
    return out, meds


vpm, mh, rows = [], [], []
for td, c in zip(fr.td, fr.front):
    b = bars_of(td, c)
    if len(b) == 0:
        continue
    if len(vpm) >= a.nvref and len(b) >= 3:
        refs = [np.nanmean([m[vi] for m in mh[-a.nsig:]]) if len(mh) >= a.nsig else np.nan for vi in range(len(VM))]
        o, meds = session(td, c, np.mean(vpm[-a.nvref:]), refs, len(mh) >= a.nsig)
        rows += o; mh.append(meds)
    vpm.append(b.v.sum() / ((b.t.iloc[-1] - b.t.iloc[0] + 1) / 60.0))
    print(f'{td} {c} vref {len(vpm)} σ-ref {len(mh)} legs so far {len(rows)}', file=sys.stderr, flush=True)

exp = pd.DataFrame(rows)
key = ['vol_min', 'td', 'ep_t', 'leg']
got['td'] = pd.to_datetime(got.td); exp['td'] = pd.to_datetime(exp.td)
print(f'engine {len(got):,} legs · reference {len(exp):,}')
m = got.merge(exp, on=key, how='outer', suffixes=('', '_x'), indicator=True)
print(m._merge.value_counts().to_string())
both = m[m._merge == 'both']
bad = 0
for col in ['side', 'sig_t', 'entry_t', 'entry_px', 'exit_sig_t', 'exit_t', 'exit_px', 'reason', 'released_t',
            'cons_first_t', 'cons_bars', 'cons_sigma', 'cons_ratio', 'cons_hi', 'cons_lo']:
    g, x = both[col], both[col + '_x']
    if col == 'reason':
        ne = g.astype(str) != x.astype(str)
    else:
        g, x = pd.to_numeric(g, errors='coerce').astype(float), pd.to_numeric(x, errors='coerce').astype(float)
        ne = ~(np.isclose(g, x, rtol=1e-7, atol=1e-7) | (g.isna() & x.isna()))
    print(f'{col:14s} mismatches {int(ne.sum()):,}' + (f'  max |Δ| {np.nanmax(np.abs(g - x)):.3g}' if ne.any() and col != 'reason' else ''))
    bad += int(ne.sum())
print('PASS' if bad == 0 and (m._merge != 'both').sum() == 0 else 'FAIL')
