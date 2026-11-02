"""Breakout BO-7 — an INDEPENDENT re-implementation of TradingEdge.Breakout --window momentum's trips, compared trip for trip.

Independent parts: the 60-bar channel by slicing (strict max / min of the PRIOR 60 bars; at the first warm bar the
59 before it — PitFader's warmth: the channel, the signal bar included, holds 60 bars), the entry cutoff, every trip with its fill, its marks
(the first bar at or after entry + m, the delta / volume from the entry bar to it on slices) and its pre-entry windows,
and the causal volume reference (volume per present bar over the previous 20 sessions). The 236 PitFader features are
the sealed blocks (PIT-3/4) called in PitFader's order and are not re-derived here.

  cd research && ./TradingEdge.Breakout/bin/Release/net10.0/TradingEdge.Breakout --window momentum --roots ES --limit-days 40 -o data/futures/breakout/test_mom
  cd research && python -u scripts/futures/breakout_check_mom.py --run data/futures/breakout/test_mom --root ES --limit-days 40 > logs/breakout_check_mom.log 2> logs/breakout_check_mom.err; echo exit $?
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
ap.add_argument('--entry-bars', type=int, default=60)
ap.add_argument('--nref', type=int, default=20)
ap.add_argument('--limit-days', type=int, required=True)
a = ap.parse_args()
MARKS = [1, 2, 3, 5, 10, 15, 20, 30, 45, 60]
PRE = [1, 2, 5, 10, 15, 20, 30, 45, 60]
S0, ENTRY_END, FLAT, NB = 34200, 54000, 57600, a.entry_bars

con = duckdb.connect()
con.execute("set memory_limit = '3GB'; set threads = 4")
keep = ['td', 'side', 'sig_t', 'entry_t', 'entry_px', 'eod', 'vref_bar'] + [f'pre_{k}_{n}' for n in PRE for k in ('vol', 'dimb')] + \
       [f'{k}_{m}' for m in MARKS for k in ('t', 'px', 'dimb', 'vol')]
got = con.execute(f"SELECT {', '.join(keep)} FROM read_parquet('{a.run}/legs/*.parquet')").df()
fr = con.execute(f"""
    WITH v AS (SELECT CAST(regexp_extract(filename, '(\\d{{4}}-\\d{{2}}-\\d{{2}})\\.parquet$', 1) AS DATE) d, bucket >= 61200 nxt,
                      ticker, sum(volume) vol
               FROM read_parquet('{a.bars}/*.parquet', filename = true) WHERE left(ticker, length(ticker) - 3) = '{a.root}' GROUP BY ALL),
    s AS (SELECT CASE WHEN nxt THEN CAST(d + INTERVAL 1 DAY AS DATE) ELSE d END td, ticker, sum(vol) vol FROM v GROUP BY ALL),
    l AS (SELECT td, ticker, row_number() OVER (PARTITION BY td ORDER BY vol DESC, ticker) rk FROM s)
    SELECT td, lag(ticker) OVER (ORDER BY td) front FROM l WHERE rk = 1 ORDER BY td""").df().dropna().head(a.limit_days)

rows, hist = [], []
for td, c in zip(fr.td, fr.front):
    b = con.execute(f"""SELECT bucket t, vwap::DOUBLE p, volume::DOUBLE v, delta::DOUBLE d FROM read_parquet('{a.bars}/{pd.Timestamp(td):%Y-%m-%d}.parquet')
                        WHERE ticker = '{c}' AND bucket >= {S0} AND bucket < {FLAT} ORDER BY bucket""").df()
    n = len(b)
    if n < 3:
        continue
    if len(hist) >= a.nref:
        vref = np.mean(hist[-a.nref:])
        t, p, v, dl = b.t.to_numpy(), b.p.to_numpy(), b.v.to_numpy(), b.d.to_numpy()
        for i in range(NB - 1, n - 1):
            if t[i] > ENTRY_END:
                break
            hi, lo = p[max(0, i - NB):i].max(), p[max(0, i - NB):i].min()   # the prior NB bars (NB − 1 at the first warm bar)
            for s in (1, -1):
                if not ((s > 0 and p[i] > hi) or (s < 0 and p[i] < lo)):
                    continue
                e = i + 1
                r = dict(td=td, side=s, sig_t=t[i], entry_t=t[e], entry_px=p[e], vref_bar=vref)
                for k in PRE:
                    sv = v[max(0, i + 1 - k):i + 1].sum()
                    r[f'pre_vol_{k}'] = sv
                    r[f'pre_dimb_{k}'] = s * dl[max(0, i + 1 - k):i + 1].sum() / sv if sv > 0 else np.nan
                exit_j = None
                for m in MARKS:
                    j = int(np.searchsorted(t, t[e] + m, 'left'))
                    if j < n:
                        sv = v[e:j + 1].sum()
                        r[f't_{m}'], r[f'px_{m}'], r[f'vol_{m}'] = t[j], p[j], sv
                        r[f'dimb_{m}'] = s * dl[e:j + 1].sum() / sv if sv > 0 else np.nan
                    else:
                        r[f't_{m}'] = r[f'px_{m}'] = r[f'vol_{m}'] = r[f'dimb_{m}'] = np.nan
                r['eod'] = np.isnan(r['t_60'])
                if r['eod']:                     # the 60 s exit at the session's last bar
                    sv = v[e:].sum()
                    r['t_60'], r['px_60'], r['vol_60'] = t[n - 1], p[n - 1], sv
                    r['dimb_60'] = s * dl[e:].sum() / sv if sv > 0 else np.nan
                rows.append(r)
    hist.append(b.v.sum() / n)
    print(f'{td} {c} refs {len(hist)} trips so far {len(rows)}', file=sys.stderr, flush=True)

exp = pd.DataFrame(rows)
key = ['td', 'side', 'sig_t']
got['td'] = pd.to_datetime(got.td); exp['td'] = pd.to_datetime(exp.td)
print(f'engine {len(got):,} trips · reference {len(exp):,}')
m = got.merge(exp, on=key, how='outer', suffixes=('', '_x'), indicator=True)
print(m._merge.value_counts().to_string())
both = m[m._merge == 'both']
bad = 0
for col in [c for c in keep if c not in key]:
    g, x = both[col], both[col + '_x']
    if col == 'eod':
        ne = g.astype(bool) != x.astype(bool)
    else:
        g, x = pd.to_numeric(g, errors='coerce').astype(float), pd.to_numeric(x, errors='coerce').astype(float)
        ne = ~(np.isclose(g, x, rtol=1e-9, atol=1e-9) | (g.isna() & x.isna()))
    if ne.any():
        print(f'{col:14s} mismatches {int(ne.sum()):,}')
    bad += int(ne.sum())
print(f'{len(keep) - len(key)} columns compared')
print('PASS' if bad == 0 and (m._merge != 'both').sum() == 0 else 'FAIL')
