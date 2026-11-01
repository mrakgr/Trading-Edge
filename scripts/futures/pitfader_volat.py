"""PitFader PIT-3 — the VOLATILITY FLOOR per instrument (user 2026-11-01: "For each instrument we should probably find a
volatility floor where the expectancy of a trade starts to improve. Every system will use the same features, but we'll
find that different instruments enjoy different gate configurations of such features").

The book: the mc = 0 sampler (data/futures/pitfader/trips_mc0, TradingEdge.PitFader; the stage-1 features sealed against
FlushFader / SpikeFader). The feature: volat_20m = the EWMA (half-life 40 slots) of |ln| slot-vwap returns, a slot = 30
present bars, read at the signal bar — in bp per slot. Two scales:
  per ROOT, the root's own percentiles of volat_20m (instruments differ by 10×+ in bp);
  pooled, volatility in TICKS: vt = volat_20m × |entry price| / tick — the typical 30-bar move in the cost unit.
Net = minus 1 tick a round trip (the taker's ½ tick a side). ⚠ mc = 0 trips are correlated (a long run of fresh extremes
gives many trips that exit together); the depth column says how deep the floor's trips sit.

  cd research && python -u scripts/futures/pitfader_volat.py > data/futures/pitfader/volat.md 2> logs/pitfader_volat.log; echo exit $?
"""
import numpy as np
import pandas as pd

T = pd.read_parquet('data/futures/pitfader/trips_mc0',
                    columns=['root', 'td', 'side', 'entry_px', 'exit_px', 'tick', 'open_at_sig', 'volat_20m'])
T = T[T.volat_20m.notna() & (T.entry_px.abs() > 0)]
T['ticks'] = T.side * (T.exit_px - T.entry_px) / T.tick
T['net'] = T.ticks - 1
T['v_bp'] = 1e4 * T.volat_20m
T['vt'] = T.volat_20m * T.entry_px.abs() / T.tick
T['yr'] = pd.to_datetime(T.td).dt.year


def pf(r):
    r = np.asarray(r); g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l


def f(x): return 'inf' if np.isinf(x) else f'{x:.2f}'


def yrs_lt1(g):
    y = g.groupby('yr').net.apply(pf)
    return f'{int((y < 1).sum())}/{len(y)}'


QS = [0.0, 0.5, 0.7, 0.8, 0.9, 0.95, 0.98]
print('# PIT-3 — the volatility floor per instrument (volat_20m, mc = 0)\n')
print(f'{len(T):,} trips with volat_20m. Each cell: PF −1 tick (mean net ticks) for the trips with volat_20m at or above '
      'the root\'s own percentile; the bp threshold is in the second table. "first > 1" = the lowest floor of the ladder '
      'whose net PF clears 1.\n')
print('## 1. Per root: PF net of 1 tick (mean net ticks) at volat_20m ≥ the root\'s percentile\n')
print('| root | trips | ' + ' | '.join(f'≥ p{int(100 * q)}' for q in QS) + ' | first > 1 | years < 1 there | depth (median open) there |')
print('|---|---|' + '---|' * (len(QS) + 3))
TH = {}
for r, g in T.groupby('root'):
    if len(g) < 2000:
        continue
    th = g.v_bp.quantile(QS).values; TH[r] = th
    cells, first = [], None
    for q, t in zip(QS, th):
        s = g[g.v_bp >= t]
        p = pf(s.net)
        cells.append(f'{f(p)} ({s.net.mean():+.2f})')
        if first is None and p > 1 and len(s) >= 500:
            first = (q, t, s)
    fq = f'p{int(100 * first[0])} = {first[1]:.1f} bp ({len(first[2]):,})' if first else '—'
    print(f'| {r} | {len(g):,} | ' + ' | '.join(cells) + f' | {fq} | {yrs_lt1(first[2]) if first else "—"} | '
          f'{int(first[2].open_at_sig.median()) if first else "—"} |')

print('\n## 2. The thresholds: volat_20m in bp at each percentile\n')
print('| root | ' + ' | '.join(f'p{int(100 * q)}' for q in QS) + ' | median in ticks |\n|---|' + '---|' * (len(QS) + 1))
for r, th in TH.items():
    print(f'| {r} | ' + ' | '.join(f'{t:.1f}' for t in th) + f' | {T[T.root == r].vt.median():.2f} |')

print('\n## 3. Pooled: volatility in TICKS (volat_20m × price / tick)\n')
print('| vt band | trips | roots | win % | mean ticks | PF gross | PF −1 tick | mean net ticks | years < 1 (net) |\n|---|---|---|---|---|---|---|---|---|')
for lo, hi in ((0, 0.5), (0.5, 1), (1, 1.5), (1.5, 2), (2, 3), (3, 5), (5, 8), (8, 15), (15, 1e9)):
    s = T[(T.vt >= lo) & (T.vt < hi)]
    if len(s) < 200:
        continue
    lab = f'{lo:g}–{hi:g}' if hi < 1e8 else f'≥ {lo:g}'
    print(f'| {lab} | {len(s):,} | {s.root.nunique()} | {100 * (s.ticks > 0).mean():.0f} | {s.ticks.mean():+.2f} | '
          f'**{f(pf(s.ticks))}** | {f(pf(s.net))} | {s.net.mean():+.2f} | {yrs_lt1(s)} |')

print('\n## 4. Pooled, per-root deciles of volat_20m (each root contributes equally to every decile)\n')
T['dec'] = T.groupby('root').v_bp.transform(lambda x: np.ceil(10 * x.rank(pct=True)).clip(1, 10)).astype(int)
print('| decile (within root) | trips | win % | mean ticks | PF gross | PF −1 tick | mean net ticks | median vt |\n|---|---|---|---|---|---|---|---|')
for d, s in T.groupby('dec'):
    print(f'| D{d} | {len(s):,} | {100 * (s.ticks > 0).mean():.0f} | {s.ticks.mean():+.2f} | **{f(pf(s.ticks))}** | '
          f'{f(pf(s.net))} | {s.net.mean():+.2f} | {s.vt.median():.2f} |')

print('\n## 5. Long vs short at the top decile (D10 within root), per root: PF −1 tick (trips)\n')
print('| root | long | short |\n|---|---|---|')
for r, g in T[T.dec == 10].groupby('root'):
    if len(g) < 300:
        continue
    L, S_ = g[g.side > 0], g[g.side < 0]
    print(f'| {r} | {f(pf(L.net))} ({len(L):,}) | {f(pf(S_.net))} ({len(S_):,}) |')

print('\n## 6. The p90 / p95 floors year by year: net PF per year (trips), per root\n')
YRS = sorted(T.yr.unique())
for qq in (0.90, 0.95):
    print(f'### volat_20m ≥ the root\'s p{int(100 * qq)}\n')
    print('| root | ' + ' | '.join(str(y) for y in YRS) + ' | years < 1 |\n|---|' + '---|' * (len(YRS) + 1))
    for r, g in T.groupby('root'):
        if len(g) < 2000:
            continue
        s = g[g.v_bp >= g.v_bp.quantile(qq)]
        y = s.groupby('yr').net.agg([pf, 'size'])
        cells = [f'{f(y.loc[yy, "pf"])} ({int(y.loc[yy, "size"])})' if yy in y.index and y.loc[yy, 'size'] >= 50 else '·' for yy in YRS]
        ok = y[y['size'] >= 50]
        print(f'| {r} | ' + ' | '.join(cells) + f' | {int((ok.pf < 1).sum())}/{len(ok)} |')
    print()

print('## 7. Volatility × depth — pooled net PF (trips): does volatility subsume the depth ladder?\n')
T['depth'] = pd.cut(T.open_at_sig, [-1, 0, 5, 25, 50, 100, 10**6], labels=['0', '1–5', '6–25', '26–50', '51–100', '> 100'])
T['vgrp'] = pd.cut(T.dec, [0, 5, 8, 9, 10], labels=['D1–5', 'D6–8', 'D9', 'D10'])
tab = T.groupby(['vgrp', 'depth'], observed=True).net.agg([pf, 'size'])
print('| volat (within-root decile) | ' + ' | '.join(['0', '1–5', '6–25', '26–50', '51–100', '> 100']) + ' |\n|---|' + '---|' * 6)
for vg in ['D1–5', 'D6–8', 'D9', 'D10']:
    cells = [f'{f(tab.loc[(vg, d), "pf"])} ({int(tab.loc[(vg, d), "size"]):,})' if (vg, d) in tab.index else '·'
             for d in ['0', '1–5', '6–25', '26–50', '51–100', '> 100']]
    print(f'| {vg} | ' + ' | '.join(cells) + ' |')
