#!/usr/bin/env python3
"""0DTE INTRADAY PULL (user 2026-11-16): ThetaData 1-minute option QUOTES and trade bars (OHLC) for a root's 0DTE chain
and its next expiry, one job per (ROOT, DATE).
  jobs file: ROOT<TAB>DATE<TAB>NEXT (build_0dte_jobs.py)
  per job, 4 requests: GET /v3/option/history/{quote,ohlc}?symbol&expiration={DATE,NEXT}&date=DATE&interval=1m
  -> <out>/<ROOT>__<YYYYMMDD>.quote.parquet and .ohlc.parquet (zstd; columns as ThetaData's, + 'exp' kept as expiration)
  complete = every response non-empty and its last minute >= 15:59 (12:59 on an early close, TradingEdge.Orb/Timezone.fs);
  ThetaData cuts responses at random (SH-15): a failed piece is retried (backoff), else the job is NOT final.
Same CLI and done.tsv as eod_pull.py (key <ROOT>__<YYYYMMDD>, status ok / nodata, rows, seconds), so options_runner.py
drives it with --puller intraday_pull.py: --deadline (no new request after it; exit 3 if work remains), --also-done,
--order newest; exit 0 all final, 1 some not final. --reference / --calendar-file / --max-dte / --endpoint are accepted
and ignored.
    python3 intraday_pull.py --jobs-file jobs.tsv --out data/options/intraday_0dte --workers 8 --timezone-fs ...
"""
import argparse, concurrent.futures as cf, datetime as dt, io, os, sys, time, urllib.error, urllib.parse, urllib.request
import pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

BASE = 'http://127.0.0.1:25503/v3/option/history'
BACKOFF = [0, 5, 15, 30, 60, 120]
FINAL = ('ok', 'nodata')


def http(path, **q):
    url = f"{BASE}/{path}?" + urllib.parse.urlencode(q)
    try:
        with urllib.request.urlopen(url, timeout=900) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def fetch(kind, root, exp, date, last_min):
    """-> (df or None, why). 472 = no data (empty df)."""
    why = ''
    for w in BACKOFF:
        time.sleep(w)
        try:
            st, body = http(kind, symbol=root, expiration=exp.replace('-', ''), date=date.replace('-', ''), interval='1m', format='csv')
        except Exception as ex:
            why = f'exc:{type(ex).__name__}'; continue
        if st == 472:
            return pd.DataFrame(), ''
        if st != 200:
            why = f'http{st}:{body[:60].decode(errors="replace").strip()}'; continue
        df = pd.read_csv(io.BytesIO(body))
        if df.empty:
            why = 'empty 200'; continue
        last = pd.to_datetime(df.timestamp).max()
        if last.strftime('%H:%M') < last_min:
            why = f'cut at {last:%H:%M}'; continue
        df['right'] = df['right'].str[0]
        df = df.drop(columns=['symbol'])
        return df, ''
    return None, why


def pull(key, root, date, nxt, out, early):
    t = time.time()
    last_min = '12:59' if date in early else '15:59'
    parts = {}
    for kind in ('quote', 'ohlc'):
        dfs = []
        for exp in (date, nxt):
            df, why = fetch(kind, root, exp, date, last_min)
            if df is None:
                return key, f'truncated:{kind}:{exp}:{why}', 0, time.time() - t
            if not df.empty: dfs.append(df)
        parts[kind] = pd.concat(dfs, ignore_index=True) if dfs else pd.DataFrame()
    if parts['quote'].empty:
        return key, 'nodata', 0, time.time() - t
    n = 0
    for kind, df in parts.items():
        if df.empty: continue
        dst = os.path.join(out, f'{key}.{kind}.parquet')
        df.to_parquet(dst + '.part', compression='zstd', index=False); os.replace(dst + '.part', dst)
        n += len(df)
    return key, 'ok', n, time.time() - t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--jobs-file', required=True); ap.add_argument('--out', required=True)
    ap.add_argument('--workers', type=int, default=8)
    ap.add_argument('--deadline'); ap.add_argument('--also-done', nargs='*', default=[])
    ap.add_argument('--order', choices=['file', 'newest'], default='newest')
    ap.add_argument('--timezone-fs', default='TradingEdge.Orb/Timezone.fs')
    for ign in ('--reference', '--calendar-file', '--max-dte', '--endpoint'): ap.add_argument(ign)
    a = ap.parse_args()
    from market_calendar import MarketCalendar
    early = {str(d) for d in MarketCalendar(a.timezone_fs).early}
    deadline = pd.Timestamp(a.deadline).timestamp() if a.deadline else None
    os.makedirs(a.out, exist_ok=True)
    done_path = os.path.join(a.out, 'done.tsv')
    jobs = []
    for l in open(a.jobs_file):
        if l.strip():
            r, d, n = l.split()
            jobs.append((f"{r}__{d.replace('-', '')}", r, d, n))
    done = set()
    for f in [done_path] + a.also_done:
        if os.path.exists(f):
            last = {}
            for l in open(f):
                if l.strip():
                    k, st = l.split('\t')[:2]; last[k] = st
            got = {k for k, st in last.items() if st in FINAL}
            if f != done_path: print(f'{f}: {len(got):,} final keys done elsewhere', flush=True)
            done |= got
    todo = [j for j in jobs if j[0] not in done]
    if a.order == 'newest': todo.sort(key=lambda j: j[2], reverse=True)
    print(f'{len(jobs):,} jobs, {len(done):,} already done, {len(todo):,} to pull, {a.workers} workers -> {a.out}', flush=True)
    t0, n, rows, bad = time.time(), 0, 0, 0
    pending = iter(todo); stopped = False
    with open(done_path, 'a') as dl, cf.ThreadPoolExecutor(a.workers) as ex:
        inflight = set()

        def submit_next():
            nonlocal stopped
            if deadline is not None and time.time() >= deadline:
                stopped = True; return
            j = next(pending, None)
            if j is not None: inflight.add(ex.submit(pull, *j, a.out, early))
        for _ in range(a.workers): submit_next()
        while inflight:
            fin, _ = cf.wait(inflight, return_when=cf.FIRST_COMPLETED)
            for f in fin:
                inflight.discard(f)
                key, status, k, sec = f.result()
                dl.write(f'{key}\t{status}\t{k}\t{sec:.1f}\n'); dl.flush()
                n += 1; rows += k; bad += status not in FINAL
                if status not in FINAL: print(f'  ! {key}: {status}', flush=True)
                if n % 50 == 0 or n == len(todo):
                    el = time.time() - t0
                    print(f'{n:,}/{len(todo):,} jobs  {rows:,} rows  errors {bad}  elapsed {el / 60:.1f}m  ETA {el / n * (len(todo) - n) / 60:.1f}m', flush=True)
                submit_next()
    left = len(todo) - n
    print(f"{'STOPPED AT THE DEADLINE' if stopped and left else 'DONE'} {n:,} jobs, {rows:,} rows, {bad} errors, {left:,} not started, "
          f"{(time.time() - t0) / 60:.1f} min", flush=True)
    sys.exit(3 if stopped and left else (1 if bad else 0))


if __name__ == '__main__':
    main()
