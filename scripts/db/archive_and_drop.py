# v1 cleanup (user 2026-09-29, plan "Data v2" step 1): archive every table NOT on the KEEP list to parquet, verify the
# archive against the table (row count AND an order-independent content hash), then drop it.
#
#   python scripts/db/archive_and_drop.py                # dry run: prints what would be kept / archived
#   python scripts/db/archive_and_drop.py --apply        # archive + verify all, then drop the verified ones
#
# Every DB operation goes through the `duckdb` CLI, which must be the engines' version (DuckDB.NET 1.4.4): a newer
# library would upgrade the file's storage format and lock the engines out. Run from research/.
# A table missing from KEEP is archived — never silently dropped: the dry run lists it first.
import argparse, csv, datetime as dt, hashlib, os, subprocess, sys

KEEP_TABLES = {
    # the raw daily chain and its causal adjustment
    'daily_prices', 'splits', 'dividends', 'split_corrections', 'daily_adjusted', 'ticker_reference',
    # the back-adjusted table: the legacy 1m systems and the daily_episodes view still read it
    'split_adjusted_prices',
    # universes: legacy 1m (LowFlyer / MaxFlyer), legacy 1s, current
    'mr_candidate', 'mr_candidate_1s', 'mr_candidate_1s_v2',
    # the reference corpora of the production specs
    'flushfader_v49tkd_cand', 'flushfader_v17tkd_cand', 'spikefader_s44_whitelist', 'lowfader_wide_whitelist',
}
KEEP_VIEWS = {'daily_episodes', 'daily_episodes_causal'}
ENGINE_DUCKDB = 'v1.4.4'

ap = argparse.ArgumentParser()
ap.add_argument('--db', default='data/trading.db')
ap.add_argument('--archive', default=f'/mnt/d/trading-edge-bulk/db_archive/{dt.date.today():%Y-%m-%d}')
ap.add_argument('--apply', action='store_true')
a = ap.parse_args()

def cli(sql: str, readonly=False) -> list[list[str]]:
    args = ['duckdb'] + (['-readonly'] if readonly else []) + ['-csv', '-noheader', a.db, '-c',
            "SET memory_limit='6GB'; SET temp_directory='/mnt/d/trading-edge-bulk/duck_tmp';" if not readonly else "SELECT 1 WHERE false;",
            '-c', sql]
    r = subprocess.run(args, capture_output=True, text=True)
    if r.returncode != 0: sys.exit(f'duckdb failed ({r.returncode}): {r.stderr.strip()}\nSQL: {sql}')
    return [row for row in csv.reader(r.stdout.splitlines()) if row]

ver = subprocess.run(['duckdb', '--version'], capture_output=True, text=True).stdout.split()[0]
if ver != ENGINE_DUCKDB: sys.exit(f'duckdb CLI is {ver}, the engines use {ENGINE_DUCKDB}: refusing to write the file')

objs = cli("SELECT table_name, table_type FROM information_schema.tables ORDER BY table_name", readonly=True)
tables = [t for t, k in objs if k == 'BASE TABLE']; views = [t for t, k in objs if k == 'VIEW']
unknown_views = [v for v in views if v not in KEEP_VIEWS]
if unknown_views: sys.exit(f'views not on the KEEP list (decide first): {unknown_views}')
missing = sorted((KEEP_TABLES | KEEP_VIEWS) - set(tables) - set(views))
if missing: sys.exit(f'KEEP names not in the database (typo?): {missing}')
drop = [t for t in tables if t not in KEEP_TABLES]
count = {t: int(n) for t, n in cli(' UNION ALL '.join(f"SELECT '{t}', count(*) FROM \"{t}\"" for t in tables), readonly=True)}
print(f'{len(tables)} tables, {len(views)} views; KEEP {len(KEEP_TABLES)} tables + {len(KEEP_VIEWS)} views, ARCHIVE + DROP {len(drop)}:')
for t in drop: print(f'  {t:40} {count[t]:>12,} rows')
print(f'kept rows: {sum(count[t] for t in KEEP_TABLES):,}; archived rows: {sum(count[t] for t in drop):,}')
if not a.apply: print('dry run (pass --apply to archive and drop)'); sys.exit(0)

os.makedirs(a.archive, exist_ok=True)
man_path = os.path.join(a.archive, 'manifest.tsv')
done = set()
if os.path.exists(man_path):
    with open(man_path) as f: done = {r['table'] for r in csv.DictReader(f, delimiter='\t') if r['verified'] == 'yes'}
new_man = not os.path.exists(man_path)
man = open(man_path, 'a', newline='')
w = csv.writer(man, delimiter='\t')
if new_man: w.writerow(['table', 'rows', 'columns', 'bytes', 'sha256', 'table_hash', 'parquet_hash', 'verified'])
verified = set(done)
for t in drop:
    if t in done: print(f'  {t}: already archived + verified'); continue
    out = os.path.join(a.archive, f'{t}.parquet')
    cli(f"COPY \"{t}\" TO '{out}.tmp' (FORMAT PARQUET, COMPRESSION zstd)", readonly=True)
    os.replace(out + '.tmp', out)
    # parquet has no 128-bit integers: DuckDB writes HUGEINT/UHUGEINT columns as DOUBLE. Hash the table with the same cast,
    # and prove the cast exact (every value survives the round trip), so the archive is still a lossless copy.
    cols = cli(f"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{t}' ORDER BY ordinal_position", readonly=True)
    wide = [c for c, ty in cols if ty in ('HUGEINT', 'UHUGEINT')]
    sel = ', '.join(f'"{c}"::DOUBLE AS "{c}"' if c in wide else f'"{c}"' for c, _ in cols)
    if wide:
        (lossy,), = cli(f"SELECT count(*) FROM \"{t}\" WHERE " + ' OR '.join(f'"{c}"::DOUBLE::{ty} IS DISTINCT FROM "{c}"' for c, ty in cols if c in wide), readonly=True)
        if int(lossy): print(f'  {t}: {lossy} values of {wide} do not survive HUGEINT -> DOUBLE: kept', flush=True); continue
    (tn, th), = cli(f"SELECT count(*), bit_xor(hash(x)) FROM (SELECT {sel} FROM \"{t}\") x", readonly=True)
    (pn, ph), = cli(f"SELECT count(*), bit_xor(hash(x)) FROM '{out}' x", readonly=True)
    ncol = cli(f"SELECT count(*) FROM information_schema.columns WHERE table_name = '{t}'", readonly=True)[0][0]
    h = hashlib.sha256()
    with open(out, 'rb') as f:
        for b in iter(lambda: f.read(1 << 24), b''): h.update(b)
    ok = int(tn) == int(pn) == count[t] and th == ph
    w.writerow([t, tn, ncol, os.path.getsize(out), h.hexdigest(), th, ph, 'yes' if ok else 'NO']); man.flush()
    print(f"  {t}: {int(tn):,} rows -> {os.path.getsize(out)/1e6:,.1f} MB  {'verified' if ok else 'MISMATCH (kept, not dropped)'}", flush=True)
    if ok: verified.add(t)
man.close()
bad = [t for t in drop if t not in verified]
if bad: print(f'NOT dropped (archive did not verify): {bad}')
to_drop = [t for t in drop if t in verified]
if to_drop:
    cli('BEGIN; ' + ' '.join(f'DROP TABLE "{t}";' for t in to_drop) + ' COMMIT; CHECKPOINT;')
    print(f'dropped {len(to_drop)} tables; the file shrinks only on compaction (scripts/db/compact.py)')
left = {t for t, k in cli("SELECT table_name, table_type FROM information_schema.tables", readonly=True)}
print(f'now {len(left)} objects; KEEP intact: {(KEEP_TABLES | KEEP_VIEWS) <= left}')
sys.exit(1 if bad else 0)
