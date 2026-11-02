#!/usr/bin/env python3
"""SWING MR SW-2 — the market-breadth gate and the distance from the moving averages (user 2026-11-02: "market breadth
works better than other measures ... how many stocks in our dataset that pass the liquidity filters are above their 20d
MA in percentage terms"; "Distance from the 72h MA worked really well in crypto perpetuals on the long side ... try a
variety of them").

Breadth (data/equity/swingmr/breadth.parquet): br20 / br50 / br200 = the share of the LIQUID names (med60 ≥ $1M, ≥ $5,
any trend) closing above their 20 / 50 / 200-session SMA at D's close; *_prev = D−1's (knowable before D's close).
The setups (SW-1, fit-percentile edges): z_20 bottom 2 %, z_50 bottom 1 %, RSI(2) < 5, and every universe day (the
control). Cells: x1 close-entry PF raw fit 2005–18 → holdout 2019–26 (holdout rows/yr; holdout mean bp).

  cd research && python -u scripts/equity/swingmr_report2.py > data/equity/swingmr/report2.md 2> logs/swingmr_report2.log; echo exit $?
"""
import sys
import time

import duckdb
import numpy as np

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

con = duckdb.connect()
con.execute("SET memory_limit='6GB'; SET threads=4")
con.execute("""CREATE VIEW t AS SELECT r.*, year(r.date) yr, year(r.date) <= 2018 fit, b.br20, b.br50, b.br200, b.br20_prev,
                 b.br50_prev, b.br200_prev
               FROM read_parquet('data/equity/swingmr/rows/*.parquet') r JOIN read_parquet('data/equity/swingmr/breadth.parquet') b USING (date)""")
NF = con.execute("SELECT count(DISTINCT yr) FILTER (fit), count(DISTINCT yr) FILTER (NOT fit) FROM t").fetchone()
S = con.execute("SELECT * FROM t WHERE fit USING SAMPLE 3 PERCENT (bernoulli, 11)").df()
log('sample', len(S))


def edge(col, p): return float(np.nanpercentile(S[col], p))


SETUPS = [('z_20 bottom 2 %', f"z_20 <= {edge('z_20', 2)}"), ('z_50 bottom 1 %', f"z_50 <= {edge('z_50', 1)}"),
          ('RSI(2) < 5', 'rsi2 < 5'), ('every universe day', 'true')]


def pf(col, flt):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x): return '—' if x is None else ('inf' if x == float('inf') else f'{x:.2f}')


def cell(flt, col='x1_rc'):
    r = con.execute(f"SELECT {pf(col, f'fit AND ({flt})')}, {pf(col, f'NOT fit AND ({flt})')}, "
                    f"count(*) FILTER (NOT fit AND ({flt})) / {NF[1]}, avg({col}) FILTER (NOT fit AND ({flt})) FROM t").fetchone()
    return f"{f(r[0])} → **{f(r[1])}** ({r[2]:,.0f}; {r[3]:+.0f})" if r[3] is not None else '—'


print('# Swing MR SW-2 — the breadth gate and the distance from the moving averages\n')
print('Cells: x1 close-entry PF raw **fit 2005–18 → holdout 2019–26** (holdout rows/yr; holdout mean bp per trade).\n')
B = [(0, 0.2), (0.2, 0.4), (0.4, 0.6), (0.6, 0.8), (0.8, 1.01)]
for br in ['br20', 'br20_prev', 'br50', 'br200']:
    print(f'## Breadth: {br} (the share of the liquid names above their {br[2:].split("_")[0]}-day SMA{", the PREVIOUS day" if "prev" in br else ""})\n')
    print('| setup | ' + ' | '.join(f'{lo:.0%}–{min(hi, 1):.0%}' for lo, hi in B) + ' |\n|---|' + '---|' * len(B))
    for lab, flt in SETUPS:
        print(f'| {lab} | ' + ' | '.join(cell(f'({flt}) AND {br} >= {lo} AND {br} < {hi}') for lo, hi in B) + ' |')
    print()
    log(br)

print('## The distance from the N-day SMA (%), the bottom tail of the fit\n')
print('| N | bottom 20 % | 10 % | 5 % | 2 % | 1 % |\n|---|---|---|---|---|---|')
for n in [3, 5, 10, 20, 50, 100, 200]:
    cells = []
    for p in [20, 10, 5, 2, 1]:
        e = edge(f'ma_{n}', p)
        cells.append(cell(f'ma_{n} <= {e}') + f' ≤ {100 * e:.1f} %')
    print(f'| {n} | ' + ' | '.join(cells) + ' |')
print()
log('ma')
print('## The distance from the SMA as a SECOND gate on z_20 bottom 2 % (the bottom 20 % / 5 % of each distance)\n')
z = SETUPS[0][1]
print('| N | z_20 2 % alone | + ma_N bottom 20 % | + ma_N bottom 5 % |\n|---|---|---|---|')
for n in [3, 5, 10, 50, 100]:
    print(f"| {n} | {cell(z)} | {cell(f'{z} AND ma_{n} <= {edge(f'ma_{n}', 20)}')} | {cell(f'{z} AND ma_{n} <= {edge(f'ma_{n}', 5)}')} |")
print()
print('## By year: z_20 bottom 2 % — ungated vs the best breadth gates (x1 close-entry PF raw; rows)\n')
gates = [('all', 'true'), ('br20 ≥ 40 %', 'br20 >= 0.4'), ('br20_prev ≥ 40 %', 'br20_prev >= 0.4'), ('br50 ≥ 50 %', 'br50 >= 0.5'),
         ('br200 ≥ 50 %', 'br200 >= 0.5')]
print('| year | ' + ' | '.join(g[0] for g in gates) + ' |\n|---|' + '---|' * len(gates))
cols = ', '.join(f"{pf('x1_rc', f'({z}) AND {g}')}, count(*) FILTER (({z}) AND {g})" for _, g in gates)
for r in con.execute(f'SELECT yr, {cols} FROM t GROUP BY yr ORDER BY yr').fetchall():
    print(f'| {r[0]} | ' + ' | '.join(f'{f(r[1 + 2 * j])} ({r[2 + 2 * j]:,})' for j in range(len(gates))) + ' |')
print()
log('done')
