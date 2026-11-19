#!/usr/bin/env python3
"""News pilot — EDGAR filing TEXT for the news-carrying forms (2026-11-18).

For every filing of the selected forms in [--from, --to] listed by `edgar_filings_index.py`:
  1. the filing's `<accession>-index.htm` (the document table: seq, description, document, type, size),
  2. the PRIMARY document (the 8-K / 6-K / 424B itself) and every EX-99.* exhibit (the press release rides as EX-99.1),
     .htm / .html / .txt only, at most --max-mb each. Everything else (graphics, XBRL, PDFs) is listed, not fetched.
The raw bytes are kept as fetched (`html`, zstd in the parquet) — text extraction is a later, repeatable step.

Output: <out>/docs/<filing_date>/part_NNN.parquet, --part filings each, one row per document.
RESUME: a part whose parquet exists is skipped (the filing list is a pure function of the metadata + the flags).

www.sec.gov refuses an undeclared client: the user's contact goes in $SEC_USER_AGENT (set by the user in the shell
profile; the address is never written here). The run aborts at start without it.

    cd research && python3 scripts/news/edgar_filing_docs.py --meta data/news/edgar --from 2026-05-18 --to 2026-11-17 \
        > logs/news_edgar_filing_docs.log 2>&1; echo "exit $?"
"""
import argparse, concurrent.futures as cf, glob, os, re, sys, threading, time, urllib.error, urllib.request
import pandas as pd

UA = os.environ.get('SEC_USER_AGENT')
PAUSE = 0.125                                         # the SEC's fair-access limit is 10 requests/s; this is 8
_lock = threading.Lock(); _last = [0.0]
FORMS = ['8-K', '8-K/A', '6-K', '6-K/A', '424B1', '424B3', '424B4', '424B5', '424B7']
ROOT = 'https://www.sec.gov'


def _wait():                                          # one global pacer across the worker threads
    with _lock:
        dt_ = _last[0] + PAUSE - time.time()
        if dt_ > 0: time.sleep(dt_)
        _last[0] = time.time()


FAILED = object()                                     # a document that stayed unreachable: recorded, never fatal


def fetch(url):
    """bytes; None on 404; FAILED after four tries (~70 s for a 5xx). A 403/429 is the SEC's rate block (10 minutes): back
    off long. ⚠ 2026-11-18: the first run died at 135 min on ONE document's HTTP 504 (six tries in 4 minutes, then a
    raise) — a single unreachable document must cost one `error` row, not the run."""
    for attempt in range(4):
        try:
            _wait()
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=60) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code == 404: return None
            code = e.code
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError):
            code = 0
        # ⚠ every retry is LOGGED: the resumed run of 2026-11-18 crawled at a quarter of the first run's pace with
        # nothing in the log to say why (silent backoff sleeps up to 8 minutes inside the workers).
        pause = 660 if code in (403, 429) else min(10 * 2 ** attempt, 60)
        print(f'  retry {attempt + 1} (code {code}, sleep {pause}s): {url.rsplit("/", 1)[-1]}'); sys.stdout.flush()
        if attempt < 3: time.sleep(pause)
    print(f'FAILED after 4 tries (last code {code}): {url}'); sys.stdout.flush()
    return FAILED


ROW = re.compile(r'<tr[^>]*>(.*?)</tr>', re.S)
CELL = re.compile(r'<td[^>]*>(.*?)</td>', re.S)
HREF = re.compile(r'href="([^"]+)"')
TAG = re.compile(r'<[^>]+>')


def doc_table(index_html):
    out = []
    for row in ROW.findall(index_html):
        cells = CELL.findall(row)
        if len(cells) != 5: continue
        href = HREF.search(cells[2])
        if not href: continue
        path = href.group(1).replace('/ix?doc=', '')
        clean = [TAG.sub('', c).replace('&nbsp;', ' ').strip() for c in cells]
        size = int(clean[4]) if clean[4].isdigit() else None
        out.append(dict(seq=clean[0], description=clean[1], doc_name=path.rsplit('/', 1)[-1], doc_type=clean[3],
                        size_listed=size, path=path))
    return out


def one_filing(f, max_bytes):
    acc = f.accession
    base = dict(cik=f.cik, accession=acc, form=f.form, filing_date=f.filing_date, acceptance_utc=f.acceptance_utc)
    idx = fetch(f"{ROOT}/Archives/edgar/data/{f.cik}/{acc.replace('-', '')}/{acc}-index.htm")
    if idx is None or idx is FAILED:
        rows = [dict(base, seq='', description='', doc_name='', doc_type='', size_listed=None,
                     status='no_index' if idx is None else 'index_error', html=None)]
        # ⚠ some index pages answer 502 on every try (2026-11-18: ~0.3% of filings, each one a stall). The metadata
        # already names the primary document, so that one is fetched directly; only the exhibits are lost, and the
        # `index_error` row marks the filing for a later pass.
        if idx is FAILED and f.primary_doc:
            body = fetch(f"{ROOT}/Archives/edgar/data/{f.cik}/{acc.replace('-', '')}/{f.primary_doc}")
            if body is not None and body is not FAILED:
                rows.append(dict(base, seq='1', description='(index unavailable: primary document named by the metadata)',
                                 doc_name=f.primary_doc, doc_type=f.form, size_listed=len(body), status='ok', html=body))
        return rows
    rows = []
    for d in doc_table(idx.decode('utf8', 'replace')):
        path = d.pop('path')
        want = (d['doc_type'] == f.form or d['doc_type'].startswith('EX-99')) and d['doc_type'] != ''
        ext = d['doc_name'].rsplit('.', 1)[-1].lower()
        if not want: status, body = 'listed', None
        elif ext not in ('htm', 'html', 'txt'): status, body = 'skipped_ext', None
        elif (d['size_listed'] or 0) > max_bytes: status, body = 'skipped_size', None
        else:
            body = fetch(ROOT + path)
            if body is FAILED: status, body = 'error', None
            else: status = 'ok' if body is not None else 'missing'
        rows.append(dict(base, **d, status=status, html=body))
    return rows or [dict(base, seq='', description='', doc_name='', doc_type='', size_listed=None, status='empty_index', html=None)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--meta', required=True, help='the directory edgar_filings_index.py wrote')
    ap.add_argument('--from', dest='lo', required=True)
    ap.add_argument('--to', dest='hi', required=True)
    ap.add_argument('--out', default='', help='default: <meta>/docs')
    ap.add_argument('--part', type=int, default=100)
    ap.add_argument('--workers', type=int, default=3)
    ap.add_argument('--max-mb', type=float, default=8.0)
    ap.add_argument('--limit', type=int, default=0, help='test: only the first N filings')
    ap.add_argument('--repair', action='store_true',
                    help='refetch only the filings that carry an `error` / `index_error` row; rewrites their parts')
    a = ap.parse_args()
    if not UA:
        print('SEC_USER_AGENT is not set — www.sec.gov refuses an undeclared client'); sys.exit(2)
    out = a.out or os.path.join(a.meta, 'docs')
    if a.repair:
        repair(a, out); return
    fl = pd.concat([pd.read_parquet(p) for p in sorted(glob.glob(os.path.join(a.meta, 'filings', '*.parquet')))])
    fl = fl[(fl.filing_date >= a.lo) & (fl.filing_date <= a.hi) & fl.form.isin(FORMS)]
    fl = fl.drop_duplicates('accession').sort_values(['filing_date', 'accession']).reset_index(drop=True)
    if a.limit: fl = fl.head(a.limit)
    print(f'{len(fl)} filings {a.lo}..{a.hi}:', fl.form.value_counts().to_dict()); sys.stdout.flush()

    parts = []
    for day, g in fl.groupby('filing_date', sort=True):
        for k in range(0, len(g), a.part):
            parts.append((day, k // a.part, g.iloc[k:k + a.part]))
    t0, done_f, done_b, todo = time.time(), 0, 0, sum(len(g) for _, _, g in parts)
    with cf.ThreadPoolExecutor(a.workers) as ex:
        for day, k, g in parts:
            fp = os.path.join(out, day, f'part_{k:03d}.parquet')
            if os.path.exists(fp): todo -= len(g); continue
            os.makedirs(os.path.dirname(fp), exist_ok=True)
            rows = [r for rs in ex.map(lambda f: one_filing(f, a.max_mb * 1e6), list(g.itertuples(index=False))) for r in rs]
            df = pd.DataFrame(rows)
            df['size_listed'] = df['size_listed'].astype('Int64')
            df.to_parquet(fp + '.tmp', index=False, compression='zstd'); os.replace(fp + '.tmp', fp)
            done_f += len(g); done_b += sum(len(b) for b in df.html if b is not None)
            el = time.time() - t0
            eta = el / done_f * (todo - done_f) / 60
            print(f'{day} part {k}: {len(g)} filings, {int((df.status == "ok").sum())} docs fetched | '
                  f'{done_f}/{todo} filings, {done_b / 1e9:.2f} GB raw, {el / 60:.0f} min, ETA {eta:.0f} min')
            sys.stdout.flush()
    print('done')


def repair(a, out):
    """A filing with an `error` or `index_error` row is fetched again whole; its rows in the part are replaced."""
    meta = pd.concat([pd.read_parquet(p) for p in sorted(glob.glob(os.path.join(a.meta, 'filings', '*.parquet')))])
    meta = meta.drop_duplicates('accession').set_index('accession')
    fixed = still = 0
    for fp in sorted(glob.glob(os.path.join(out, '*', 'part_*.parquet'))):
        df = pd.read_parquet(fp)
        bad = df.loc[df.status.isin(['error', 'index_error']), 'accession'].unique().tolist()
        if not bad: continue
        new = []
        for acc in bad:
            f = meta.loc[acc]
            rows = one_filing(f._replace(accession=acc) if hasattr(f, '_replace') else
                              pd.Series(dict(f, accession=acc)), a.max_mb * 1e6)
            ok = not any(r['status'] in ('error', 'index_error') for r in rows)
            fixed += ok; still += not ok
            print(f'{acc}: {"fixed" if ok else "still failing"} ({sum(r["status"] == "ok" for r in rows)} docs)')
            new += rows
        df = pd.concat([df[~df.accession.isin(bad)], pd.DataFrame(new)], ignore_index=True)
        df['size_listed'] = df['size_listed'].astype('Int64')
        df.to_parquet(fp + '.tmp', index=False, compression='zstd'); os.replace(fp + '.tmp', fp)
    print(f'repair: {fixed} fixed, {still} still failing'); print('done')


if __name__ == '__main__':
    main()
