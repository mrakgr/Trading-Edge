#!/usr/bin/env python3
"""SWING MR — the first report on data/equity/swingmr/rows (swingmr_build.py): z scores and efficiency ratios of varying
lengths and thresholds on stocks above the 200d SMA with the prior 60d median dollar volume ≥ $1M and price ≥ $5.

Thresholds are the FIT's own percentiles per feature (the bottom 20 / 10 / 5 / 2 / 1 %), from a 3 % fit sample, so
lengths compare like for like. Exits: x1 = the first close above the 5-day SMA (Connors), x2 = the first close above the
previous day's high (both ≤ 10 days), rc_5 / rc_10 fixed; entries at D's close (rc / x?_rc) and D+1's open (ro / x?_ro).
FIT = 2005–2018, HOLDOUT = 2019–2026 (user ruling). "−10" = PF net of 10 bp per round trip (a stress line, not a cost
model). n/yr = rows per year: a sampler, so the same name on consecutive days counts each day.

  cd research && python -u scripts/equity/swingmr_report.py > data/equity/swingmr/report.md 2> logs/swingmr_report.log; echo exit $?
"""
import sys
import time

import duckdb
import numpy as np

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

con = duckdb.connect()
con.execute("SET memory_limit='6GB'; SET threads=4")
con.execute("CREATE VIEW t AS SELECT *, year(date) yr, year(date) <= 2018 fit FROM read_parquet('data/equity/swingmr/rows/*.parquet')")
NF = con.execute("SELECT count(DISTINCT yr) FILTER (fit), count(DISTINCT yr) FILTER (NOT fit), count(*) FROM t").fetchone()
log('rows', NF[2])
S = con.execute("SELECT * FROM t WHERE fit USING SAMPLE 3 PERCENT (bernoulli, 11)").df()
ZN, EN = [3, 5, 10, 20, 50], [2, 3, 5, 10, 20]
PCT = [20, 10, 5, 2, 1]


def edge(col, p): return float(np.nanpercentile(S[col], p))


def pf(col, flt):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x): return '—' if x is None else ('inf' if x == float('inf') else f'{x:.2f}')


def stats(flt, cols=('x1_rc', 'x1_ro', 'rc_5', 'x2_rc')):
    q = [f'count(*) FILTER (fit AND ({flt})) / {NF[0]}', f'count(*) FILTER (NOT fit AND ({flt})) / {NF[1]}']
    for c in cols:
        q += [pf(c, f'fit AND ({flt})'), pf(c, f'NOT fit AND ({flt})'), f'avg({c}) FILTER (NOT fit AND ({flt}))']
    q += [pf('x1_rc - 10', f'NOT fit AND ({flt})')]
    return con.execute('SELECT ' + ', '.join(q) + ' FROM t').fetchone()


HDR = ('| n/yr fit / holdout | x1 close-entry PF | x1 open-entry PF | 5 d close-entry PF | x2 close-entry PF | '
       'x1 close holdout −10 |')


def line(lab, r, cols=4):
    cells = [f'{r[0]:,.0f} / {r[1]:,.0f}']
    for j in range(cols):
        a, b, m = r[2 + 3 * j], r[3 + 3 * j], r[4 + 3 * j]
        cells.append(f'{f(a)} → **{f(b)}** ({m:+.0f} bp)' if m is not None else '—')
    cells.append(f(r[2 + 3 * cols]))
    return f'| {lab} | ' + ' | '.join(cells) + ' |'


print('# Swing MR — z scores and efficiency ratios above the 200d SMA (first look)\n')
print('Universe: close above its 200-session SMA, prior-60-session median dollar volume ≥ \\$1M, raw close ≥ \\$5, CS/ADRC, '
      '2005–2026. Cells: PF raw **fit 2005–18 → holdout 2019–26** (holdout mean bp per trade). x1 = out at the first close '
      'above the 5-day SMA; x2 = the first close above the previous high (≤ 10 days).\n')
print('| cell ' + HDR + '\n|---|---|---|---|---|---|---|')
print(line('every universe day', stats('true')))
print(line('RSI(2) < 10 (Connors)', stats('rsi2 < 10')))
print(line('RSI(2) < 5', stats('rsi2 < 5')))
print()
log('baseline')

for fam, ns, nm in [('z', ZN, 'z score of the close vs its N-day mean'), ('er', EN, 'signed efficiency ratio (− = an efficient decline)')]:
    print(f'## {nm}: the bottom tail of the fit\n')
    print('| N | bottom | edge ' + HDR + '\n|---|---|---|---|---|---|---|---|')
    for n in ns:
        for p in PCT:
            e = edge(f'{fam}_{n}', p)
            r = stats(f'{fam}_{n} <= {e}')
            print(line(f'{n} | {p} % | ≤ {e:.3g}', r).replace('| ', '| ', 1))
    print()
    log(fam)

print('## The combination: a stretched z AND an efficient decline (bottom 10 % of each, fit edges)\n')
print('Rows = the z length; columns = the efficiency length. Cell: x1 close-entry PF fit → holdout (holdout n/yr).\n')
print('| z \\ er | ' + ' | '.join(f'er_{m}' for m in EN) + ' |\n|---|' + '---|' * len(EN))
best = []
for n in ZN:
    cells = []
    for m in EN:
        flt = f"z_{n} <= {edge(f'z_{n}', 10)} AND er_{m} <= {edge(f'er_{m}', 10)}"
        r = con.execute(f"SELECT {pf('x1_rc', f'fit AND {flt}')}, {pf('x1_rc', f'NOT fit AND {flt}')}, "
                        f"count(*) FILTER (NOT fit AND {flt}) / {NF[1]}, count(*) FILTER (fit AND {flt}) / {NF[0]} FROM t").fetchone()
        cells.append(f'{f(r[0])} → **{f(r[1])}** ({r[2]:,.0f})')
        best.append((r[0] or 0, n, m, flt, r[3]))
    print(f'| z_{n} | ' + ' | '.join(cells) + ' |')
print()
log('combo')

print('## By year: the three best combinations BY FIT PF (≥ 100 fit rows per year), x1 close entry\n')
top = sorted([b for b in best if b[4] >= 100], reverse=True)[:3]
print('| year | ' + ' | '.join(f'z_{b[1]} × er_{b[2]} (fit {b[0]:.2f})' for b in top) + ' | RSI(2) < 10 | every day |\n|---|' + '---|' * (len(top) + 2))
cols = ', '.join([pf('x1_rc', b[3]) + f', count(*) FILTER ({b[3]})' for b in top] + [pf('x1_rc', 'rsi2 < 10'), pf('x1_rc', 'true')])
for r in con.execute(f'SELECT yr, {cols} FROM t GROUP BY yr ORDER BY yr').fetchall():
    cells = [f'{f(r[1 + 2 * j])} ({r[2 + 2 * j]:,})' for j in range(len(top))] + [f(r[-2]), f(r[-1])]
    print(f'| {r[0]} | ' + ' | '.join(cells) + ' |')
print()
log('done')
