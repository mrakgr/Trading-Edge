"""Breakout BO-4 — TAPE READING AT THE BREAK (user 2026-11-02: "Maybe that could be worth it? Let's give it a try"): does a
break on HIGH VOLUME, with the aggressive flow (the volume delta) pushing in the break's direction, follow through?

The episodes of BO-3 (the fixed-volume window, every V) and BO-2 (the σ window, floor 10 min, σ 3 and 8) joined to the
feature row of their START bar (the break bar, read inclusive; the entry fills at the next bar — causal). Features:
  rv_bar   = the break bar's volume ÷ the root's average volume per second (vref_min / 60)
  rv_cons  = the break bar's volume ÷ the consolidation's mean volume per bar (its volume ÷ its bars)
  rv_5 / rv_30 = the last 5 / 30 bars' volume ÷ (5 / 30 × vref_min / 60)
  dimb_N   = side × delta_N_bar ÷ vol_N_bar, N = 30, 60, 120: > 0 = the aggressors push WITH the break
Quintile edges from the FIT (2015–22) only, per family; cells = episode PF raw fit → **holdout** (holdout episodes).

  cd research && python -u scripts/futures/breakout_tape.py > data/futures/breakout/tape.md 2> logs/breakout_tape.log; echo exit $?
"""
import sys
import time

import duckdb
import numpy as np
import pandas as pd

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

con = duckdb.connect()
con.execute("set memory_limit = '4GB'; set threads = 4; set preserve_insertion_order = false")
FEATS = ['bar_vol', 'vol_5_bar', 'delta_30_bar', 'vol_30_bar', 'delta_60_bar', 'vol_60_bar', 'delta_120_bar', 'vol_120_bar']


def episodes(run, fam_cols, cons_vol, where):
    q = f"""
      WITH e AS (
        SELECT contract, td, {fam_cols}, ep_t, any_value(side) FILTER (leg = 0) side, year(td) <= 2022 fit,
               sum(1e4 * side * (exit_px - entry_px) / abs(entry_px)) r, any_value(vref_min) vref,
               any_value({cons_vol}) cvol, any_value(cons_bars) cbars
        FROM read_parquet('{run}/legs/*.parquet') WHERE entry_px <> 0 AND {where} GROUP BY contract, td, {fam_cols}, ep_t)
      SELECT e.*, {', '.join('f.' + c for c in FEATS)}
      FROM e JOIN read_parquet('{run}/feats/*.parquet') f ON f.contract = e.contract AND f.td = e.td AND f.sig_t = e.ep_t"""
    d = con.execute(q).df()
    ps = d.vref / 60.0
    d['rv_bar'] = d.bar_vol / ps
    d['rv_cons'] = d.bar_vol / (d.cvol / d.cbars)
    d['rv_5'] = d.vol_5_bar / (5 * ps)
    d['rv_30'] = d.vol_30_bar / (30 * ps)
    for n in (30, 60, 120):
        d[f'dimb_{n}'] = d.side * d[f'delta_{n}_bar'] / d[f'vol_{n}_bar']
    return d


def pf(r):
    r = r[np.isfinite(r)]
    g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l


def f(x): return 'inf' if np.isinf(x) else f'{x:.2f}'


def cell(d, m):
    return f'{f(pf(d.r[m & d.fit].to_numpy()))} → **{f(pf(d.r[m & ~d.fit].to_numpy()))}** ({int((m & ~d.fit).sum()):,})'


FEAT_LIST = ['rv_bar', 'rv_cons', 'rv_5', 'rv_30', 'dimb_30', 'dimb_60', 'dimb_120']
NAME = {'rv_bar': 'break-bar volume vs average', 'rv_cons': 'break-bar volume vs the consolidation\'s bars',
        'rv_5': 'last 5 bars\' volume vs average', 'rv_30': 'last 30 bars\' volume vs average',
        'dimb_30': 'delta WITH the break, 30 bars', 'dimb_60': 'delta WITH the break, 60 bars', 'dimb_120': 'delta WITH the break, 120 bars'}


def section(title, d, fam):
    print(f'## {title}\n')
    print(f'All episodes: {cell(d, pd.Series(True, index=d.index))}\n')
    print('| feature | Q1 (lowest) | Q2 | Q3 | Q4 | Q5 (highest) | fit Q5 edge (median of families) |\n|---|---|---|---|---|---|---|')
    Q = {}
    for x in FEAT_LIST:
        cells, edges5 = [], []
        qcol = pd.Series(-1, index=d.index)
        for _, g in d.groupby(fam):
            e = np.nanquantile(g[x][g.fit], [0.2, 0.4, 0.6, 0.8])
            edges5.append(e[3])
            qcol.loc[g.index] = np.searchsorted(e, g[x].to_numpy(), side='right')
        qcol[d[x].isna()] = -1
        Q[x] = qcol
        for k in range(5):
            cells.append(cell(d, qcol == k))
        print(f'| {NAME[x]} | ' + ' | '.join(cells) + f' | {np.median(edges5):.3g} |')
    print()
    print('**The tape-reading combination** (volume quintile from the fit × the delta with or against the break):\n')
    print('| volume × delta | dimb_30 ≤ 0 (against / balanced) | dimb_30 > 0 (with the break) | dimb_30 in its fit Q5 |\n|---|---|---|---|')
    for x in ['rv_bar', 'rv_cons', 'rv_5']:
        for lab, m in [('Q1–Q4', Q[x].between(0, 3)), ('Q5 (high volume)', Q[x] == 4)]:
            print(f'| {NAME[x]} {lab} | {cell(d, m & (d.dimb_30 <= 0))} | {cell(d, m & (d.dimb_30 > 0))} | {cell(d, m & (Q["dimb_30"] == 4))} |')
    print()


print('# Breakout BO-4 — tape reading at the break: the break bar\'s volume and the delta with the break\n')
print('Cells: episode PF raw **fit 2015–22 → holdout 2023–26** (holdout episodes). Quintile edges from the fit, per family. '
      'The feature row is the break bar\'s (inclusive); the entry fills at the next bar.\n')
d = episodes('data/futures/breakout/run_bo3', 'vol_min', 'vol', 'true')
log('BO-3 episodes', len(d))
section('BO-3 — the fixed-volume window, V 5 / 10 / 20 / 40 min pooled (edges per V)', d, 'vol_min')
for vm in [10.0, 40.0]:
    section(f'BO-3 — V {vm:g} min alone', d[d.vol_min == vm].copy(), 'vol_min')
del d
d = episodes('data/futures/breakout/run_bo2', 'thr, floor', 'cons_vol', 'floor = 10 AND thr IN (3, 8)')
log('BO-2 episodes', len(d))
section('BO-2 — the σ window, floor 10 min, σ 3 and 8 pooled (edges per σ)', d, 'thr')
log('done')
