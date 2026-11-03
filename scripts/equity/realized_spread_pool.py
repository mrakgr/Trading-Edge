"""THE REALIZED SPREAD POOLED over the saved name-days (realized_spread.py --save): EQUAL weight per name (each name's
share-weighted average), lit trades only. Per time-since-open bucket, and the mean-reversion split (the maker's edge vs a
causal 30 s / 10 s EWMA, from 5 min after the open). Cells: the mean over names of the realized spread in bp, and how
many names are > 0.
  cd research && python -u scripts/equity/realized_spread_pool.py data/equity/realized_spread/trades > data/equity/realized_spread/report_pool.md
"""
import sys, duckdb
src = sys.argv[1]
con = duckdb.connect(); con.execute("set memory_limit='3GB'; set threads=4")
con.execute(f"CREATE VIEW t AS SELECT * FROM read_parquet('{src}/*.parquet') WHERE lit")
D = [1, 5, 30, 60, 300]
N = con.execute("SELECT count(DISTINCT ticker) FROM t").fetchone()[0]
print(f'# The realized spread, pooled over {N} IPO first days (lit trades, equal weight per name)\n')
print('Cells: the mean over names of each name\'s share-weighted bp (and how many names are > 0).\n')
B = [(0, 30, '0–30 s'), (30, 300, '30 s–5 min'), (300, 600, '5–10 min'), (600, 1800, '10–30 min'), (1800, 3600, '30–60 min'), (3600, 1e9, '1 h +')]
def table(groups, label):
    print(f'| {label} | names | quoted spread | effective half | ' + ' | '.join(f'realized {d}s' for d in D) + ' |')
    print('|---|---|---|---|' + '---|' * len(D))
    for lab, flt in groups:
        cols = ', '.join(f"sum(S * rs_{d}) / sum(S) r{d}" for d in D)
        q = con.execute(f"""WITH n AS (SELECT ticker, median(qs_bp) qs, sum(S * eff_bp) / sum(S) eff, {cols} FROM t WHERE {flt} GROUP BY ticker)
            SELECT count(*), avg(qs), avg(eff), {', '.join(f'avg(r{d}), sum((r{d} > 0)::INT)' for d in D)} FROM n""").fetchone()
        if not q[0]: continue
        print(f'| {lab} | {q[0]} | {q[1]:.1f} | {q[2]:.1f} | ' + ' | '.join(f'**{q[3 + 2 * k]:+.1f}** ({q[4 + 2 * k]})' for k in range(len(D))) + ' |')
    print()
print('## By time since the first trade\n')
table([(lab, f'sec >= {lo} AND sec < {hi}') for lo, hi, lab in B] + [('**the day**', 'true')], 'since the open')
for hl in [30, 10]:
    print(f'## The mean-reversion split, {hl} s EWMA (from 5 min after the open)\n')
    table([('with the idea (e > 0)', f'sec >= 300 AND e_{hl} > 0'), ('against it (e ≤ 0)', f'sec >= 300 AND e_{hl} <= 0'),
           ('e > 20 bp', f'sec >= 300 AND e_{hl} > 20'), ('5 < e ≤ 20', f'sec >= 300 AND e_{hl} > 5 AND e_{hl} <= 20'),
           ('0 < e ≤ 5', f'sec >= 300 AND e_{hl} > 0 AND e_{hl} <= 5'), ('e ≤ −20', f'sec >= 300 AND e_{hl} <= -20')], 'maker edge vs the MA')
    print(f'### …the same split, 5–30 min after the open only\n')
    table([('with the idea (e > 0)', f'sec >= 300 AND sec < 1800 AND e_{hl} > 0'), ('against it (e ≤ 0)', f'sec >= 300 AND sec < 1800 AND e_{hl} <= 0')], 'maker edge vs the MA')
