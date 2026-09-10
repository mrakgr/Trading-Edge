"""The Scanner parity diff (2026-09-09, §S49aj) — general: two trip/book parquets joined on (symbol, trade_date, signal_sec).

  trips:  set differences both ways + per-shared-column max |Δ| (NaN == NaN; NULL == NaN) — the engine-level seal
  book:   the same on a book (ret_exit, multiplier, entry/exit secs and prices), with the worst row shown

Everything runs INSIDE DuckDB (projection to the shared columns, hash join, one aggregate per column) so an 8M-row
research corpus with 340 columns diffs against a Scanner year in a bounded --mem. Run from research/:
  python scripts/equity/scanner_diff.py trips LEFT RIGHT [--year 2024] [--cols a,b,c] [--show 20] [--tol 0]
  python scripts/equity/scanner_diff.py book  SCANNER_BOOK REFERENCE_BOOK --tol 1e-9
LEFT/RIGHT may be globs. trade_date is compared as a yyyy-MM-dd string on both sides. Exit code 0 = zero-diff.
"""
import argparse, sys
import duckdb

ap = argparse.ArgumentParser()
ap.add_argument("mode", choices=["trips", "book"])
ap.add_argument("left"); ap.add_argument("right")
ap.add_argument("--year", type=int, default=0, help="restrict BOTH sides to year(trade_date) = this")
ap.add_argument("--start", default="", help="restrict BOTH sides to trade_date >= this (yyyy-mm-dd)")
ap.add_argument("--end", default="", help="restrict BOTH sides to trade_date <= this")
ap.add_argument("--cols", default="", help="restrict the column diff to these (comma list); default = every shared column")
ap.add_argument("--show", type=int, default=20)
ap.add_argument("--tol", type=float, default=0.0, help="a column 'differs' where |Δ| > this (default 0 = exact)")
ap.add_argument("--rtol", type=float, default=0.0, help="RELATIVE tolerance: |Δ| / max(|a|, |b|, 1) > this (summation-order noise on big sums)")
ap.add_argument("--mem", default="6GB")
args = ap.parse_args()
KEY = ["symbol", "trade_date", "signal_sec"]
con = duckdb.connect(); con.execute(f"SET memory_limit='{args.mem}'"); con.execute("SET threads=6")

def cols_of(path):
    return [(r[0], r[1]) for r in con.execute(f"DESCRIBE SELECT * FROM read_parquet('{path}')").fetchall()]
lc, rc = dict(cols_of(args.left)), dict(cols_of(args.right))
shared = [c for c in lc if c in rc and c not in KEY]
if args.cols: shared = [c for c in args.cols.split(",") if c in shared]
NUM = ("DOUBLE", "FLOAT", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "HUGEINT", "UINTEGER", "UBIGINT", "USMALLINT", "UTINYINT", "DECIMAL")
def is_num(t): return any(t.upper().startswith(n) for n in NUM)
conds = ([f"year(trade_date::DATE) = {args.year}"] if args.year else []) + ([f"trade_date::DATE >= '{args.start}'"] if args.start else []) + ([f"trade_date::DATE <= '{args.end}'"] if args.end else [])
where = ("WHERE " + " AND ".join(conds)) if conds else ""
proj = ", ".join(KEY[:1] + ["trade_date::VARCHAR AS trade_date", "signal_sec"] + [f'"{c}"' for c in shared])
con.execute(f"CREATE VIEW L AS SELECT {proj} FROM read_parquet('{args.left}') {where}")
con.execute(f"CREATE VIEW R AS SELECT {proj} FROM read_parquet('{args.right}') {where}")
nl, nr = con.execute("SELECT count(*) FROM L").fetchone()[0], con.execute("SELECT count(*) FROM R").fetchone()[0]
dl = con.execute("SELECT count(*) - count(DISTINCT (symbol, trade_date, signal_sec)) FROM L").fetchone()[0]
dr = con.execute("SELECT count(*) - count(DISTINCT (symbol, trade_date, signal_sec)) FROM R").fetchone()[0]
if dl: print(f"⚠ left: {dl} duplicate keys")
if dr: print(f"⚠ right: {dr} duplicate keys")
lo = con.execute("SELECT symbol, trade_date, signal_sec FROM L ANTI JOIN R USING (symbol, trade_date, signal_sec) ORDER BY 1,2,3").fetchall()
ro = con.execute("SELECT symbol, trade_date, signal_sec FROM R ANTI JOIN L USING (symbol, trade_date, signal_sec) ORDER BY 1,2,3").fetchall()
ns = con.execute("SELECT count(*) FROM L JOIN R USING (symbol, trade_date, signal_sec)").fetchone()[0]
yl = f" (year {args.year})" if args.year else ""
print(f"left  {args.left}{yl}: {nl:,} rows")
print(f"right {args.right}{yl}: {nr:,} rows")
print(f"keys: left-only {len(lo):,}, right-only {len(ro):,}, shared {ns:,}")
for lab, ks in [("left-only", lo), ("right-only", ro)]:
    for k in ks[:args.show]: print(f"  {lab}: {k[0]} {k[1]} {k[2]}")

# one aggregate per column over the join: max |Δ| with NaN==NaN and NULL==NaN; ±inf when exactly one side is NaN/NULL
exprs = []
for c in shared:
    if is_num(lc[c]) and is_num(rc[c]):
        a, b = f'L."{c}"::DOUBLE', f'R."{c}"::DOUBLE'
        na, nb = f"({a} IS NULL OR isnan({a}))", f"({b} IS NULL OR isnan({b}))"
        d = f"CASE WHEN {na} AND {nb} THEN 0.0 WHEN {na} OR {nb} THEN 'inf'::DOUBLE ELSE abs({a} - {b}) END"
        if args.rtol > 0: d = f"CASE WHEN {na} AND {nb} THEN 0.0 WHEN {na} OR {nb} THEN 'inf'::DOUBLE ELSE abs({a} - {b}) / greatest(abs({a}), abs({b}), 1.0) END"
    else:
        d = f'CASE WHEN L."{c}" IS NOT DISTINCT FROM R."{c}" THEN 0.0 ELSE 1.0 END'
    exprs.append(f"max({d}), count(*) FILTER (WHERE {d} > {(args.rtol if args.rtol > 0 else args.tol)!r})")
res = con.execute(f"SELECT {', '.join(exprs)} FROM L JOIN R USING (symbol, trade_date, signal_sec)").fetchone() if shared and ns else ()
print(f"\ncolumn diff over {ns:,} shared rows, {len(shared)} shared columns:")
bad = 0; w = max((len(c) for c in shared), default=10)
for i, c in enumerate(shared):
    mx, n = (res[2 * i] or 0.0, res[2 * i + 1] or 0) if res else (0.0, 0)
    if n: bad += 1
    print(f"  {c:<{w}}  max|Δ|{'/rel' if args.rtol > 0 else ''} {mx:<12.3e} rows≠ {n:>9,}{'' if n == 0 else '   ⚠'}")
ok = bad == 0 and not lo and not ro
print(f"\n{'✅ ZERO-DIFF' if ok else '⚠ DIFFERENCES'}: {ns:,} shared keys, {bad} of {len(shared)} shared columns differ, {len(lo) + len(ro):,} key-exclusive")
if args.mode == "book":
    for c in ["ret_exit", "multiplier"]:
        if c in shared:
            r = con.execute(f'SELECT symbol, trade_date, signal_sec, L."{c}", R."{c}", abs(L."{c}" - R."{c}") AS d FROM L JOIN R USING (symbol, trade_date, signal_sec) ORDER BY d DESC NULLS FIRST LIMIT 1').fetchone()
            if r and r[5]: print(f"  worst {c}: {r[0]} {r[1]} {r[2]} left {r[3]!r} right {r[4]!r} (Δ {r[5]:.3e})")
sys.exit(0 if ok else 1)
