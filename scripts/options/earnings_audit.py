#!/usr/bin/env python3
"""Audit earnings_scheduled.py's is_release label with EDGAR full-text search (efts.sec.gov; www.sec.gov/Archives
403s our generic User-Agent). Per sampled Item 2.02 filing: which marker phrases its documents contain.
A scheduled quarterly release almost always announces the earnings CALL/WEBCAST; preliminary results, delivery
reports and updates usually do not (and say 'preliminary', 'deliveries', 'update').
    cd research && python3 scripts/options/earnings_audit.py [--n 15] [--seed 1] > logs/options_earnings_audit.log"""
import argparse, json, time, urllib.parse, urllib.request
import pandas as pd
import os, sys; sys.path.insert(0, os.path.dirname(__file__))
from edgar_headline import headline

ap = argparse.ArgumentParser(); ap.add_argument('--sched', default='data/options/earnings_sched.parquet')
ap.add_argument('--n', type=int, default=15); ap.add_argument('--seed', type=int, default=1)
ap.add_argument('--since', default='2022-12-01')
a = ap.parse_args()
UA = 'TradingEdge-research edgar-client'
PH = ['conference call', 'webcast', 'preliminary', 'deliveries', 'quarter results', 'per diluted share', 'outlook']


def efts(q, cik, d):
    u = 'https://efts.sec.gov/LATEST/search-index?' + urllib.parse.urlencode(dict(
        q=f'"{q}"' if q else '', ciks=f'{int(cik):010d}', forms='8-K', dateRange='custom', startdt=d, enddt=d))
    for att in range(4):
        try:
            time.sleep(0.15)
            with urllib.request.urlopen(urllib.request.Request(u, headers={'User-Agent': UA}), timeout=30) as r:
                return json.load(r)['hits']['hits']
        except Exception: time.sleep(2 * (att + 1))
    return []


def row(r):
    acc, d = r.accession, r.filing_date
    hits = efts('', r.cik, d)
    desc = '; '.join(sorted({h['_source'].get('file_description') or '' for h in hits if h['_id'].startswith(acc)}))[:80]
    got = [p for p in PH if any(h['_id'].startswith(acc) for h in efts(p, r.cik, d))]
    try: hl = headline(r.cik, acc, 600).split(' | ', 1)[-1]
    except Exception as ex: hl = f'ERROR {ex!r}'
    for k in ('FOR IMMEDIATE RELEASE', 'Exhibit 99.1', 'EXHIBIT 99.1', 'Exhibit 99'):
        if k in hl: hl = hl.split(k, 1)[1]; break
    return (f"{r.ticker:6} {d} {r.session} gap={r.gap_prev if r.is_release else '-'} nwin={r.n_window} items={r['items']} | "
            f"{','.join(got)}\n        {hl.strip()[:220]}")


e = pd.read_parquet(a.sched); e = e[e.filing_date >= a.since]
S = {'NOT release (dropped)': e[~e.is_release],
     'release, competed (n_window > 1)': e[e.is_release & (e.n_window > 1)],
     'release, alone (control)': e[e.is_release & (e.n_window == 1)]}
for k, x in S.items():
    print(f'\n== {k}: {len(x):,} filings; sample {min(a.n, len(x))}', flush=True)
    for _, r in x.sample(min(a.n, len(x)), random_state=a.seed).sort_values(['ticker', 'filing_date']).iterrows():
        print(row(r), flush=True)
