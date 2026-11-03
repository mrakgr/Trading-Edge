"""LV-1 — the key-level trend's first report (user 2026-11-03; TradingEdge.Breakout/LevelEngine.fs, --window level).

A TRIP = one position, entry to its exit (a reversal, the cutoff's close, or eod); stop-and-reverse, so the trips of a
session tile it after the first signal. Return = side × (exit − entry) in bp of the entry (raw: present-bar vwap fills);
"−1t" subtracts one tick per trip (each trip is one round trip: a reversal closes one and opens the next).
FIT = 2015–2022, HOLDOUT = 2023–2026 (the breakout reports' split). The marks: the same entries held a FIXED time instead.

  cd research && python -u scripts/futures/levels_report.py > data/futures/levels/report_lv1.md 2> logs/levels_report.log; echo exit $?
"""
import argparse
import sys
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument('--run', default='data/futures/levels/run_lv1')
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

GROUPS = {'equity': ['ES', 'MES', 'NQ'], 'bonds': ['ZB', 'ZN', 'ZT'], 'sofr': ['SR3'],
          'fx': ['6A', '6B', '6C', '6E', '6J', '6N', '6S', '6Z', 'M6A', 'M6E', 'MJY', 'M6J', 'E7'],
          'energy': ['CL', 'MCL', 'NG', 'RB'], 'metals': ['HG', 'MGC'], 'ags': ['ZC', 'ZS', 'HE'], 'vx': ['VX']}
MARKS = [5, 10, 30, 60, 120, 300, 600, 1200, 1800]
con = duckdb.connect()
con.execute("set memory_limit = '3GB'; set threads = 4; set preserve_insertion_order = false")
gcase = 'CASE ' + ' '.join(f"WHEN root IN ({','.join(repr(r) for r in rs)}) THEN '{g}'" for g, rs in GROUPS.items()) + " ELSE 'other' END"
marks = ', '.join(f'1e4 * side * (px_{m} - entry_px) / abs(entry_px) r_{m}' for m in MARKS)
con.execute(f"""CREATE TEMP TABLE t AS
    SELECT root, {gcase} grp, td, year(td) yr, year(td) <= 2022 fit, side, reason, exit_t - entry_t hold_s,
           1e4 * side * (exit_px - entry_px) / abs(entry_px) r,
           1e4 * side * (exit_px - entry_px) / abs(entry_px) - 1e4 * tick / abs(entry_px) rn,
           side * (exit_px - entry_px) / tick tk,
           abs(mean - level) / nullif(sigma, 0) z, sigma / tick sig_tk, abs(mean - level) / tick dist_tk,
           sig_t - level_t level_age, level_vol, vol_now, {marks}
    FROM read_parquet('{a.run}/legs/*.parquet') WHERE entry_px <> 0""")
NF = con.execute("SELECT count(DISTINCT td) FILTER (fit), count(DISTINCT td) FILTER (NOT fit), count(*) FROM t").fetchone()
log('trips', NF[2])


def pf(col, flt):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x): return '—' if x is None else ('inf' if x == float('inf') else f'{x:.2f}')


print('# LV-1 — the key-level trend, first look\n')
print('A trip = one position (stop-and-reverse). Cells: PF raw **fit 2015–22 → holdout 2023–26**; "−1t" = holdout net of '
      'one tick per trip. Fills at the next present 1s bar\'s vwap.\n')
print('## By group\n')
print('| group | trips/day (holdout) | PF raw | −1t | holdout mean bp | mean ticks | median hold min | % reversals |\n|---|---|---|---|---|---|---|---|')
for r in con.execute(f"""SELECT grp, count(*) FILTER (NOT fit) / count(DISTINCT td) FILTER (NOT fit), {pf('r', 'fit')}, {pf('r', 'NOT fit')},
            {pf('rn', 'NOT fit')}, avg(r) FILTER (NOT fit), avg(tk) FILTER (NOT fit), median(hold_s) FILTER (NOT fit) / 60.0,
            100 * avg((reason = 'flip')::INT) FILTER (NOT fit) FROM t GROUP BY 1 ORDER BY 1""").fetchall():
    print(f'| {r[0]} | {r[1] or 0:.1f} | {f(r[2])} → **{f(r[3])}** | {f(r[4])} | {r[5] or 0:+.2f} | {r[6] or 0:+.2f} | {r[7] or 0:.1f} | {r[8] or 0:.0f} |')
print()
log('groups')
print('## By root\n')
print('| root | holdout trips | PF raw | −1t | holdout mean ticks | median σ ticks |\n|---|---|---|---|---|---|')
for r in con.execute(f"""SELECT root, count(*) FILTER (NOT fit), {pf('r', 'fit')}, {pf('r', 'NOT fit')}, {pf('rn', 'NOT fit')},
            avg(tk) FILTER (NOT fit), median(sig_tk) FILTER (NOT fit) FROM t GROUP BY 1 ORDER BY 1""").fetchall():
    print(f'| {r[0]} | {r[1]:,} | {f(r[2])} → **{f(r[3])}** | {f(r[4])} | {r[5] or 0:+.2f} | {r[6] or 0:.2f} |')
print()
print('## The same entries held a FIXED time (the marks; PF raw fit → holdout, all groups)\n')
print('| hold | ' + ' | '.join(f'{m} s' for m in MARKS) + ' | the system exit |\n|---|' + '---|' * (len(MARKS) + 1))
cells = ', '.join(f"{pf(f'r_{m}', 'fit')}, {pf(f'r_{m}', 'NOT fit')}" for m in MARKS)
r = con.execute(f"SELECT {cells}, {pf('r', 'fit')}, {pf('r', 'NOT fit')} FROM t").fetchone()
print('| PF | ' + ' | '.join(f'{f(r[2 * k])} → **{f(r[2 * k + 1])}**' for k in range(len(MARKS) + 1)) + ' |')
print()
log('marks')
print('## By the distance at the signal (|mean − level| ÷ σ) and the level\'s age (holdout PF raw; trips)\n')
for col, edges, lab in [('z', [2, 2.5, 3, 4, 6, 1e9], 'z'), ('level_age', [0, 30, 120, 300, 900, 1800, 1e9], 'level age s'),
                        ('sig_tk', [0, 0.5, 1, 2, 4, 8, 1e9], 'σ ticks')]:
    print(f'| {lab} | PF raw fit → holdout | holdout trips | holdout mean ticks |\n|---|---|---|---|')
    for lo, hi in zip(edges[:-1], edges[1:]):
        flt = f'{col} >= {lo} AND {col} < {hi}'
        q = con.execute(f"SELECT {pf('r', f'fit AND {flt}')}, {pf('r', f'NOT fit AND {flt}')}, count(*) FILTER (NOT fit AND {flt}), "
                        f"avg(tk) FILTER (NOT fit AND {flt}) FROM t").fetchone()
        print(f"| [{lo:g}, {'∞' if hi >= 1e9 else f'{hi:g}'}) | {f(q[0])} → **{f(q[1])}** | {q[2]:,} | {q[3] or 0:+.2f} |")
    print()
log('buckets')
print('## By year (PF raw; trips)\n')
print('| year | ' + ' | '.join(GROUPS) + ' |\n|---|' + '---|' * len(GROUPS))
cols = ', '.join(f"{pf('r', f'grp = {g!r}')}, count(*) FILTER (grp = {g!r})" for g in GROUPS)
for r in con.execute(f'SELECT yr, {cols} FROM t GROUP BY yr ORDER BY yr').fetchall():
    print(f'| {r[0]} | ' + ' | '.join(f'{f(r[1 + 2 * j])} ({r[2 + 2 * j]:,})' for j in range(len(GROUPS))) + ' |')
print()
log('done')
