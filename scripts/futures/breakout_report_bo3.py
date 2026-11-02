"""Breakout BO-3 — the FIXED-VOLUME window's report (user 2026-11-02: "we should instead be using total volume as the
threshold ... and then calculate the sigma of that volume"). The system is BO-2's stop-and-reverse; the family = V
(vol_min, minutes of the root's average volume); TIGHTNESS = cons_ratio (σ ÷ the root's reference σ at that V, ≤ 1.0 by
the engine's pre-filter).

THE EPISODE is the trade: every leg from one start, summed — Σ legs of side × (exit − entry) / entry in bp (raw: present-bar
vwap fills). "−1t" subtracts one tick per LEG (each leg is a round trip: a flip closes one and opens the next).
FIT = 2015–2022, HOLDOUT = 2023–2026. The families overlap (one consolidation can start an episode under several σ /
floors).

  cd research && python -u scripts/futures/breakout_report_bo3.py > data/futures/breakout/report_bo3.md 2> logs/breakout_report_bo3.log; echo exit $?
"""
import argparse
import sys
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument('--run', default='data/futures/breakout/run_bo3')
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
    SELECT root, any_value({gcase}) grp, td, year(td) <= 2022 fit, vol_min, ep_t, any_value(cons_ratio) ratio,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px)) r,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px) - 1e4 * tick / abs(entry_px)) rn,
           sum(side * (exit_px - entry_px) / tick) tk,
           count(*) legs, count(released_t) > 0 rel, max(exit_t) - min(entry_t) hold_s,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px)) FILTER (reason = 'flip') r_flip,
           sum(1e4 * side * (exit_px - entry_px) / abs(entry_px)) FILTER (reason <> 'flip') r_last,
           any_value(reason) FILTER (reason <> 'flip') last_reason
    FROM read_parquet('{a.run}/legs/*.parquet') WHERE entry_px <> 0 GROUP BY root, td, vol_min, ep_t""")
log('episodes built', con.execute('SELECT count(*) FROM ep').fetchone()[0])


def pf(col, flt='true'):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x, d=2):
    return '—' if x is None else ('inf' if x == float('inf') else f'{x:.{d}f}')


RB = [(0, 0.5), (0.5, 0.7), (0.7, 0.85), (0.85, 1.0001)]
print('# Breakout BO-3 — the fixed-volume window (V = minutes of average volume), BO-2\'s stop-and-reverse\n')
print('Cells: episode PF raw **fit → holdout** (an episode = all its legs summed); "−1t" = holdout net of one tick per '
      'leg. ratio = the consolidation\'s σ ÷ the root\'s reference σ at that V (< 1 = tighter than usual).\n')
print('## Every root pooled (ratio ≤ 1)\n')
print('| V min | episodes fit / holdout | legs / ep | released % | PF raw | −1t | holdout mean ticks / ep | median hold min |')
print('|---|---|---|---|---|---|---|---|')
for r in con.execute(f"""SELECT vol_min, count(*) FILTER (fit), count(*) FILTER (NOT fit), avg(legs) FILTER (NOT fit),
            avg(rel::INT) FILTER (NOT fit), {pf('r', 'fit')}, {pf('r', 'NOT fit')}, {pf('rn', 'NOT fit')},
            avg(tk) FILTER (NOT fit), median(hold_s) FILTER (NOT fit) / 60.0 FROM ep GROUP BY ALL ORDER BY vol_min""").fetchall():
    print(f'| {r[0]:g} | {r[1]:,} / {r[2]:,} | {r[3]:.2f} | {100 * r[4]:.0f} | {f(r[5])} → **{f(r[6])}** | {f(r[7])} | '
          f'{r[8]:+.2f} | {r[9]:.1f} |')
print()
log('pooled')

print('## The tightness ladder (PF raw fit → holdout; holdout episodes; −1t holdout)\n')
print('| V min | ' + ' | '.join(f'ratio {lo:g}–{min(hi, 1):g}' for lo, hi in RB) + ' |\n|---|' + '---|' * len(RB))
for vm in [x[0] for x in con.execute('SELECT DISTINCT vol_min FROM ep ORDER BY 1').fetchall()]:
    cells = []
    for lo, hi in RB:
        r = con.execute(f"""SELECT {pf('r', 'fit')}, {pf('r', 'NOT fit')}, count(*) FILTER (NOT fit), {pf('rn', 'NOT fit')}
                            FROM ep WHERE vol_min = {vm} AND ratio >= {lo} AND ratio < {hi}""").fetchone()
        cells.append(f'{f(r[0])} → **{f(r[1])}** ({r[2]:,}; {f(r[3])})')
    print(f'| {vm:g} | ' + ' | '.join(cells) + ' |')
print()

print('## Where the P&L comes from (holdout): the flip legs vs the last leg\n')
print('Mean bp per episode. The last leg is the released, trailed leg (trail) or the one open at the close (eod). ⚠ Not a '
      'gate: whether an episode flips is known only afterwards.\n')
print('| V min | episodes | flip legs Σ mean bp | last leg mean bp | last = trail % | episode mean bp | 0-flip share % |')
print('|---|---|---|---|---|---|---|')
for r in con.execute(f"""SELECT vol_min, count(*), avg(coalesce(r_flip, 0)), avg(r_last), avg((last_reason = 'trail')::INT), avg(r),
            avg((legs = 1)::INT) FROM ep WHERE NOT fit GROUP BY ALL ORDER BY vol_min""").fetchall():
    print(f'| {r[0]:g} | {r[1]:,} | {r[2]:+.2f} | {r[3]:+.2f} | {100 * r[4]:.0f} | {r[5]:+.2f} | {100 * r[6]:.0f} |')
print()
log('decomposition')

vms = [x[0] for x in con.execute('SELECT DISTINCT vol_min FROM ep ORDER BY 1').fetchall()]
for title, flt in [('ratio ≤ 1', 'true'), ('ratio < 0.7 (the tightest cells)', 'ratio < 0.7')]:
    print(f'## By group, {title} (episode PF raw fit → holdout; holdout episodes; −1t holdout)\n')
    print('| group | ' + ' | '.join(f'V {v:g} min' for v in vms) + ' |\n|---|' + '---|' * len(vms))
    q = con.execute(f"""SELECT grp, vol_min, {pf('r', 'fit')}, {pf('r', 'NOT fit')}, count(*) FILTER (NOT fit), {pf('rn', 'NOT fit')}
                        FROM ep WHERE {flt} GROUP BY ALL""").fetchall()
    cell = {(r[0], r[1]): r for r in q}
    for g in list(GROUPS) + ['other']:
        row = [('—' if (r := cell.get((g, v))) is None else f'{f(r[2])} → **{f(r[3])}** ({r[4]:,}; {f(r[5])})') for v in vms]
        print(f'| {g} | ' + ' | '.join(row) + ' |')
    print()

print('## By year (episode PF raw, ratio ≤ 1)\n')
print('| year | ' + ' | '.join(f'V {v:g}' for v in vms) + ' |\n|---|' + '---|' * len(vms))
q = con.execute(f"SELECT year(td) y, vol_min, {pf('r')}, count(*) FROM ep GROUP BY ALL").fetchall()
cell = {(r[0], r[1]): r for r in q}
for y in sorted({r[0] for r in q}):
    print(f'| {y} | ' + ' | '.join(f'{f(cell[(y, v)][2])} ({cell[(y, v)][3]:,})' if (y, v) in cell else '—' for v in vms) + ' |')
print()
log('done')
