#!/usr/bin/env python3
"""StraddleHarvester — the WHOLE-MARKET job list for the ThetaData EOD history (user 2026-11-11: "we'll get all of it").

Every root ThetaData lists (/v3/option/list/symbols), its window bounded by its own expirations
(/v3/option/list/expirations?symbol=X): [first expiration − 3 years (LEAPS list ~2.5 years out), last expiration],
intersected with each of --ranges IN PRIORITY ORDER (user 2026-11-11: first 2022-11-09 → 2026-11-10, then
2012-06-01 → 2022-11-08; the lines of range 1 come first, and eod_pull.py works through them in file order).
--exclude-file (ROOT START END lines, e.g. data/options/universe_jobs.tsv) subtracts spans already pulled.
Writes ROOT<TAB>START<TAB>END lines for eod_pull.py --jobs-file (which cuts them into calendar quarters).
Needs the Theta Terminal.

    python3 build_market_jobs.py --out market_jobs.tsv > build_market_jobs.log 2>&1; echo "exit $?"
"""
import argparse, concurrent.futures as cf, datetime as dt, io, sys, urllib.error, urllib.parse, urllib.request
import pandas as pd

BASE = 'http://127.0.0.1:25503/v3/option'


def get(path, **q):
    url = f"{BASE}/{path}?" + urllib.parse.urlencode({**q, 'format': 'csv'})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=300) as r:
                return r.status, r.read()
        except urllib.error.HTTPError as e:
            if e.code == 472:
                return 472, b''
            err = e.code
        except Exception as e:
            err = type(e).__name__
        import time; time.sleep(5 * (attempt + 1))
    return err, b''


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ranges', default='2012-06-01:2022-11-08', help='START:END[,START:END...] in priority order')
    ap.add_argument('--exclude-file', help='ROOT START END lines: spans to leave out (already pulled)')
    ap.add_argument('--out', required=True)
    ap.add_argument('--workers', type=int, default=8)
    a = ap.parse_args()
    ranges = [tuple(dt.date.fromisoformat(x) for x in r.split(':')) for r in a.ranges.split(',')]
    excl = {}
    if a.exclude_file:
        for l in open(a.exclude_file):
            if l.strip():
                r, s0, e0 = l.split(); excl.setdefault(r, []).append((dt.date.fromisoformat(s0), dt.date.fromisoformat(e0)))

    def subtract(s, e, spans):
        pieces = [(s, e)]
        for xs, xe in spans:
            nxt = []
            for ps, pe in pieces:
                if xe < ps or xs > pe:
                    nxt.append((ps, pe)); continue
                if ps < xs:
                    nxt.append((ps, xs - dt.timedelta(days=1)))
                if xe < pe:
                    nxt.append((xe + dt.timedelta(days=1), pe))
            pieces = nxt
        return pieces
    st, body = get('list/symbols')
    assert st == 200, st
    roots = sorted(pd.read_csv(io.BytesIO(body)).symbol.dropna().astype(str))
    print(f"{len(roots):,} roots listed", flush=True)

    def window(root):
        st, body = get('list/expirations', symbol=root)
        if st != 200 or not body.strip():
            return root, None, None, st
        e = pd.to_datetime(pd.read_csv(io.BytesIO(body)).expiration).dt.date
        return root, e.min(), e.max(), st

    wins, bad, n = {}, [], 0
    with cf.ThreadPoolExecutor(a.workers) as ex:
        for root, e0, e1, st in ex.map(window, roots):
            n += 1
            if e0 is None:
                if st != 472:
                    bad.append((root, st))
                continue
            wins[root] = (e0 - dt.timedelta(days=1100), e1)
            if n % 1000 == 0:
                print(f"{n:,}/{len(roots):,} roots looked up", flush=True)
    jobs = []
    for lo, hi in ranges:                                  # priority order: all of range 1, then range 2, ...
        k = len(jobs)
        for root in sorted(wins):
            s, e = max(lo, wins[root][0]), min(hi, wins[root][1])
            if s <= e:
                jobs += [(root, ps, pe) for ps, pe in subtract(s, e, excl.get(root, []))]
        print(f"range {lo} → {hi}: {len(jobs) - k:,} job lines", flush=True)
    with open(a.out, 'w') as f:
        for r, s, e in jobs:
            f.write(f"{r}\t{s}\t{e}\n")
    q = sum(((e.year - s.year) * 4 + (e.month - 1) // 3 - (s.month - 1) // 3 + 1) for _, s, e in jobs)
    print(f"wrote {len(jobs):,} jobs (~{q:,} quarter requests) -> {a.out}; expirations lookup errors: {len(bad)} {bad[:10]}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
