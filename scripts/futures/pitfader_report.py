"""The report over a PitFader trips parquet (TradingEdge.PitFader; user 2026-11-01: the basic 20m-low / 5m-high mean
reversion on every root, broken down by the time the trade was taken).

Units: the contract's TICKS (per root, unit-free PFs) and bp of |entry price| (pooled across roots = equal notional per
trip). Net = minus 1 or 2 ticks a round trip (a taker pays ~½ tick a side against the mid; commissions are extra). The
entry time = the SIGNAL bar's clock, ET.

  cd research && python -u scripts/futures/pitfader_report.py --trips data/futures/pitfader/trips_mc0 > data/futures/pitfader/report_mc0.md 2> logs/pitfader_report.log; echo exit $?
"""
import argparse
import numpy as np
import pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--trips', default='data/futures/pitfader/trips_mc0', help='a trips parquet or a directory of parts')
a = ap.parse_args()
T = pd.read_parquet(a.trips, columns=['root', 'td', 'side', 'sig_t', 'entry_t', 'exit_t', 'entry_px', 'exit_px', 'eod', 'tick', 'open_at_sig'])
T['ticks'] = T.side * (T.exit_px - T.entry_px) / T.tick
T['bp'] = 1e4 * T.side * (T.exit_px - T.entry_px) / T.entry_px.abs()
T['tick_bp'] = 1e4 * T.tick / T.entry_px.abs()
T['hour'] = (T.sig_t % 86400) // 3600
T['hold_m'] = (T.exit_t - T.entry_t) / 60
T['yr'] = pd.to_datetime(T.td).dt.year
T = T[np.isfinite(T.bp) & (T.entry_px.abs() > 0)]


def pf(r):
    r = np.asarray(r); g, l = r[r > 0].sum(), -r[r < 0].sum()
    return np.inf if l == 0 else g / l


def f(x, d=2): return 'inf' if np.isinf(x) else f'{x:.{d}f}'


SESS = T.groupby(['root', 'td']).ngroups
print(f'# PitFader — the basic mean reversion (20m low → 5m high, and the mirror), every root — mc = {0 if T.open_at_sig.max() > 0 else 1}\n')
print(f'{len(T):,} trips over {SESS:,} root-sessions, {T.td.min()}..{T.td.max()}. Fill = the next 1s bar\'s vwap; '
      'out at the session\'s last bar if still open (eod). '
      'Ticks: gross and net of 1 / 2 ticks a round trip.\n')

print('## 1. Per root\n')
print('| root | trips | per session | win % | mean ticks | PF gross | PF −1 tick | PF −2 ticks | long / short PF gross | '
      'mean bp | 1 tick = bp | hold min (median) | eod % | from |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
for r, g in T.groupby('root'):
    k = g.ticks.values
    print(f'| {r} | {len(g):,} | {len(g) / g.td.nunique():.1f} | {100 * (k > 0).mean():.0f} | {k.mean():+.2f} | **{f(pf(k))}** | '
          f'{f(pf(k - 1))} | {f(pf(k - 2))} | {f(pf(k[g.side.values > 0]))} / {f(pf(k[g.side.values < 0]))} | '
          f'{g.bp.mean():+.2f} | {g.tick_bp.median():.2f} | {g.hold_m.median():.1f} | {100 * g.eod.mean():.1f} | {g.td.min()} |')


def block(name, key, labels=None):
    print(f'\n## {name}\n')
    print('| ' + name.split(' ', 1)[1] + ' | trips | win % | mean ticks | PF ticks gross (median root) | pooled bp: PF gross | '
          'PF −1 tick | mean bp | mean bp −1 tick |\n|---|---|---|---|---|---|---|---|---|')
    for kv, g in T.groupby(key, observed=True):
        roots = g.groupby('root').ticks.apply(pf)
        print(f'| {labels(kv) if labels else kv} | {len(g):,} | {100 * (g.ticks > 0).mean():.0f} | {g.ticks.mean():+.2f} | '
              f'{f(roots.median())} | **{f(pf(g.bp))}** | {f(pf(g.bp - g.tick_bp))} | {g.bp.mean():+.2f} | '
              f'{(g.bp - g.tick_bp).mean():+.2f} |')


block('2. side', 'side', lambda s: 'long' if s > 0 else 'short')
block('3. entry hour (ET)', 'hour', lambda h: f'{h:02d}:00')
block('4. year', 'yr')
if T.open_at_sig.max() > 0:      # mc = 0: the averaging-down depth (trips of the side already open at the signal)
    T['depth'] = pd.cut(T.open_at_sig, [-1, 0, 5, 25, 50, 100, 10**6], labels=['0 (first)', '1–5', '6–25', '26–50', '51–100', '> 100'])
    block('4b. depth (trips already open at the signal)', 'depth')

print('\n## 5. Root × entry time (ET, the signal) — PF in ticks, gross (trips)\n')
B = [(34200, 37800, '09:30–10:30'), (37800, 41400, '10:30–11:30'), (41400, 45000, '11:30–12:30'), (45000, 48600, '12:30–13:30'),
     (48600, 52200, '13:30–14:30'), (52200, 86400, '14:30–15:00')]
print('| root | ' + ' | '.join(b[2] for b in B) + ' |\n|---|' + '---|' * len(B))
for r, g in T.groupby('root'):
    cells = []
    for lo, hi, _ in B:
        x = g[(g.sig_t >= lo) & (g.sig_t < hi)].ticks.values
        cells.append(f'{f(pf(x))} ({len(x):,})' if len(x) >= 30 else '·')
    print(f'| {r} | ' + ' | '.join(cells) + ' |')
