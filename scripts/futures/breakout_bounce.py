"""Breakout BO-8 — the LONG 1m breakout in context (user 2026-11-02: "For long does the trade having a low z score improve
things? What about the leg counters? Maybe aggressive buying on a bounce from a deep decline could be a workable setup?").

BO-7's long trips (a new 60-bar high, out 60 s later). A long break carries PitFader's SHORT leg instance (its EXT = new
20m highs, OPP = new 20m lows), so for these trips:
  z_since_opp      = the price's z vs the vw log-price path since the last new 20m LOW (low = barely off the low)
  z_since_flow     = the same since the bounce's first new 20m high
  k / k_300 / k_60 = new 20m highs since the bounce's first (k), reset by a new 20m / 300-bar / 60-bar low
  breach_opp_1200  = bars since the last new 20m low (small = the low is recent)
  ols_r_1200       = the signed r of the 20m regression (−1 = a clean decline)
  d20              = ln(vwap / vwap_1200): the price vs the 20m vwap
  pre_dimb_30      = the aggressive flow WITH the break over the 30 bars before it
Only trips with the 20m vwap warm (from the session's 1200th bar). Quintile edges from a 2 % sample of the FIT (2015–22); cells = PF raw at 60 s, fit → holdout (holdout trips). SQL
aggregates only.

  cd research && python -u scripts/futures/breakout_bounce.py > data/futures/breakout/bounce.md 2> logs/breakout_bounce.log; echo exit $?
"""
import sys
import time

import duckdb
import numpy as np

T0 = time.time()
def log(*x): print(f'[{time.time() - T0:7.1f}s]', *x, file=sys.stderr, flush=True)

GROUPS = {'equity': ['ES', 'MES', 'NQ'], 'bonds': ['ZB', 'ZN', 'ZT'], 'fx': ['6A', '6B', '6C', '6E', '6J', '6N', '6S', '6Z', 'M6A', 'M6E', 'MJY', 'M6J', 'E7'],
          'energy': ['CL', 'MCL', 'NG', 'RB'], 'metals': ['HG', 'MGC'], 'ags': ['ZC', 'ZS', 'HE']}
con = duckdb.connect()
con.execute("set memory_limit = '6GB'; set threads = 4; set preserve_insertion_order = false")
gcase = 'CASE ' + ' '.join(f"WHEN root IN ({','.join(repr(r) for r in rs)}) THEN '{g}'" for g, rs in GROUPS.items()) + " ELSE 'other' END"
con.execute(f"""CREATE TEMP TABLE L AS SELECT {gcase} grp, year(td) <= 2022 fit, year(td) yr,
    1e4 * (px_60 - entry_px) / abs(entry_px) r, (px_60 - entry_px) / tick tk, 1e4 * tick / abs(entry_px) tbp,
    z_since_opp, z_since_flow, k, k_300, k_60, breach_opp_1200, ols_r_1200, ln(vwap_60 / vwap_1200) d20, pre_dimb_30,
    pre_vol_30 / (30 * vref_bar) rv30
    FROM read_parquet('data/futures/breakout/run_bo7/legs/*.parquet')
    WHERE side = 1 AND entry_px > 0 AND NOT eod AND vwap_1200 > 0 AND vwap_60 > 0""")
log('longs', con.execute('SELECT count(*) FROM L').fetchone()[0])
S = con.execute('SELECT * FROM L WHERE fit USING SAMPLE 2 PERCENT (bernoulli, 7)').df()
log('sample', len(S))


def pf(col, flt):
    return f"sum(greatest({col}, 0)) FILTER ({flt}) / nullif(sum(greatest(-({col}), 0)) FILTER ({flt}), 0)"


def f(x): return '—' if x is None else f'{x:.2f}'


def cells(conds):
    q = ', '.join(f"{pf('r', f'fit AND ({c})')}, {pf('r', f'NOT fit AND ({c})')}, count(*) FILTER (NOT fit AND ({c}))" for c in conds)
    r = con.execute(f'SELECT {q} FROM L').fetchone()
    return [f'{f(r[3 * j])} → **{f(r[3 * j + 1])}** ({r[3 * j + 2]:,})' for j in range(len(conds))]


def qconds(col):
    e = np.nanquantile(S[col].astype(float), [0.2, 0.4, 0.6, 0.8])
    b = [-np.inf, *e, np.inf]
    def one(lo, hi):
        return ' AND '.join(([f'{col} >= {lo}'] if np.isfinite(lo) else []) + ([f'{col} < {hi}'] if np.isfinite(hi) else []))
    return [one(b[k], b[k + 1]) for k in range(5)], e


print('# Breakout BO-8 — the LONG 1m breakout in context: the z score, the leg counters, a bounce from a deep decline\n')
r = con.execute(f"SELECT {pf('r', 'fit')}, {pf('r', 'NOT fit')}, count(*) FILTER (NOT fit) FROM L").fetchone()
print(f'All long breaks: {f(r[0])} → **{f(r[1])}** ({r[2]:,} holdout). Cells: PF raw at 60 s, fit 2015–22 → **holdout 2023–26** '
      '(holdout trips); quintile edges from the fit.\n')
print('| feature | Q1 (lowest) | Q2 | Q3 | Q4 | Q5 (highest) | fit edges |\n|---|---|---|---|---|---|---|')
for col, nm in [('z_since_opp', 'z since the last 20m low'), ('z_since_flow', 'z since the bounce\'s first 20m high'),
                ('k', 'k: new 20m highs in the bounce'), ('k_300', 'k_300'), ('k_60', 'k_60'),
                ('breach_opp_1200', 'bars since the last new 20m low'), ('ols_r_1200', 'the 20m regression r (−1 = a clean decline)'),
                ('d20', 'the price vs the 20m vwap'), ('pre_dimb_30', 'the 30-bar delta with the break')]:
    c, e = qconds(col)
    print(f'| {nm} | ' + ' | '.join(cells(c)) + f' | {" / ".join(f"{x:.3g}" for x in e)} |')
print()
log('ladders')

print('## ⭐ The setup: aggressive buying on a bounce from a deep decline\n')
print('Rows = the decline (the 20m regression r and how recent the 20m low is); columns = the 30-bar delta with the break.\n')
print('| decline | dimb_30 ≤ 0 | 0 … 0.3 | ≥ 0.3 | ≥ 0.3 and volume ≥ 2× |\n|---|---|---|---|---|')
for lab, c in [('any', 'true'), ('20m r ≤ −0.5', 'ols_r_1200 <= -0.5'), ('20m r ≤ −0.8', 'ols_r_1200 <= -0.8'),
               ('20m r ≤ −0.5, low ≤ 120 bars ago', 'ols_r_1200 <= -0.5 AND breach_opp_1200 <= 120'),
               ('20m r ≤ −0.8, low ≤ 120 bars ago', 'ols_r_1200 <= -0.8 AND breach_opp_1200 <= 120'),
               ('price ≥ 0.3 % under the 20m vwap', 'd20 <= -0.003'),
               ('≥ 0.3 % under, low ≤ 120 bars ago', 'd20 <= -0.003 AND breach_opp_1200 <= 120'),
               ('z since the 20m low ≤ fit Q1', f'z_since_opp < {np.nanquantile(S.z_since_opp, 0.2)}')]:
    print(f'| {lab} | ' + ' | '.join(cells([f'({c}) AND pre_dimb_30 <= 0', f'({c}) AND pre_dimb_30 > 0 AND pre_dimb_30 < 0.3',
                                              f'({c}) AND pre_dimb_30 >= 0.3', f'({c}) AND pre_dimb_30 >= 0.3 AND rv30 >= 2'])) + ' |')
print()
log('setup')

print('## The setup by group: 20m r ≤ −0.5, the low ≤ 120 bars ago (columns as above)\n')
print('| group | dimb_30 ≤ 0 | 0 … 0.3 | ≥ 0.3 | ≥ 0.3 and volume ≥ 2× |\n|---|---|---|---|---|')
for g in GROUPS:
    c = f"grp = '{g}' AND ols_r_1200 <= -0.5 AND breach_opp_1200 <= 120"
    print(f'| {g} | ' + ' | '.join(cells([f'({c}) AND pre_dimb_30 <= 0', f'({c}) AND pre_dimb_30 > 0 AND pre_dimb_30 < 0.3',
                                            f'({c}) AND pre_dimb_30 >= 0.3', f'({c}) AND pre_dimb_30 >= 0.3 AND rv30 >= 2'])) + ' |')
print()
print('## By year: 20m r ≤ −0.5, the low ≤ 120 bars ago\n')
print('| year | all | dimb_30 ≥ 0.3 | dimb_30 ≤ 0 |\n|---|---|---|---|')
w = 'ols_r_1200 <= -0.5 AND breach_opp_1200 <= 120'
for r in con.execute(f"""SELECT yr, {pf('r', w)}, {pf('r', f'{w} AND pre_dimb_30 >= 0.3')}, {pf('r', f'{w} AND pre_dimb_30 <= 0')}
                         FROM L GROUP BY yr ORDER BY yr""").fetchall():
    print(f'| {r[0]} | {f(r[1])} | {f(r[2])} | {f(r[3])} |')
print()
log('done')
