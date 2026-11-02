"""Breakout BO-5 — the volume delta DURING the trades (user 2026-11-02: "we should take a look at what the volume delta was
for our successful trades in the system. Was the volume delta in our favor for the duration of the trades or against
us?").

⚠ A HINDSIGHT DESCRIPTION, not a gate: the delta over a trade is known only once the trade is over.

Every LEG of BO-3 at --vol-min (default 10 min): dimb = side × Σ delta ÷ Σ volume over the bars entry_t ≤ t ≤ exit_t
(> 0 = the aggressors pushed our way while we held), and the same over the first 30 s and 60 s of the leg (capped at
the exit). Legs are bucketed by their outcome (raw bp, side × (exit − entry) / entry) and by how they ended (flip =
stopped at the other end of the consolidation and reversed; trail = the released leg's 1m trailing stop; eod).

  cd research && python -u scripts/futures/breakout_trade_delta.py > data/futures/breakout/trade_delta.md 2> logs/breakout_trade_delta.log; echo exit $?
"""
import argparse
import sys
import time

import duckdb
import numpy as np
import pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--run', default='data/futures/breakout/run_bo3')
ap.add_argument('--bars', default='data/futures/intraday_1s_ticks')
ap.add_argument('--vol-min', type=float, default=10.0)
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

con = duckdb.connect()
con.execute("set memory_limit = '3GB'; set threads = 4")
L = con.execute(f"""SELECT contract, td, side, leg, entry_t, exit_t, reason, year(td) <= 2022 fit,
                           1e4 * side * (exit_px - entry_px) / abs(entry_px) r, (exit_px - entry_px) * side / tick tk
                    FROM read_parquet('{a.run}/legs/*.parquet') WHERE vol_min = {a.vol_min} AND entry_px <> 0""").df()
log(f'{len(L):,} legs')
for c in ['d_all', 'd_30', 'd_60', 'v_all']:
    L[c] = np.nan
days = L.groupby('td').indices
for k, (td, idx) in enumerate(sorted(days.items())):
    sub = L.iloc[idx]
    cs = ','.join(f"'{c}'" for c in sub.contract.unique())
    b = con.execute(f"""SELECT ticker, bucket t, volume::DOUBLE v, delta::DOUBLE d FROM read_parquet('{a.bars}/{pd.Timestamp(td):%Y-%m-%d}.parquet')
                        WHERE ticker IN ({cs}) AND bucket >= 34200 AND bucket < 57600 ORDER BY ticker, bucket""").df()
    for c, g in b.groupby('ticker'):
        m = (sub.contract == c).to_numpy()
        ii = idx[m]
        t = g.t.to_numpy(); cv = np.r_[0, np.cumsum(g.v.to_numpy())]; cd = np.r_[0, np.cumsum(g.d.to_numpy())]
        e, x = L.entry_t.to_numpy()[ii], L.exit_t.to_numpy()[ii]
        s = L.side.to_numpy()[ii].astype(float)
        i0 = np.searchsorted(t, e, 'left'); i1 = np.searchsorted(t, x, 'right')
        out = {}
        for nm, hi in [('all', i1), ('30', np.minimum(i1, np.searchsorted(t, e + 30, 'right'))),
                       ('60', np.minimum(i1, np.searchsorted(t, e + 60, 'right')))]:
            with np.errstate(all='ignore'):
                out[nm] = s * (cd[hi] - cd[i0]) / (cv[hi] - cv[i0])
        L.loc[L.index[ii], 'd_all'] = out['all']; L.loc[L.index[ii], 'd_30'] = out['30']; L.loc[L.index[ii], 'd_60'] = out['60']
        L.loc[L.index[ii], 'v_all'] = cv[i1] - cv[i0]
    if k % 250 == 0:
        log(f'  {k + 1}/{len(days)} {td}')
log('delta done')

L['hold_s'] = L.exit_t - L.entry_t
L['out'] = pd.cut(L.r, [-np.inf, -1e-12, 1e-12, np.inf], labels=['loser', 'flat', 'winner'])
# the big winners / losers: the top / bottom decile of raw bp among the legs
lo, hi = L.r.quantile([0.1, 0.9])


def row(lab, m):
    x = L[m]
    def q(c): return f'{x[c].median():+.3f} / {100 * (x[c] > 0).mean():.0f} %'
    return f'| {lab} | {len(x):,} | {x.r.mean():+.1f} | {x.hold_s.median():.0f} | {q("d_30")} | {q("d_60")} | {q("d_all")} |'


print(f'# Breakout BO-5 — the volume delta DURING the trades (BO-3, V {a.vol_min:g} min, every leg, 2015–2026)\n')
print('⚠ HINDSIGHT: the delta over a trade is known only after it. dimb = side × Σ delta ÷ Σ volume over the leg '
      '(> 0 = the aggressors pushed OUR way). Cells: median dimb / share of legs with dimb > 0.\n')
hdr = '| legs | n | mean bp | median hold s | first 30 s | first 60 s | the whole leg |\n|---|---|---|---|---|---|---|'
print('## By outcome\n'); print(hdr)
print(row('all legs', L.r.notna()))
print(row('winners (r > 0)', L.out == 'winner'))
print(row('losers (r < 0)', L.out == 'loser'))
print(row(f'big winners (top 10 %, r ≥ {hi:+.1f} bp)', L.r >= hi))
print(row(f'big losers (bottom 10 %, r ≤ {lo:+.1f} bp)', L.r <= lo))
print()
print('## By how the leg ended × outcome\n'); print(hdr)
for rs in ['trail', 'flip', 'eod']:
    for o in ['winner', 'loser']:
        print(row(f'{rs} · {o}s', (L.reason == rs) & (L.out == o)))
print()
print('## Holdout only (2023–26), by outcome\n'); print(hdr)
for lab, m in [('winners', L.out == 'winner'), ('losers', L.out == 'loser'), ('big winners', L.r >= hi), ('big losers', L.r <= lo)]:
    print(row(lab, m & ~L.fit))
print()
print('## The outcome by the whole-leg delta (quintiles of d_all; mean bp, win %)\n')
print('| d_all quintile | range | legs | mean bp | win % |\n|---|---|---|---|---|')
qq = pd.qcut(L.d_all, 5, labels=False, duplicates='drop')
for k in range(5):
    x = L[qq == k]
    print(f'| Q{k + 1} | {x.d_all.min():+.2f} … {x.d_all.max():+.2f} | {len(x):,} | {x.r.mean():+.1f} | {100 * (x.r > 0).mean():.0f} |')
print()
log('done')
