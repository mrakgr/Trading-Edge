"""Breakout BO-6 — ABSORPTION's report (user 2026-11-02: "strong delta that isn't confirmed by price movement"; both families;
the delta "as confirmation to enter into trades or to cut them quickly ... we could even flip the trade").

A trip = one detection (FADE: against the aggressors at the detection; LEVEL: the first break of the detection window's
range within 15 min). Returns raw bp, side × (exit − entry) / entry; STOPPED = the stop's fill replaces horizon H's exit
when stop_sig_t < exit_t_H. FIT = 2015–22, HOLDOUT = 2023–26. "−1t" = one tick per round trip.
THE CONFIRMATION, scored causally: the decision uses d30 (the delta over [entry, entry + 30 s], known at entry + 30 s);
the REST of the trade is scored from exit_px_60 (the first bar at or after entry + 60 s — strictly after the decision's
bars) to the horizon. Rest PF < 1 on the cut side = cutting helps; well below 1 = flipping would earn ≈ 1 / PF.

  cd research && python -u scripts/futures/breakout_report_bo6.py > data/futures/breakout/report_bo6.md 2> logs/breakout_report_bo6.log; echo exit $?
"""
import sys
import time

import duckdb
import numpy as np
import pandas as pd

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

GROUPS = {'equity': ['ES', 'MES', 'NQ'], 'bonds': ['ZB', 'ZN', 'ZT'], 'sofr': ['SR3'],
          'fx': ['6A', '6B', '6C', '6E', '6J', '6N', '6S', '6Z', 'M6A', 'M6E', 'MJY', 'M6J', 'E7'],
          'energy': ['CL', 'MCL', 'NG', 'RB'], 'metals': ['HG', 'MGC'], 'ags': ['ZC', 'ZS', 'HE'], 'vx': ['VX']}
H = [30, 60, 300, 900]
con = duckdb.connect()
con.execute("set memory_limit = '4GB'; set threads = 4")
cols = ['root', 'td', 'fam', 'n_win', 'aggr', 'side', 'entry_px', 'tick', 'imb_ratio', 'rv', 'move_ratio', 'stop_sig_t', 'stop_fill_px',
        'd30', 'd60'] + [f'exit_{k}_{h}' for h in H for k in ('t', 'px')]
d = con.execute(f"SELECT {', '.join(cols)} FROM read_parquet('data/futures/breakout/run_bo6/legs/*.parquet') WHERE entry_px <> 0").df()
log(f'{len(d):,} trips')
g = {r: k for k, rs in GROUPS.items() for r in rs}
d['grp'] = d.root.map(g).fillna('other')
d['fit'] = pd.to_datetime(d.td).dt.year <= 2022
E = d.entry_px.abs()
for h in H:
    d[f'r{h}'] = 1e4 * d.side * (d[f'exit_px_{h}'] - d.entry_px) / E
    st = d.stop_sig_t.notna() & (d.stop_sig_t < d[f'exit_t_{h}'])
    d[f's{h}'] = np.where(st, 1e4 * d.side * (d.stop_fill_px - d.entry_px) / E, d[f'r{h}'])
d['tbp'] = 1e4 * d.tick / E
d['with_aggr'] = d.side * d.aggr > 0


def pf(r):
    r = np.asarray(r, float); r = r[np.isfinite(r)]
    gg, ll = r[r > 0].sum(), -r[r < 0].sum()
    return np.nan if len(r) == 0 else (np.inf if ll == 0 else gg / ll)


def f(x): return '—' if x is None or (isinstance(x, float) and np.isnan(x)) else ('inf' if np.isinf(x) else f'{x:.2f}')


def ft(x, col): return f'{f(pf(x.loc[x.fit, col]))} → **{f(pf(x.loc[~x.fit, col]))}**'


print('# Breakout BO-6 — absorption: strong delta the price did not follow\n')
print('Cells: PF raw **fit 2015–22 → holdout 2023–26**. A trip = one detection. STOPPED = the stop beyond the window\'s '
      'extreme (FADE) or the range\'s other end (LEVEL).\n')
print('## By family × window: unstopped / stopped\n')
print('| family | N bars | trips fit / holdout | ' + ' | '.join(f'{h} s unstopped | {h} s stopped' for h in H) + ' | holdout −1t at 300 s (unstopped) |')
print('|---|---|---|' + '---|---|' * len(H) + '---|')
for (fam, n), x in d.groupby(['fam', 'n_win']):
    print(f'| {fam} | {n} | {int(x.fit.sum()):,} / {int((~x.fit).sum()):,} | ' +
          ' | '.join(f'{ft(x, f"r{h}")} | {ft(x, f"s{h}")}' for h in H) +
          f' | {f(pf((x.r300 - x.tbp)[~x.fit]))} |')
print()
print('## LEVEL: did the break go WITH the aggressors (they broke the absorber) or AGAINST them (the absorber won)?\n')
print('| N bars | side | trips holdout | 60 s | 300 s | 300 s stopped | 900 s stopped |\n|---|---|---|---|---|---|---|')
L = d[d.fam == 'level']
for (n, w), x in L.groupby(['n_win', 'with_aggr']):
    print(f'| {n} | {"with the aggressors" if w else "against the aggressors"} | {int((~x.fit).sum()):,} | {ft(x, "r60")} | {ft(x, "r300")} | '
          f'{ft(x, "s300")} | {ft(x, "s900")} |')
print()
log('families')

print('## The ladders (fit-quintile edges per N; PF raw at 300 s, unstopped; fit → holdout)\n')
for fam in ['fade', 'level']:
    x0 = d[d.fam == fam]
    print(f'### {fam.upper()}\n')
    print('| feature | Q1 (lowest) | Q2 | Q3 | Q4 | Q5 (highest) |\n|---|---|---|---|---|---|')
    for feat, nm in [('imb_ratio', 'imbalance ÷ the usual'), ('move_ratio', 'move in the aggressors\' direction ÷ the usual (low = absorbed)'),
                     ('rv', 'volume ÷ the usual')]:
        q = pd.Series(-1, index=x0.index)
        for _, gg in x0.groupby('n_win'):
            e = np.nanquantile(gg.loc[gg.fit, feat], [0.2, 0.4, 0.6, 0.8])
            q.loc[gg.index] = np.searchsorted(e, gg[feat].to_numpy(), side='right')
        print(f'| {nm} | ' + ' | '.join(ft(x0[q == k], 'r300') + f' ({int(((q == k) & ~x0.fit).sum()):,})' for k in range(5)) + ' |')
    print()
log('ladders')

print('## ⭐ The confirmation: the delta over the first 30 s, then the REST of the trade from the 60 s mark\n')
print('Rest = side × (exit at H − exit at 60 s) ÷ entry, bp. Holdout PF of the rest (holdout trips; mean rest bp). A rest PF '
      'well under 1 on the "against" rows = cut there; flipping would earn about 1 / PF before costs.\n')
for fam in ['fade', 'level']:
    x0 = d[d.fam == fam].copy()
    for h in [300, 900]:
        x0[f'rest{h}'] = 1e4 * x0.side * (x0[f'exit_px_{h}'] - x0.exit_px_60) / x0.entry_px.abs()
    print(f'### {fam.upper()}\n')
    print('| d30 (the flow over the first 30 s) | share | rest to 300 s: fit → holdout | holdout mean bp | rest to 900 s: fit → holdout | holdout mean bp |')
    print('|---|---|---|---|---|---|')
    bins = [(-1.01, -0.3, 'strongly against (≤ −0.3)'), (-0.3, -0.1, 'against (−0.3 … −0.1)'), (-0.1, 0.1, 'balanced'),
            (0.1, 0.3, 'with us (0.1 … 0.3)'), (0.3, 1.01, 'strongly with us (≥ 0.3)')]
    for lo, hi, nm in bins:
        x = x0[(x0.d30 > lo) & (x0.d30 <= hi)]
        print(f'| {nm} | {100 * len(x) / len(x0):.0f} % | {ft(x, "rest300")} | {x.rest300[~x.fit].mean():+.2f} | {ft(x, "rest900")} | '
              f'{x.rest900[~x.fit].mean():+.2f} |')
    print()
log('confirmation')

print('## By group (PF raw at 300 s unstopped, fit → holdout (holdout trips)), N 60 and 120 pooled\n')
print('| group | FADE | LEVEL with the aggressors | LEVEL against the aggressors |\n|---|---|---|---|')
x0 = d[d.n_win.isin([60, 120])]
for gname in list(GROUPS) + ['other']:
    x = x0[x0.grp == gname]
    if len(x) == 0:
        continue
    cells = []
    for m in [x.fam == 'fade', (x.fam == 'level') & x.with_aggr, (x.fam == 'level') & ~x.with_aggr]:
        cells.append(f'{ft(x[m], "r300")} ({int((m & ~x.fit).sum()):,})')
    print(f'| {gname} | ' + ' | '.join(cells) + ' |')
print()
print('## By year (PF raw at 300 s unstopped, N 60)\n')
print('| year | FADE | LEVEL with | LEVEL against |\n|---|---|---|---|')
x0 = d[d.n_win == 60]
yr = pd.to_datetime(x0.td).dt.year
for y in sorted(yr.unique()):
    x = x0[yr == y]
    print(f'| {y} | {f(pf(x.r300[x.fam == "fade"]))} | {f(pf(x.r300[(x.fam == "level") & x.with_aggr]))} | '
          f'{f(pf(x.r300[(x.fam == "level") & ~x.with_aggr]))} |')
print()
log('done')
