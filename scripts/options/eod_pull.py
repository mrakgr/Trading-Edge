#!/usr/bin/env python3
"""StraddleHarvester data — pull ThetaData's per-contract EOD option rows, ONE ROOT PER REQUEST (user 2026-11-10).

Whole-market EOD flat files are PRO-only (and only the last 7 days), and `symbol=*` is refused, so every root is asked
separately: GET /v3/option/history/eod?symbol=X&expiration=*&start_date&end_date (calls + puts, every expiry).
A row = one contract on one day: OHLC, volume, trade count, and the LAST NBBO of the day (bid/ask + sizes).

Two modes:
  --start/--end (+ optional --roots-file): every root over one range -> <out>/<ROOT>.parquet
  --jobs-file (ROOT<TAB>START<TAB>END per line): each root over its OWN range, cut into calendar-quarter chunks
              (bounds a request's memory: AAPL is ~47k rows a month unfiltered) -> <out>/<ROOT>__<chunkstart>.parquet
--max-dte N passes ThetaData's max_dte (contracts <= N calendar days to expiry): 60 cut AAPL's rows to 35% and the time
to half; `strike_range` was measured 7x SLOWER (server-side spot lookup) — trim strikes locally instead.
<out>/done.tsv (key, status, rows, seconds) is the RESUME record: a rerun skips every key whose LAST status is final
(ok / nodata / empty); errors and cut-off responses are retried.
⚠ CUT-OFF RESPONSES (2026-11-10): the Terminal can end a response early with HTTP 200. Every response is checked
against MASSIVE's days for the root (its last date must reach Massive's last day in the range) and retried up to 8 times
with backoff (a cut can repeat across attempts, so agreement proves nothing);
`--validate` checks chunks already saved and quarantines the cut-off ones.
Needs the Theta Terminal (127.0.0.1:25503; start it with ~/thetadata/start.sh). Options STANDARD: 4 concurrent.

    cd research && python3 scripts/options/eod_pull.py --start 2026-10-01 --end 2026-11-06 \
        --out data/options/eod_pilot_2026-10 > logs/options_eod_pilot_2026-10.log 2>&1; echo "exit $?"
    cd research && python3 scripts/options/eod_pull.py --jobs-file data/options/universe_jobs.tsv --max-dte 90 \
        --out data/options/eod_universe > logs/options_eod_universe.log 2>&1; echo "exit $?"
"""
import argparse, concurrent.futures as cf, datetime as dt, io, json, os, sys, time, urllib.error, urllib.parse, urllib.request
import pandas as pd

BASE = 'http://127.0.0.1:25503/v3/option'
KEEP = ['symbol', 'expiration', 'strike', 'right', 'created', 'open', 'high', 'low', 'close', 'volume', 'count',
        'bid_size', 'bid', 'ask_size', 'ask']


def http(path, **q):
    url = f"{BASE}/{path}?" + urllib.parse.urlencode(q)
    try:
        with urllib.request.urlopen(url, timeout=900) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:              # 472 = ThetaData's NO DATA
        return e.code, e.read()


def list_roots():
    st, body = http('list/symbols', format='ndjson')
    assert st == 200, (st, body[:200])
    return sorted(json.loads(l)['symbol'] for l in body.decode().splitlines() if l.strip())


class Reference:
    """How far each root's data SHOULD run, from INDEPENDENT sources:
      from Massive's first day (2022-11-09) on: the days Massive's whole-market options day files have any contract of
        the root (data/options/massive_root_days.parquet; a root Massive never saw -> every Massive day);
      before that: the trading calendar (--calendar-file, one YYYY-MM-DD per line; built from our daily stock data).
    A calendar expectation is stricter than the root's own life: a GENUINE end (delisting, root change) fails the check,
    the range is bisected, the days after the end come back 472 (accepted as empty) and the days up to it complete."""
    def __init__(self, massive_path, calendar_path=None):
        import duckdb
        rd = duckdb.sql(f"select root, date from '{massive_path}' order by root, date").df() \
            if massive_path and os.path.exists(massive_path) else None
        if rd is not None:
            rd['date'] = pd.to_datetime(rd.date).dt.date
        self.by_root = {} if rd is None else {r: sorted(g.date) for r, g in rd.groupby('root')}
        self.days = sorted(set(rd.date)) if rd is not None else []
        self.m0 = self.days[0] if self.days else None
        self.cal = sorted(pd.Timestamp(l.strip()).date() for l in open(calendar_path) if l.strip()) \
            if calendar_path and os.path.exists(calendar_path) else []

    def expected_last(self, root, start, end):
        if self.m0 is not None and end >= self.m0:    # the Massive-covered part of the range
            xs = [x for x in self.by_root.get(root, self.days) if max(start, self.m0) <= x <= end]
            if xs:
                return xs[-1]
            if end <= self.days[-1] and start >= self.m0:
                return None                           # Massive covers the range and never saw the root in it
        hi = end if self.m0 is None else min(end, self.m0 - pd.Timedelta(days=1).to_pytimedelta())
        xs = [x for x in self.cal if start <= x <= hi]
        return xs[-1] if xs else None


def completeness(df, root, start, end, ref):
    """⚠ 2026-11-10: the Terminal can END A RESPONSE EARLY with HTTP 200 ("Corrupt FIT data" in its log). Rows come
    ordered by DATE, so a cut-off chunk is missing its later days (GOOGL 2024Q1 saved = 2024-01-02 only), and the cut
    can REPEAT across attempts (16:25-17:00 ET: every 2024Q1 chunk cut in its first week, the same day twice) — so
    agreement between attempts proves nothing. Complete = reaches the last day MASSIVE has the root within the range."""
    exp = ref.expected_last(root, pd.Timestamp(start).date(), pd.Timestamp(end).date())
    last = df.date.max()
    if exp is not None and last < exp:
        return False, f"ends {last} < {exp}"
    return True, ''


KEEP_OUT = ['root', 'expiration', 'strike', 'right', 'open', 'high', 'low', 'close', 'volume', 'count', 'bid_size',
            'bid', 'ask_size', 'ask', 'date']
ymd = lambda d: str(d).replace('-', '')


def parse(body):
    df = pd.read_csv(io.BytesIO(body), usecols=KEEP)
    df['date'] = pd.to_datetime(df.created.str[:10]).dt.date
    df['expiration'] = pd.to_datetime(df.expiration).dt.date
    df['right'] = df.right.str[0]                     # CALL/PUT -> C/P
    df = df.drop(columns='created').rename(columns={'symbol': 'root'})
    for c in ['volume', 'count', 'bid_size', 'ask_size']:
        df[c] = df[c].astype('int64')
    return df


def write(df, out, key):
    dst = os.path.join(out, f"{key}.parquet")
    df.to_parquet(dst + '.part', index=False)
    os.replace(dst + '.part', dst)


TRIES = 3                                             # attempts per piece before it is split in half
BACKOFF = [0, 5, 15]
DAY_TRIES = 8                                         # a single day cannot be split: more attempts
DAY_BACKOFF = [0, 5, 10, 20, 30, 60, 90, 120]


def fetch_range(root, start, end, max_dte, ref, depth=0):
    """-> (df or None, why). ⚠ 2026-11-10 ThetaData degraded: a request fails (HTTP 500 or a silently cut 200) when ANY
    day in it hits "Corrupt FIT data", and single days fail ~30% at random, so a quarter-long request almost never
    succeeds. A piece that fails TRIES times is BISECTED (down to single days, which get DAY_TRIES) and reassembled."""
    s, e = pd.Timestamp(start).date(), pd.Timestamp(end).date()
    q = dict(symbol=root, expiration='*', start_date=ymd(s), end_date=ymd(e), format='csv')
    if max_dte:
        q['max_dte'] = max_dte
    single = s == e
    why = ''
    for wait in (DAY_BACKOFF if single else BACKOFF):
        time.sleep(wait)
        try:
            st, body = http('history/eod', **q)
        except Exception as ex:
            why = f"exc:{type(ex).__name__}"; continue
        if st == 472:
            return pd.DataFrame(columns=KEEP_OUT), ''
        if st != 200:
            why = f"http{st}:{body[:60].decode(errors='replace').strip()}"; continue
        df = parse(body)
        ok, why = completeness(df, root, s, e, ref) if not df.empty else (True, '')
        if ok:
            return df, ''
        # it stops early: a CUT, or the root's GENUINE end (delisting / root change; the calendar expects every day).
        # One request for the rest of the range settles it: 472 there = genuine (accept); rows there = a cut (retry).
        last = df.date.max()
        rq = dict(q, start_date=ymd(last + dt.timedelta(days=1)))
        try:
            rst, _ = http('history/eod', **rq)
        except Exception:
            rst = None
        if rst == 472:
            return df, ''
        why += f" (the rest of the range: {rst})"
    if single:
        return None, f"{s}: {why}"
    mid = s + (e - s) / 2
    left, w1 = fetch_range(root, s, mid, max_dte, ref, depth + 1)
    if left is None:
        return None, w1
    right, w2 = fetch_range(root, mid + pd.Timedelta(days=1).to_pytimedelta(), e, max_dte, ref, depth + 1)
    if right is None:
        return None, w2
    parts = [x for x in (left, right) if not x.empty]
    return (pd.concat(parts, ignore_index=True) if parts else left), ''


def pull(key, root, start, end, out, max_dte, ref):
    """-> (key, status, rows, seconds). Final statuses: ok (complete vs Massive) · nodata (472) · empty. Anything else
    is NOT final: no file is written and a rerun retries it."""
    t = time.time()
    df, why = fetch_range(root, start, end, max_dte, ref)     # every piece it returns was verified on its own
    if df is None:
        return key, f"truncated:{why}", 0, time.time() - t
    if df.empty:
        return key, 'nodata', 0, time.time() - t
    write(df, out, key)
    return key, 'ok', len(df), time.time() - t


FINAL = ('ok', 'nodata', 'empty')


def validate(out, jobs, ref, quarantine):
    """Check every saved 'ok' chunk; move the cut-off ones to `quarantine` and drop them from done.tsv (so a rerun
    re-pulls them). done.tsv is backed up first."""
    import shutil
    done_path = os.path.join(out, 'done.tsv')
    lines = [l for l in open(done_path) if l.strip()]
    shutil.copy(done_path, done_path + '.bak')
    job_of = {j[0]: j for j in jobs}
    bad = set()
    for l in lines:
        key, st = l.split('\t')[:2]
        if st not in ('ok', 'short') or key not in job_of:
            continue
        _, root, start, end = job_of[key]
        df = pd.read_parquet(os.path.join(out, f"{key}.parquet"), columns=['date'])
        ok, why = completeness(df, root, start, end, ref)
        if not ok:
            bad.add(key); print(f"  cut: {key} ({len(df):,} rows) {why}", flush=True)
    os.makedirs(quarantine, exist_ok=True)
    for key in bad:
        shutil.move(os.path.join(out, f"{key}.parquet"), os.path.join(quarantine, f"{key}.parquet"))
    with open(done_path, 'w') as f:
        f.writelines(l.replace('\tshort\t', '\tok\t') for l in lines if l.split('\t')[0] not in bad)  # a verified
                                                                                    # 'short' (old rule) is ok
    print(f"validated {sum(1 for l in lines if l.split(chr(9))[1] in ('ok', 'short')):,} ok chunks: {len(bad)} cut off -> "
          f"{quarantine} (done.tsv backed up to done.tsv.bak)", flush=True)


def quarter_chunks(start, end):
    """[start, end] (dates) cut at calendar-quarter starts -> [(chunk_start, chunk_end)]"""
    out, a = [], start
    while a <= end:
        q0 = pd.Timestamp(a.year, 3 * ((a.month - 1) // 3) + 1, 1)
        b = min(end, (q0 + pd.DateOffset(months=3) - pd.Timedelta(days=1)).date())
        out.append((a, b)); a = b + pd.Timedelta(days=1).to_pytimedelta()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--start'); ap.add_argument('--end')
    ap.add_argument('--out', required=True)
    ap.add_argument('--roots-file', help='one root per line (default: every root ThetaData lists)')
    ap.add_argument('--jobs-file', help='ROOT<TAB>YYYY-MM-DD<TAB>YYYY-MM-DD per line: per-root ranges, quarter chunks')
    ap.add_argument('--max-dte', type=int)
    ap.add_argument('--workers', type=int, default=4)
    ap.add_argument('--reference', default='data/options/massive_root_days.parquet')
    ap.add_argument('--calendar-file', default='data/options/trading_days.txt',
                    help='trading days, one per line: the completeness reference before Massive starts')
    ap.add_argument('--also-done', nargs='*', default=[],
                    help="other machines' done.tsv files (from the storage box): their final keys are skipped too")
    ap.add_argument('--order', choices=['file', 'newest'], default='file',
                    help="newest = every root's most recent chunk first, then the one before, ... (user 2026-11-11)")
    ap.add_argument('--deadline', help='UTC ISO time: no NEW request after it; in-flight ones finish (exit 3 if work remains)')
    ap.add_argument('--validate', action='store_true', help='check the saved chunks, quarantine the cut-off ones, exit')
    a = ap.parse_args()
    ref = Reference(a.reference, a.calendar_file)
    deadline = pd.Timestamp(a.deadline).timestamp() if a.deadline else None
    ymd = lambda d: str(d).replace('-', '')
    os.makedirs(a.out, exist_ok=True)
    done_path = os.path.join(a.out, 'done.tsv')
    if a.jobs_file:
        jobs = []
        for l in open(a.jobs_file):
            if l.strip():
                root, s0, e0 = l.split()
                for cs, ce in quarter_chunks(pd.Timestamp(s0).date(), pd.Timestamp(e0).date()):
                    jobs.append((f"{root}__{ymd(cs)}", root, ymd(cs), ymd(ce)))
    else:
        roots = [l.strip() for l in open(a.roots_file) if l.strip()] if a.roots_file else list_roots()
        jobs = [(r, r, ymd(a.start), ymd(a.end)) for r in roots]
    if a.validate:
        validate(a.out, jobs, ref, os.path.join(a.out + '_quarantine'))
        return
    last = {}
    if os.path.exists(done_path):                     # the LAST status per key wins; only FINAL statuses are done
        for l in open(done_path):
            if l.strip():
                k, st = l.split('\t')[:2]; last[k] = st
    done = {k for k, st in last.items() if st in FINAL}
    for f in a.also_done:                             # the other machine's work (PC by day, VPS by night)
        if os.path.exists(f):
            other = {}
            for l in open(f):
                if l.strip():
                    k, st = l.split('\t')[:2]; other[k] = st
            got = {k for k, st in other.items() if st in FINAL}
            print(f"{f}: {len(got):,} final keys done elsewhere", flush=True)
            done |= got
    todo = [j for j in jobs if j[0] not in done]
    if a.order == 'newest':                           # by chunk start, newest first; stable (roots alphabetical within)
        todo.sort(key=lambda j: j[2], reverse=True)
    print(f"{len(jobs):,} requests, {len(done):,} already done, {len(todo):,} to pull, max_dte {a.max_dte}, "
          f"{a.workers} workers -> {a.out}", flush=True)
    t0, n, rows, bad = time.time(), 0, 0, 0
    pending = iter(todo)
    stopped = False
    with open(done_path, 'a') as dl, cf.ThreadPoolExecutor(a.workers) as ex:
        inflight = set()

        def submit_next():
            nonlocal stopped
            if deadline is not None and time.time() >= deadline:
                stopped = True; return
            j = next(pending, None)
            if j is not None:
                inflight.add(ex.submit(pull, *j, a.out, a.max_dte, ref))

        for _ in range(a.workers):
            submit_next()
        while inflight:
            finished, _ = cf.wait(inflight, return_when=cf.FIRST_COMPLETED)
            for f in finished:
                inflight.discard(f)
                root, status, k, sec = f.result()
                dl.write(f"{root}\t{status}\t{k}\t{sec:.1f}\n"); dl.flush()
                n += 1; rows += k; bad += status not in FINAL
                if status not in FINAL:
                    print(f"  ! {root}: {status}", flush=True)
                if n % 250 == 0 or n == len(todo):
                    el = time.time() - t0
                    print(f"{n:,}/{len(todo):,} requests  {rows:,} rows  {rows / el:,.0f} rows/s  errors {bad}  "
                          f"elapsed {el / 60:.1f}m  ETA {el / n * (len(todo) - n) / 60:.1f}m", flush=True)
                submit_next()
    left = len(todo) - n
    print(f"{'STOPPED AT THE DEADLINE' if stopped and left else 'DONE'} {n:,} requests, {rows:,} rows, {bad} errors, "
          f"{left:,} not started, {(time.time() - t0) / 60:.1f} min", flush=True)
    sys.exit(3 if stopped and left else (1 if bad else 0))   # 0 all final · 1 some not final (rerun) · 3 deadline


if __name__ == '__main__':
    main()
