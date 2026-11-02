"""Breakout — the first report on a TradingEdge.Breakout run (user 2026-11-02: "I am really curious if a system like
this could work").

Returns are RAW (entry and exits at present-bar vwaps); "−1t" subtracts one tick per round trip (the PitFader stress
line). STOPPED = the window's other end: the stop's fill replaces horizon H's exit when stop_sig_t < exit_t_H.
FIT = 2015–2022, HOLDOUT = 2023–2026. Every trip of a family is one consolidation (one per side for brk); the families
overlap (one consolidation shows up under several σ / floors).

  cd research && python -u scripts/futures/breakout_report.py > data/futures/breakout/report.md 2> logs/breakout_report.log; echo exit $?
"""
import argparse
import sys
import time

import duckdb

ap = argparse.ArgumentParser()
ap.add_argument('--run', default='data/futures/breakout/run')
a = ap.parse_args()
T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

GROUPS = {'equity': ['ES', 'MES', 'NQ'], 'bonds': ['ZB', 'ZN', 'ZT'], 'sofr': ['SR3'],
          'fx': ['6A', '6B', '6C', '6E', '6J', '6N', '6S', '6Z', 'M6A', 'M6E', 'MJY', 'E7'],
          'energy': ['CL', 'MCL', 'NG', 'RB'], 'metals': ['HG', 'MGC'], 'ags': ['ZC', 'ZS', 'HE'], 'vx': ['VX']}
H = [60, 120, 300, 600]
con = duckdb.connect()
con.execute("set memory_limit = '4GB'; set threads = 4; set preserve_insertion_order = false")
gcase = 'CASE ' + ' '.join(f"WHEN root IN ({','.join(repr(r) for r in rs)}) THEN '{g}'" for g, rs in GROUPS.items()) + " ELSE 'other' END"
ret = lambda px: f'1e4 * side * ({px} - entry_px) / abs(entry_px)'
cols = ', '.join(
    [f"{ret(f'exit_px_{h}')} r{h}, {ret(f'CASE WHEN stop_sig_t < exit_t_{h} THEN stop_fill_px ELSE exit_px_{h} END')} s{h}, "
     f"stop_sig_t < exit_t_{h} hit{h}" for h in H])
con.execute(f"""CREATE VIEW t AS SELECT trig, thr, floor, side, root, {gcase} grp, year(td) yr, year(td) <= 2022 fit,
                1e4 * tick / abs(entry_px) tbp, side * (exit_px_60 - entry_px) / tick tk60, win_bars, {cols}
                FROM read_parquet('{a.run}/trips/*.parquet') WHERE entry_px <> 0""")


def pf(col, flt='true'):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x, d=2):
    return '—' if x is None else ('inf' if x == float('inf') else f'{x:.{d}f}')


log('views ready')
print('# Breakout — the first look (60 s exit, unstopped vs stopped at the window\'s other end)\n')
print('Cells: PF raw **fit → holdout**; "−1t" = holdout net of one tick; stop % = holdout trips stopped before 60 s.\n')
for trig in ['brk', 'arm']:
    q = con.execute(f"""SELECT thr, floor, count(*) FILTER (fit) nf, count(*) FILTER (NOT fit) nh,
                {pf('r60', 'fit')}, {pf('r60', 'NOT fit')}, {pf('r60 - tbp', 'NOT fit')},
                {pf('s60', 'fit')}, {pf('s60', 'NOT fit')}, {pf('s60 - tbp', 'NOT fit')},
                avg(tk60) FILTER (NOT fit), avg(hit60::INT) FILTER (NOT fit), median(win_bars) FILTER (NOT fit)
                FROM t WHERE trig = '{trig}' GROUP BY ALL ORDER BY thr, floor""").fetchall()
    log(trig, 'overview')
    print(f'## {trig.upper()} — every root pooled\n')
    print('| σ ticks | floor min | trips fit / holdout | unstopped PF | −1t | stopped PF | −1t | holdout mean ticks | stop % | median window bars |')
    print('|---|---|---|---|---|---|---|---|---|---|')
    for r in q:
        print(f'| {r[0]:g} | {r[1]:g} | {r[2]:,} / {r[3]:,} | {f(r[4])} → **{f(r[5])}** | {f(r[6])} | {f(r[7])} → **{f(r[8])}** | {f(r[9])} | '
              f'{f(r[10], 2)} | {100 * r[11]:.0f} | {r[12]:.0f} |')
    print()
    q = con.execute(f"""SELECT thr, {', '.join(f"{pf(f'r{h}', 'NOT fit')}, {pf(f's{h}', 'NOT fit')}" for h in H)}
                FROM t WHERE trig = '{trig}' AND floor = 10 GROUP BY ALL ORDER BY thr""").fetchall()
    print(f'### {trig.upper()} — the horizon, floor 10 min (holdout PF raw: unstopped / stopped)\n')
    print('| σ ticks | ' + ' | '.join(f'{h} s' for h in H) + ' |\n|---|' + '---|' * len(H))
    for r in q:
        print(f'| {r[0]:g} | ' + ' | '.join(f'{f(r[1 + 2 * k])} / {f(r[2 + 2 * k])}' for k in range(len(H))) + ' |')
    print()
    q = con.execute(f"""SELECT grp, thr, count(*) FILTER (NOT fit), {pf('r60', 'fit')}, {pf('r60', 'NOT fit')},
                {pf('s60', 'fit')}, {pf('s60', 'NOT fit')}, {pf('s300', 'fit')}, {pf('s300', 'NOT fit')}
                FROM t WHERE trig = '{trig}' AND floor = 10 GROUP BY ALL ORDER BY grp, thr""").fetchall()
    print(f'### {trig.upper()} — by group, floor 10 min (PF raw fit → holdout)\n')
    print('| group | σ ticks | holdout trips | 60 s unstopped | 60 s stopped | 300 s stopped |\n|---|---|---|---|---|---|')
    for r in q:
        print(f'| {r[0]} | {r[1]:g} | {r[2]:,} | {f(r[3])} → **{f(r[4])}** | {f(r[5])} → **{f(r[6])}** | {f(r[7])} → **{f(r[8])}** |')
    print()
    log(trig, 'done')
