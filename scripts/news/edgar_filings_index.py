#!/usr/bin/env python3
"""News pilot — EDGAR filing METADATA for every listed common stock / ADR (2026-11-18).

One row per filing: form, filing date, acceptance time (real UTC, to the second), 8-K item codes, the size of the
complete submission, the primary document's name. No filing text — that is `edgar_filing_docs.py`.

Universe: Massive's ticker reference, types CS + ADRC, the ACTIVE tickers plus every ticker DELISTED on or after --since
(a universe of today's survivors would drop exactly the names whose last filings matter). One CIK = one issuer; its
tickers are joined in `tickers`.
Source: data.sec.gov/submissions/CIK##########.json — `filings.recent` (the last ~1,000 filings or one year, whichever
is more) plus the older pages in `filings.files` that still reach --since.

The User-Agent: $SEC_USER_AGENT if the user has set it (the SEC asks for a contact; the address is never written here),
otherwise a generic client name — data.sec.gov accepts that, www.sec.gov does not.
RESUME: the CIK list is cut into fixed chunks; a chunk whose parquet exists is skipped.

    cd research && python3 scripts/news/edgar_filings_index.py --out data/news/edgar --since 2024-01-01 \
        > logs/news_edgar_filings_index.log 2>&1; echo "exit $?"
"""
import argparse, concurrent.futures as cf, json, os, sys, threading, time, urllib.error, urllib.request
import pandas as pd

UA = os.environ.get('SEC_USER_AGENT') or 'TradingEdge-research edgar-client'
PAUSE = 0.125                                         # the SEC's fair-access limit is 10 requests/s; this is 8
_lock = threading.Lock(); _last = [0.0]


def _wait():                                          # one global pacer across the worker threads
    with _lock:
        dt_ = _last[0] + PAUSE - time.time()
        if dt_ > 0: time.sleep(dt_)
        _last[0] = time.time()


def sec(url):
    for attempt in range(5):
        try:
            _wait()
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=60) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code == 404: return None
            if attempt < 4: time.sleep(10 * (attempt + 1)); continue
            raise
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            if attempt < 4: time.sleep(10 * (attempt + 1)); continue
            raise


def massive_tickers(key, active):
    out, url = [], f'https://api.massive.com/v3/reference/tickers?market=stocks&active={active}&limit=1000'
    while url:
        req = urllib.request.Request(url, headers={'Authorization': 'Bearer ' + key})
        with urllib.request.urlopen(req, timeout=60) as r:
            d = json.load(r)
        out += d.get('results') or []
        url = d.get('next_url')
    return out


COLS = ['accessionNumber', 'filingDate', 'reportDate', 'acceptanceDateTime', 'form', 'items', 'size', 'isXBRL',
        'primaryDocument', 'primaryDocDescription']


def one_cik(cik, since):
    s = sec(f'https://data.sec.gov/submissions/CIK{cik:010d}.json')
    if not s: return None, []
    pages = [s['filings']['recent']]
    for f in s['filings'].get('files') or []:
        if f.get('filingTo', '9999') >= since:
            p = sec(f"https://data.sec.gov/submissions/{f['name']}")
            if p: pages.append(p)
    rows = []
    for p in pages:
        for i in range(len(p['form'])):
            if p['filingDate'][i] >= since:
                rows.append((cik,) + tuple(p[c][i] for c in COLS))
    issuer = dict(cik=cik, sec_name=s.get('name'), sic=s.get('sic'), sic_desc=s.get('sicDescription'),
                  category=s.get('category'), entity_type=s.get('entityType'), state_inc=s.get('stateOfIncorporation'),
                  fiscal_year_end=s.get('fiscalYearEnd'), sec_tickers=','.join(s.get('tickers') or []),
                  sec_exchanges=','.join(x or '' for x in (s.get('exchanges') or [])))
    return issuer, rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--since', default='2024-01-01')
    ap.add_argument('--chunk', type=int, default=250)
    ap.add_argument('--workers', type=int, default=3)
    a = ap.parse_args()
    os.makedirs(os.path.join(a.out, 'filings'), exist_ok=True)
    os.makedirs(os.path.join(a.out, 'issuers'), exist_ok=True)
    print('declared SEC contact:', 'yes' if os.environ.get('SEC_USER_AGENT') else 'no (generic client name)')

    uni_path = os.path.join(a.out, 'universe.parquet')
    if os.path.exists(uni_path):
        uni = pd.read_parquet(uni_path)
    else:
        key = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']
        act, dead = massive_tickers(key, 'true'), massive_tickers(key, 'false')
        print(f'massive tickers: {len(act)} active, {len(dead)} inactive'); sys.stdout.flush()
        keep, nocik = [], 0
        for t in act + dead:
            if t.get('type') not in ('CS', 'ADRC'): continue
            dl = (t.get('delisted_utc') or '')[:10]
            if not t.get('active', True) and dl < a.since: continue
            if not t.get('cik'): nocik += 1; continue
            keep.append(dict(cik=int(t['cik']), ticker=t['ticker'], type=t['type'], name=t.get('name'),
                             active=bool(t.get('active', True)), delisted=dl or None,
                             exchange=t.get('primary_exchange')))
        uni = pd.DataFrame(keep)
        uni.to_parquet(uni_path, index=False)
        print(f'universe: {len(uni)} tickers ({int(uni.active.sum())} active, {int((~uni.active).sum())} delisted since '
              f'{a.since}), {uni.cik.nunique()} CIKs; {nocik} CS/ADRC tickers without a CIK dropped')
    ciks = sorted(uni.cik.unique().tolist())
    print(f'{len(ciks)} CIKs, since {a.since}'); sys.stdout.flush()

    t0, nrows = time.time(), 0
    chunks = [ciks[i:i + a.chunk] for i in range(0, len(ciks), a.chunk)]
    with cf.ThreadPoolExecutor(a.workers) as ex:
        for k, chunk in enumerate(chunks):
            fp = os.path.join(a.out, 'filings', f'chunk_{k:04d}.parquet')
            if os.path.exists(fp): continue
            res = list(ex.map(lambda c: one_cik(c, a.since), chunk))
            rows = [r for _, rs in res for r in rs]
            df = pd.DataFrame(rows, columns=['cik', 'accession', 'filing_date', 'report_date', 'acceptance_utc', 'form',
                                             'items', 'size', 'is_xbrl', 'primary_doc', 'primary_desc'])
            df['acceptance_utc'] = pd.to_datetime(df['acceptance_utc'], utc=True, errors='coerce')
            pd.DataFrame([i for i, _ in res if i]).to_parquet(os.path.join(a.out, 'issuers', f'chunk_{k:04d}.parquet'),
                                                              index=False)
            df.to_parquet(fp + '.tmp', index=False); os.replace(fp + '.tmp', fp)
            nrows += len(df)
            miss = sum(i is None for i, _ in res)
            el = time.time() - t0
            print(f'chunk {k + 1}/{len(chunks)}: {len(df)} filings ({miss} CIKs without submissions), total {nrows}, '
                  f'{el / 60:.1f} min'); sys.stdout.flush()
    print('done')


if __name__ == '__main__':
    main()
