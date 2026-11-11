#!/usr/bin/env python3
"""Which Item 2.02 8-Ks are the SCHEDULED quarterly earnings release (user 2026-11-11)?

Item 2.02 also covers preliminary results, pre-announcements, delivery/production updates, guidance changes
(SH-20 audit: 7-8 of 12 'no event priced' events were such). Rule, per CIK:
  for each periodic report (10-Q / 10-K / 10-KT, + amendments ignored) with period end P, filed F:
  the RELEASE for that period = the LAST Item 2.02 filing with P < filing date <= F; if there is none, the FIRST one
  in (F, F + 3 days]. (An extra 2.02 the day after the release - supplemental slides, a corrected exhibit - is
  common: SAVE 2023-02-07, UAMY 2026-05-15 - so the slack is only a fallback.)
  (prelims come first, between P and the real release; the 10-Q/10-K follows the release.)
Labelling uses the 10-Q date (after the event) only to say WHICH 8-K was the results release; live, an earnings
calendar announces that date weeks ahead. NOT lookahead in the trading sense; unscheduled 8-Ks are what we drop.
Also: gap_prev = days since the previous RELEASE of the same CIK (cadence check).

Input: the md500k Item 2.02 file (point-in-time CIKs) + cik_spans from pead.py's resolve_reused (reused tickers).
    cd research && python3 scripts/options/earnings_scheduled.py --roots-from data/options/eod_universe \
        > logs/options_earnings_sched.log 2>&1
Output: data/options/earnings_sched.parquet (every Item 2.02 filing of the selected tickers + is_release, gap_prev)
"""
import argparse, glob, json, os, sys, threading, time, urllib.error, urllib.request
import concurrent.futures as cf
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--earnings', default='data/options/pead/earnings_md500k.parquet')
ap.add_argument('--spans', default='data/options/pead_md500k/cik_spans.parquet')
ap.add_argument('--roots-from', default='', help='dir of ROOT__*.parquet files: restrict to these tickers')
ap.add_argument('--cache', default='data/options/pead/edgar_cache/periodic')
ap.add_argument('--out', default='data/options/earnings_sched.parquet')
ap.add_argument('--workers', type=int, default=4)
ap.add_argument('--bulk', default='', help='periodic reports from sec_bulk.py output (data/sec/filings.parquet), no requests')
a = ap.parse_args()
UA = 'TradingEdge-research edgar-client'; PAUSE = 0.15
_l = threading.Lock(); _last = [0.0]


def get(url):
    for att in range(5):
        with _l:
            w = _last[0] + PAUSE - time.time()
            if w > 0: time.sleep(w)
            _last[0] = time.time()
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=30) as r:
                return json.load(r)
        except urllib.error.HTTPError as ex:
            if ex.code == 404: return None
            time.sleep(2 * (att + 1))
        except Exception: time.sleep(2 * (att + 1))
    raise RuntimeError(url)


def periodic_rows(b):
    return [dict(form=f, filing_date=b['filingDate'][i], period=b['reportDate'][i])
            for i, f in enumerate(b['form']) if f in ('10-Q', '10-K', '10-KT')]


def periodic(cik):
    fn = f'{a.cache}/{cik}.json'
    if os.path.exists(fn): return json.load(open(fn))
    s = get(f'https://data.sec.gov/submissions/CIK{cik:010d}.json')
    got = periodic_rows(s['filings']['recent']) if s else []
    for f in (s['filings'].get('files', []) if s else []):
        if f['filingTo'] >= '2015-01-01': got += periodic_rows(get(f"https://data.sec.gov/submissions/{f['name']}"))
    json.dump(got, open(fn + '.tmp', 'w')); os.replace(fn + '.tmp', fn)
    return got


os.makedirs(a.cache, exist_ok=True)
e = pd.read_parquet(a.earnings)
if os.path.exists(a.spans):  # same rule as pead.py resolve_reused: a filing counts only while its CIK held the ticker
    sp = pd.read_parquet(a.spans); keep = np.ones(len(e), bool); fd = pd.to_datetime(e.et_date)
    for r in sp.itertuples():
        m = (e.ticker == r.ticker).values
        keep &= ~(m & (e.cik == r.cik_old).values & (fd >= r.switch).values)
        keep &= ~(m & (e.cik == r.cik_new).values & (fd < r.switch).values)
    print(f'reused-ticker spans {len(sp)}: dropped {(~keep).sum()} filings', flush=True); e = e[keep]
else: print(f'⚠ no {a.spans}: reused tickers NOT resolved', flush=True)
if a.roots_from:
    roots = {os.path.basename(f).split('__')[0] for f in glob.glob(f'{a.roots_from}/*.parquet')}
    e = e[e.ticker.str.replace('.', '', regex=False).isin(roots)]   # option roots drop the dot: BRK.B -> BRKB
e = e.drop(columns=[c for c in ['query_date'] if c in e]).drop_duplicates(['ticker', 'accession']).copy()
ciks = sorted(e.cik.unique()); t0 = time.time()
print(f'{len(e):,} Item 2.02 filings, {e.ticker.nunique()} tickers, {len(ciks)} CIKs', flush=True)
P = {}
if a.bulk:
    B = pd.read_parquet(a.bulk); B = B[B.form.isin(['10-Q', '10-K', '10-KT']) & B.cik.isin(ciks)]
    P = {c: g.rename(columns={'report_date': 'period'})[['form', 'filing_date', 'period']].to_dict('records') for c, g in B.groupby('cik')}
    ciks_ = []
else: ciks_ = ciks
with cf.ThreadPoolExecutor(a.workers) as ex:
    for n, (c, g) in enumerate(zip(ciks_, ex.map(periodic, ciks_)), 1):
        P[c] = g
        if n % 200 == 0: print(f'  {n}/{len(ciks)} CIKs {time.time() - t0:.0f}s', flush=True)
e['fd'] = pd.to_datetime(e.filing_date); e['is_release'] = False; e['period'] = pd.NaT; e['per_form'] = None
e['n_window'] = 0; picks = {}
for c, g in e.groupby('cik'):
    pr = pd.DataFrame(P.get(c, []))
    if pr.empty: continue
    pr = pr[pr.period.notna()]
    pr = pr[pr.period.astype(bool)].assign(F=lambda x: pd.to_datetime(x.filing_date), Pd=lambda x: pd.to_datetime(x.period))
    pr = pr.sort_values('F').drop_duplicates('Pd')            # the first report of each period (amendments skipped)
    u = g.drop_duplicates('accession').sort_values(['fd', 'accept_et'])   # one row per filing (GOOG/GOOGL share a CIK)
    fd = u.set_index('accession').fd
    for r in pr.itertuples():
        w = fd[(fd > r.Pd) & (fd <= r.F)]; pick = w.index[-1] if len(w) else None
        if pick is None:
            w = fd[(fd > r.F) & (fd <= r.F + pd.Timedelta(days=3))]; pick = w.index[0] if len(w) else None
        if pick is not None: picks[pick] = (r.Pd, r.form, len(w))
e['is_release'] = e.accession.isin(picks)
k_ = e.accession.map(lambda x: picks.get(x, (pd.NaT, None, 0)))
e['period'] = pd.to_datetime([v[0] for v in k_]); e['per_form'] = [v[1] for v in k_]; e['n_window'] = [v[2] for v in k_]
e = e.sort_values(['ticker', 'fd'])
rel = e[e.is_release]
e['gap_prev'] = np.nan
e.loc[rel.index, 'gap_prev'] = rel.groupby(['cik', 'ticker']).fd.diff().dt.days
e.drop(columns='fd').to_parquet(a.out, index=False)
print(f'releases {e.is_release.sum():,} of {len(e):,} filings ({100 * e.is_release.mean():.1f}%) -> {a.out}', flush=True)
print('release gap_prev quantiles:', rel.groupby('cik').fd.diff().dt.days.quantile([.01, .05, .25, .5, .75, .95, .99]).to_dict())
print('CIKs without periodic reports:', sum(1 for c in ciks if not P.get(c)))
