"""Breakout BO-2 — the stop-and-reverse system's report (user 2026-11-02: "Maybe holding the trade for just 1m and trading
only once per consolidation is no better than random").

THE EPISODE is the trade: every leg from one start, summed — Σ legs of side × (exit − entry) / entry in bp (raw: present-bar
vwap fills). "−1t" subtracts one tick per LEG (each leg is a round trip: a flip closes one and opens the next).
FIT = 2015–2022, HOLDOUT = 2023–2026. The families overlap (one consolidation can start an episode under several σ /
floors).

  cd research && python -u scripts/futures/breakout_report_bo2.py > data/futures/breakout/report_bo2.md 2> logs/breakout_report_bo2.log; echo exit $?
"""
import argparse
import sys
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument('--run', default='data/futures/breakout/run_bo2')
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

GROUPS = {'equity': ['ES', 'MES', 'NQ'], 'bonds': ['ZB', 'ZN', 'ZT'], 'sofr': ['SR3'],
          'fx': ['6A', '6B', '6C', '6E', '6J', '6N', '6S', '6Z', 'M6A', 'M6E', 'MJY', 'M6J', 'E7'],
          'energy': ['CL', 'MCL', 'NG', 'RB'], 'metals': ['HG', 'MGC'], 'ags': ['ZC', 'ZS', 'HE'], 'vx': ['VX']}
con = duckdb.connect()
con.execute("set memory_limit = '4GB'; set threads = 4; set preserve_insertion_order = false")
gcase = 'CASE ' + ' '.join(f"WHEN root IN ({','.join(repr(r) for r in rs)}) THEN '{g}'" for g, rs in GROUPS.items()) + " ELSE 'other' END"
con.execute(f"""CREATE TEMP TABLE ep AS
    SELECT root, any_value({gcase}) grp, td, year(td) <= 2022 fit, thr, floor, ep_t,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px)) r,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px) - 1e4 * tick / abs(entry_px)) rn,
           sum(side * (exit_px - entry_px) / tick) tk,
           count(*) legs, count(released_t) > 0 rel, max(exit_t) - min(entry_t) hold_s,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px)) FILTER (reason = 'flip') r_flip,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px)) FILTER (reason <> 'flip') r_last,
           any_value(reason) FILTER (reason <> 'flip') last_reason
    FROM read_parquet('{a.run}/legs/*.parquet') WHERE entry_px <> 0 GROUP BY root, td, thr, floor, ep_t""")
log('episodes built', con.execute('SELECT count(*) FROM ep').fetchone()[0])


def pf(col, flt='true'):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x, d=2):
    return '—' if x is None else ('inf' if x == float('inf') else f'{x:.{d}f}')


print('# Breakout BO-2 — the stop-and-reverse system (flip at the consolidation\'s other end until the release, then '
      'trail the 1m low / high)\n')
print('Cells: episode PF raw **fit → holdout** (an episode = all its legs summed); "−1t" = holdout net of one tick per '
      'leg.\n')
print('## Every root pooled\n')
print('| σ ticks | floor min | episodes fit / holdout | legs / ep | released % | PF raw | −1t | holdout mean ticks / ep | median hold min |')
print('|---|---|---|---|---|---|---|---|---|')
for r in con.execute(f"""SELECT thr, floor, count(*) FILTER (fit), count(*) FILTER (NOT fit), avg(legs) FILTER (NOT fit),
            avg(rel::INT) FILTER (NOT fit), {pf('r', 'fit')}, {pf('r', 'NOT fit')}, {pf('rn', 'NOT fit')},
            avg(tk) FILTER (NOT fit), median(hold_s) FILTER (NOT fit) / 60.0 FROM ep GROUP BY ALL ORDER BY thr, floor""").fetchall():
    print(f'| {r[0]:g} | {r[1]:g} | {r[2]:,} / {r[3]:,} | {r[4]:.2f} | {100 * r[5]:.0f} | {f(r[6])} → **{f(r[7])}** | {f(r[8])} | '
          f'{r[9]:+.2f} | {r[10]:.1f} |')
print()
log('pooled')

print('## Where the P&L comes from (floor 10 min, holdout): the flip legs vs the last leg\n')
print('Mean bp per episode. The last leg is the released, trailed leg (trail) or the one open at the close (eod).\n')
print('| σ ticks | episodes | flip legs Σ mean bp | last leg mean bp | last = trail % | trail-exit last leg PF | eod last leg PF | episode mean bp |')
print('|---|---|---|---|---|---|---|---|')
for r in con.execute(f"""SELECT thr, count(*), avg(coalesce(r_flip, 0)), avg(r_last), avg((last_reason = 'trail')::INT),
            {pf('r_last', "last_reason = 'trail'")}, {pf('r_last', "last_reason = 'eod'")}, avg(r)
            FROM ep WHERE NOT fit AND floor = 10 GROUP BY ALL ORDER BY thr""").fetchall():
    print(f'| {r[0]:g} | {r[1]:,} | {r[2]:+.2f} | {r[3]:+.2f} | {100 * r[4]:.0f} | {f(r[5])} | {f(r[6])} | {r[7]:+.2f} |')
print()

print('## By the number of flips (floor 10 min; PF raw fit → holdout, holdout share of episodes)\n')
print('| σ ticks | 0 flips | 1 flip | 2 flips | 3+ flips |\n|---|---|---|---|---|')
for thr in con.execute('SELECT DISTINCT thr FROM ep ORDER BY thr').fetchall():
    cells = []
    tot = con.execute(f'SELECT count(*) FROM ep WHERE NOT fit AND floor = 10 AND thr = {thr[0]}').fetchone()[0]
    for lo, hi in [(1, 1), (2, 2), (3, 3), (4, 10 ** 6)]:
        r = con.execute(f"""SELECT {pf('r', 'fit')}, {pf('r', 'NOT fit')}, count(*) FILTER (NOT fit) FROM ep
                            WHERE floor = 10 AND thr = {thr[0]} AND legs BETWEEN {lo} AND {hi}""").fetchone()
        cells.append(f'{f(r[0])} → **{f(r[1])}** ({100 * r[2] / max(tot, 1):.0f} %)')
    print(f'| {thr[0]:g} | ' + ' | '.join(cells) + ' |')
print()
log('decomposition')

print('## By group (floor 10 min; episode PF raw fit → holdout; holdout episodes; −1t holdout)\n')
print('| group | σ 1 | σ 2 | σ 3 | σ 4 | σ 6 | σ 8 |\n|---|---|---|---|---|---|---|')
q = con.execute(f"""SELECT grp, thr, {pf('r', 'fit')}, {pf('r', 'NOT fit')}, count(*) FILTER (NOT fit), {pf('rn', 'NOT fit')}
                    FROM ep WHERE floor = 10 GROUP BY ALL""").fetchall()
cell = {(r[0], r[1]): r for r in q}
for g in list(GROUPS) + ['other']:
    row = []
    for thr in [1, 2, 3, 4, 6, 8]:
        r = cell.get((g, float(thr)))
        row.append('—' if r is None else f'{f(r[2])} → **{f(r[3])}** ({r[4]:,}; {f(r[5])})')
    print(f'| {g} | ' + ' | '.join(row) + ' |')
print()

print('## By year (floor 10 min, σ 3 and σ 8; episode PF raw)\n')
print('| year | σ 3 episodes | σ 3 PF | σ 8 episodes | σ 8 PF |\n|---|---|---|---|---|')
for r in con.execute(f"""SELECT year(td) y, count(*) FILTER (thr = 3), {pf('r', 'thr = 3')}, count(*) FILTER (thr = 8),
            {pf('r', 'thr = 8')} FROM ep WHERE floor = 10 GROUP BY ALL ORDER BY y""").fetchall():
    print(f'| {r[0]} | {r[1]:,} | {f(r[2])} | {r[3]:,} | {f(r[4])} |')
print()
log('done')
