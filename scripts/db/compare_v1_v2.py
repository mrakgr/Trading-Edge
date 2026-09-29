# Data v2 against v1 (plan "Data v2" step 2 verification): every table v2 writes, compared with the same table in
# trading.db — row count, an order-independent content hash, and where the hash differs, the differing rows by key
# (exact, and within a relative tolerance for the float columns, which summation order can move in the last bits).
#
#   python scripts/db/compare_v1_v2.py [--v2 data/v2] [--db data/trading.db]
#
# Uses the duckdb CLI (the engines' 1.4.4) read-only. Run from research/.
import argparse, csv, subprocess, sys

ap = argparse.ArgumentParser()
ap.add_argument('--v2', default='data/v2')
ap.add_argument('--db', default='data/trading.db')
a = ap.parse_args()
KEYS = {'daily_prices': 'ticker, date', 'daily_adjusted': 'ticker, date', 'splits': 'id', 'dividends': 'id',
        'ticker_reference': 'ticker, type', 'split_corrections': 'ticker, execution_date'}
PRE = "SET memory_limit='6GB'; SET temp_directory='/mnt/d/trading-edge-bulk/duck_tmp'; SET preserve_insertion_order=false;"

def cli(sql):
    r = subprocess.run(['duckdb', '-readonly', '-csv', '-noheader', a.db, '-c', PRE, '-c', sql], capture_output=True, text=True)
    if r.returncode != 0: sys.exit(f'duckdb failed: {r.stderr.strip()}\nSQL: {sql[:400]}')
    return [row for row in csv.reader(r.stdout.splitlines()) if row]

bad = 0
for t, key in KEYS.items():
    pq = f"'{a.v2}/{t}.parquet'"
    cols = [c for c, in cli(f"SELECT column_name FROM information_schema.columns WHERE table_name = '{t}' ORDER BY ordinal_position")]
    pcols = [c for c, _ in cli(f"SELECT column_name, column_type FROM (DESCRIBE SELECT * FROM {pq})")]
    types_v1 = dict(cli(f"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{t}'"))
    types_v2 = dict(cli(f"SELECT column_name, column_type FROM (DESCRIBE SELECT * FROM {pq})"))
    sel = ', '.join(f'"{c}"' for c in cols)
    (n1, h1), = cli(f'SELECT count(*), bit_xor(hash(x)) FROM (SELECT {sel} FROM "{t}") x')
    (n2, h2), = cli(f'SELECT count(*), bit_xor(hash(x)) FROM (SELECT {sel} FROM {pq}) x')
    same = cols == pcols and types_v1 == types_v2 and n1 == n2 and h1 == h2
    print(f"{t:18} v1 {int(n1):>11,}  v2 {int(n2):>11,}  columns {'same' if cols == pcols else 'DIFFER'}  types {'same' if types_v1 == types_v2 else 'DIFFER'}  content {'IDENTICAL' if h1 == h2 else 'differs'}", flush=True)
    if types_v1 != types_v2:
        print(f"    types v1 {types_v1}\n    types v2 {types_v2}")
    if same: continue
    bad += 1
    # the differing rows, by key: only in v1, only in v2, and in both with different values
    k = [x.strip() for x in key.split(',')]
    on = ' AND '.join(f'v1."{c}" = v2."{c}"' for c in k)
    val = [c for c in cols if c not in k]
    neq = ' OR '.join(f'v1."{c}" IS DISTINCT FROM v2."{c}"' for c in val)
    fl = [c for c in val if types_v1.get(c) == 'DOUBLE']
    tol = ' OR '.join([f'NOT (abs(v1."{c}" - v2."{c}") <= 1e-9 * greatest(abs(v1."{c}"), abs(v2."{c}"), 1e-300) OR (v1."{c}" IS NULL AND v2."{c}" IS NULL))' for c in fl]
                      + [f'v1."{c}" IS DISTINCT FROM v2."{c}"' for c in val if c not in fl]) or 'false'
    (only1, only2, diff, difftol), = cli(f"""
        SELECT (SELECT count(*) FROM "{t}" v1 ANTI JOIN {pq} v2 ON {on}),
               (SELECT count(*) FROM {pq} v2 ANTI JOIN "{t}" v1 ON {on}),
               (SELECT count(*) FROM "{t}" v1 JOIN {pq} v2 ON {on} WHERE {neq}),
               (SELECT count(*) FROM "{t}" v1 JOIN {pq} v2 ON {on} WHERE {tol})""")
    print(f"    rows only in v1 {only1}, only in v2 {only2}, same key different values {diff} (beyond 1e-9 relative: {difftol})")
    if int(diff):
        ex = cli(f"""SELECT {', '.join('v1."' + c + '"' for c in k)}, {', '.join(f'v1."{c}", v2."{c}"' for c in val[:6])}
                     FROM "{t}" v1 JOIN {pq} v2 ON {on} WHERE {neq} LIMIT 5""")
        for r in ex: print('      e.g.', r)
print('ALL IDENTICAL' if bad == 0 else f'{bad} table(s) differ (see above)')
sys.exit(1 if bad else 0)
