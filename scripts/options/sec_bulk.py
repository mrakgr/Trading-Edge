#!/usr/bin/env python3
"""Every company's Item 2.02 8-Ks and periodic reports (10-Q / 10-K / 10-KT), from the SEC's nightly bulk
submissions.zip (1.6 GB, all of EDGAR's per-company filing indexes) instead of one request per company.

    curl -A 'TradingEdge-research mrakgr@gmail.com' -o data/sec/submissions.zip \
        https://www.sec.gov/Archives/edgar/daily-index/bulkdata/submissions.zip
    cd research && python3 scripts/options/sec_bulk.py > logs/sec_bulk.log 2>&1
Output: data/sec/filings.parquet — cik, form, accession, filing_date, accept_utc, items, report_date
(www.sec.gov needs a contact email in the User-Agent; the user's, 2026-11-11.)"""
import argparse, json, os, time, zipfile
import concurrent.futures as cf
import pandas as pd

ap = argparse.ArgumentParser(); ap.add_argument('--zip', default='data/sec/submissions.zip')
ap.add_argument('--out', default='data/sec/filings.parquet'); ap.add_argument('--since', default='2015-01-01')
ap.add_argument('--workers', type=int, default=8)
a = ap.parse_args()
PER = ('10-Q', '10-K', '10-KT')


def block_rows(cik, b):
    out = []
    for i, f in enumerate(b.get('form', [])):
        if b['filingDate'][i] < a.since: continue
        it = b['items'][i] or ''
        if (f in ('8-K', '8-K/A') and '2.02' in it.split(',')) or f in PER:
            out.append((cik, f, b['accessionNumber'][i], b['filingDate'][i], b['acceptanceDateTime'][i], it,
                        b['reportDate'][i]))
    return out


def work(names):
    z = zipfile.ZipFile(a.zip); rows = []
    for n in names:
        try: d = json.loads(z.read(n))
        except Exception: continue
        cik = int(n[3:13])
        b = d['filings']['recent'] if 'filings' in d else d          # main file vs a -submissions-NNN page
        rows += block_rows(cik, b)
    return rows


t0 = time.time()
names = [n for n in zipfile.ZipFile(a.zip).namelist() if n.startswith('CIK') and n.endswith('.json')]
print(f'{len(names):,} files in the zip ({time.time() - t0:.0f}s)', flush=True)
chunks = [names[i::a.workers * 8] for i in range(a.workers * 8)]
rows = []
with cf.ProcessPoolExecutor(a.workers) as ex:
    for k, r in enumerate(ex.map(work, chunks), 1):
        rows += r
        if k % 8 == 0: print(f'  {k}/{len(chunks)} chunks, {len(rows):,} rows, {time.time() - t0:.0f}s', flush=True)
df = pd.DataFrame(rows, columns=['cik', 'form', 'accession', 'filing_date', 'accept_utc', 'items', 'report_date'])
df = df.drop_duplicates('accession' if False else ['cik', 'accession'])
os.makedirs(os.path.dirname(a.out), exist_ok=True); df.to_parquet(a.out, index=False)
print(f'{len(df):,} filings, {df.cik.nunique():,} CIKs -> {a.out} ({time.time() - t0:.0f}s)')
print(df.form.value_counts().to_dict())
