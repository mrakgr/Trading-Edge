# Compact trading.db after archive_and_drop.py (plan "Data v2" step 1): DuckDB never shrinks a file on DROP, only on a
# rewrite. COPY FROM DATABASE into a fresh file, then verify EVERY object — per table and view the row count and an
# order-independent content hash, plus the constraints (the ingest's upserts need the primary keys) and the view SQL —
# before swapping. The old file is kept as trading.pre_cleanup.db until the user deletes it.
#
#   python scripts/db/compact.py            # compact + verify, no swap
#   python scripts/db/compact.py --swap     # ... and swap the files when everything matches
#
# The duckdb CLI must be the engines' version (1.4.4), as in archive_and_drop.py. Run from research/.
import argparse, csv, os, subprocess, sys, time

ap = argparse.ArgumentParser()
ap.add_argument('--db', default='data/trading.db')
ap.add_argument('--out', default='data/trading.compact.db')
ap.add_argument('--swap', action='store_true')
a = ap.parse_args()
ENGINE_DUCKDB = 'v1.4.4'
TMP = "SET memory_limit='6GB'; SET temp_directory='/mnt/d/trading-edge-bulk/duck_tmp';"

def cli(db, sql, readonly=True):
    args = ['duckdb'] + (['-readonly'] if readonly and db != ':memory:' else []) + ['-csv', '-noheader'] + ([db] if db != ':memory:' else []) + ['-c', TMP, '-c', sql]
    r = subprocess.run(args, capture_output=True, text=True)
    if r.returncode != 0: sys.exit(f'duckdb failed ({r.returncode}): {r.stderr.strip()}\nSQL: {sql[:300]}')
    return [row for row in csv.reader(r.stdout.splitlines()) if row]

def fingerprint(db):
    objs = cli(db, "SELECT table_name, table_type FROM information_schema.tables ORDER BY table_name")
    fp = {}
    for name, kind in objs:
        (n, h), = cli(db, f'SELECT count(*), bit_xor(hash(x)) FROM "{name}" x')
        fp[name] = (kind, int(n), h)
        print(f'  {name:32} {kind:10} {int(n):>12,} rows  {h}', flush=True)
    cons = sorted(tuple(r) for r in cli(db, "SELECT table_name, constraint_type, array_to_string(constraint_column_names, ',') FROM duckdb_constraints() WHERE constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'NOT NULL') ORDER BY ALL"))
    views = sorted(tuple(r) for r in cli(db, "SELECT view_name, sql FROM duckdb_views() WHERE NOT internal ORDER BY 1"))
    return fp, cons, views

ver = subprocess.run(['duckdb', '--version'], capture_output=True, text=True).stdout.split()[0]
if ver != ENGINE_DUCKDB: sys.exit(f'duckdb CLI is {ver}, the engines use {ENGINE_DUCKDB}')
if os.path.exists(a.out): sys.exit(f'{a.out} exists: remove it first (never overwritten)')
t0 = time.time()
print(f'BEFORE ({a.db}, {os.path.getsize(a.db)/1e9:.2f} GB):', flush=True)
before = fingerprint(a.db)
print(f'copying -> {a.out} ({time.time()-t0:.0f} s)', flush=True)
cli(':memory:', f"ATTACH '{a.db}' AS src (READ_ONLY); ATTACH '{a.out}' AS dst; COPY FROM DATABASE src TO dst; DETACH dst;", readonly=False)
print(f'AFTER ({a.out}, {os.path.getsize(a.out)/1e9:.2f} GB, {time.time()-t0:.0f} s):', flush=True)
after = fingerprint(a.out)
ok = True
for what, b, c in (('objects', before[0], after[0]), ('constraints', before[1], after[1]), ('views', before[2], after[2])):
    if b != c:
        ok = False
        print(f'MISMATCH in {what}:')
        if isinstance(b, dict):
            for k in sorted(set(b) | set(c)):
                if b.get(k) != c.get(k): print(f'  {k}: before {b.get(k)}  after {c.get(k)}')
        else:
            print(f'  only before: {sorted(set(b) - set(c))}\n  only after: {sorted(set(c) - set(b))}')
print(f"{'ALL MATCH' if ok else 'NOT SWAPPED: mismatches above'}: {len(before[0])} objects, {len(before[1])} constraints, {len(before[2])} views; "
      f'{os.path.getsize(a.db)/1e9:.2f} GB -> {os.path.getsize(a.out)/1e9:.2f} GB')
if ok and a.swap:
    old = a.db.replace('.db', '.pre_cleanup.db')
    if os.path.exists(old): sys.exit(f'{old} exists: not swapping')
    os.replace(a.db, old); os.replace(a.out, a.db)
    print(f'swapped: {a.db} is the compacted file; the original is {old}')
sys.exit(0 if ok else 1)
