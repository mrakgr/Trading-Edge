"""Breakout BO-7 — the 1m breakout's report (user 2026-11-02: "whether entering when the delta is lopsided, holding the trade
when it continues being strong, and exiting when it gets weak makes sense"; repeat entries, "filter them posthoc").

A trip = every bar that makes a new 60-bar high (long) / low (short); fill at the next bar's vwap; exit 60 s later
(px_60). Returns: raw bp = side × (px − entry) / entry; ticks = side × (px − entry) / tick. FIT = 2015–22, HOLDOUT =
2023–26. All SQL aggregates with FIXED buckets (no quantiles over the tens of millions of rows).
  pre_dimb_k = side × Σ delta ÷ Σ volume of the k bars up to the signal (> 0 = the aggressors WITH the break)
  pre_rv_k   = their volume ÷ (k × the root's mean volume per bar, previous 20 sessions)
  dimb_m     = the same from the entry bar to mark m (⚠ the mark bar's own delta and vwap are concurrent, so a decision
               at mark m is scored from the NEXT mark on: rest = side × (px_60 − px_next) / entry)

  cd research && python -u scripts/futures/breakout_report_bo7.py > data/futures/breakout/report_bo7.md 2> logs/breakout_report_bo7.log; echo exit $?
"""
import sys
import time

import duckdb

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

GROUPS = {'equity': ['ES', 'MES', 'NQ'], 'bonds': ['ZB', 'ZN', 'ZT'], 'sofr': ['SR3'],
          'fx': ['6A', '6B', '6C', '6E', '6J', '6N', '6S', '6Z', 'M6A', 'M6E', 'MJY', 'M6J', 'E7'],
          'energy': ['CL', 'MCL', 'NG', 'RB'], 'metals': ['HG', 'MGC'], 'ags': ['ZC', 'ZS', 'HE'], 'vx': ['VX']}
MARKS = [1, 2, 3, 5, 10, 15, 20, 30, 45, 60]
con = duckdb.connect()
con.execute("set memory_limit = '6GB'; set threads = 4; set preserve_insertion_order = false")
gcase = 'CASE ' + ' '.join(f"WHEN root IN ({','.join(repr(r) for r in rs)}) THEN '{g}'" for g, rs in GROUPS.items()) + " ELSE 'other' END"
E = 'abs(entry_px)'
con.execute(f"""CREATE VIEW t AS SELECT {gcase} grp, year(td) yr, year(td) <= 2022 fit, side,
    1e4 * side * (px_60 - entry_px) / {E} r, side * (px_60 - entry_px) / tick tk, 1e4 * tick / {E} tbp,
    {', '.join(f'side * (px_{m} - entry_px) / tick tk_{m}' for m in MARKS)},
    {', '.join(f'pre_dimb_{k}, pre_vol_{k} / ({k} * vref_bar) pre_rv_{k}' for k in [1, 5, 15, 30, 60])},
    {', '.join(f'dimb_{m}' for m in [5, 10, 20, 30])},
    {', '.join(f'1e4 * side * (px_60 - px_{nx}) / {E} rest_{m}' for m, nx in [(5, 10), (10, 15), (20, 30), (30, 45)])}
    FROM read_parquet('data/futures/breakout/run_bo7/legs/*.parquet') WHERE entry_px <> 0 AND NOT eod""")


def pf(col, flt='true'):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x, d=2): return '—' if x is None else f'{x:.{d}f}'


def ft(flt, col='r'):
    return f"{pf(col, f'fit AND ({flt})')}, {pf(col, f'NOT fit AND ({flt})')}, count(*) FILTER (NOT fit AND ({flt}))"


def cell(r, k=0): return f'{f(r[k])} → **{f(r[k + 1])}** ({r[k + 2]:,})'


DB = [('≤ −0.3', '{c} <= -0.3'), ('−0.3 … −0.1', '{c} > -0.3 AND {c} <= -0.1'), ('−0.1 … 0.1', '{c} > -0.1 AND {c} < 0.1'),
      ('0.1 … 0.3', '{c} >= 0.1 AND {c} < 0.3'), ('≥ 0.3', '{c} >= 0.3')]
VB = [('< 0.5', '{c} < 0.5'), ('0.5 … 1', '{c} >= 0.5 AND {c} < 1'), ('1 … 2', '{c} >= 1 AND {c} < 2'), ('2 … 4', '{c} >= 2 AND {c} < 4'),
      ('≥ 4', '{c} >= 4')]

print('# Breakout BO-7 — the 1m breakout: every new 60-bar high / low, out 60 s later\n')
print('Cells: PF raw **fit 2015–22 → holdout 2023–26** (holdout trips). "−1t" = one tick per round trip.\n')
print('## The baseline by group\n')
print('| group | trips fit / holdout | PF raw 60 s | −1t holdout | holdout mean ticks | ' + ' | '.join(f'{m} s' for m in MARKS) + ' |')
print('|---|---|---|---|---|' + '---|' * len(MARKS))
q = con.execute(f"""SELECT coalesce(grp, 'ALL'), count(*) FILTER (fit), count(*) FILTER (NOT fit), {pf('r', 'fit')}, {pf('r', 'NOT fit')},
            {pf('r - tbp', 'NOT fit')}, avg(tk) FILTER (NOT fit), {', '.join(f'avg(tk_{m}) FILTER (NOT fit)' for m in MARKS)}
            FROM t GROUP BY ROLLUP (grp) ORDER BY grp NULLS FIRST""").fetchall()
for r in q:
    print(f'| {r[0]} | {r[1]:,} / {r[2]:,} | {f(r[3])} → **{f(r[4])}** | {f(r[5])} | {r[6]:+.3f} | ' + ' | '.join(f'{x:+.3f}' for x in r[7:]) + ' |')
print('\n(The mark columns: the holdout\'s mean ticks at each mark — the average path of a 1m breakout.)\n')
log('baseline')

print('## 1. ENTERING: the delta and the volume BEFORE the entry → the 60 s result\n')
print('| pre-entry window | ' + ' | '.join(f'dimb {b[0]}' for b in DB) + ' |\n|---|' + '---|' * len(DB))
for k in [1, 5, 15, 30, 60]:
    r = con.execute('SELECT ' + ', '.join(ft(b[1].format(c=f'pre_dimb_{k}')) for b in DB) + ' FROM t').fetchone()
    print(f'| {k} bars | ' + ' | '.join(cell(r, 3 * j) for j in range(len(DB))) + ' |')
print()
print('| pre-entry window | ' + ' | '.join(f'volume ÷ usual {b[0]}' for b in VB) + ' |\n|---|' + '---|' * len(VB))
for k in [1, 5, 15, 30, 60]:
    r = con.execute('SELECT ' + ', '.join(ft(b[1].format(c=f'pre_rv_{k}')) for b in VB) + ' FROM t').fetchone()
    print(f'| {k} bars | ' + ' | '.join(cell(r, 3 * j) for j in range(len(VB))) + ' |')
print()
print('**Lopsided AND heavy** (30-bar window): pre_dimb_30 ≥ 0.3 × pre_rv_30 bucket\n')
print('| | ' + ' | '.join(b[0] for b in VB) + ' |\n|---|' + '---|' * len(VB))
for lab, dc in [('dimb_30 ≥ 0.3 (with the break)', 'pre_dimb_30 >= 0.3'), ('dimb_30 ≤ −0.1 (against)', 'pre_dimb_30 <= -0.1')]:
    r = con.execute('SELECT ' + ', '.join(ft(f"({dc}) AND {b[1].format(c='pre_rv_30')}") for b in VB) + ' FROM t').fetchone()
    print(f'| {lab} | ' + ' | '.join(cell(r, 3 * j) for j in range(len(VB))) + ' |')
print()
log('entering')

print('## 2. HOLDING: the delta since the entry at mark m → the REST of the trade from the next mark to 60 s\n')
print('Rest PF (fit → holdout, holdout trips). A rest PF > 1 on the "with us" rows and < 1 on the "against" rows = hold the '
      'strong, cut the weak.\n')
for m, nx in [(5, 10), (10, 15), (20, 30), (30, 45)]:
    print(f'### Decision at {m} s, scored from {nx} s\n')
    print('| dimb since entry | all | the trade UP at the mark | the trade DOWN at the mark |\n|---|---|---|---|')
    for lab, c in DB:
        cond = c.format(c=f'dimb_{m}')
        r = con.execute(f"SELECT {ft(cond, f'rest_{m}')}, {ft(f'({cond}) AND tk_{m} > 0', f'rest_{m}')}, "
                        f"{ft(f'({cond}) AND tk_{m} < 0', f'rest_{m}')} FROM t").fetchone()
        print(f'| {lab} | {cell(r, 0)} | {cell(r, 3)} | {cell(r, 6)} |')
    print()
log('holding')

print('## By year (PF raw 60 s, all groups; and with the break-side 30-bar delta ≥ 0.3)\n')
print('| year | all | pre_dimb_30 ≥ 0.3 |\n|---|---|---|')
for r in con.execute(f"SELECT yr, {pf('r')}, {pf('r', 'pre_dimb_30 >= 0.3')} FROM t GROUP BY yr ORDER BY yr").fetchall():
    print(f'| {r[0]} | {f(r[1])} | {f(r[2])} |')
print()
log('done')
