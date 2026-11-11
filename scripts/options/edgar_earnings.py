#!/usr/bin/env python3
"""StraddleHarvester — earnings release dates from SEC EDGAR: every 8-K (and 8-K/A) carrying Item 2.02
"Results of Operations and Financial Condition" (user 2026-11-10: the earnings source; neither ThetaData nor our
Massive plan has an earnings calendar — Benzinga / TMX are 403).

Per ticker: ticker -> CIK from MASSIVE's ticker reference (/v3/reference/tickers/X?date=D — point in time, so renamed and
delisted names resolve: FB on 2020-06-01, TWTR on 2021-06-01; www.sec.gov's company_tickers.json is current-only and
403s a generic User-Agent) -> data.sec.gov/submissions/CIK##########.json plus its older pages.
The tickers file: `TICKER` or `TICKER<TAB>YYYY-MM-DD` (the date the ticker meant that company; default today).
acceptanceDateTime is real UTC (AAPL: 20:30Z in July = 16:30 EDT, 21:30Z in January = 16:30 EST) -> converted to ET:
  session  BMO   accepted before 09:30 ET  -> the news hits day D's open
           RTH   09:30 - 16:00              -> during day D
           AMC   at/after 16:00              -> the news hits the NEXT session's open
⚠ the 8-K can trail the press release (minutes; occasionally a day). The options' IV term-structure kink is the
cross-check. ⚠ The User-Agent: a generic client name (the user's contact details only if the user asks for it).

    cd research && python3 scripts/options/edgar_earnings.py --tickers-file <file> --out data/options/earnings_8k202.parquet \
        > logs/options_edgar_earnings.log 2>&1; echo "exit $?"
"""
import argparse, json, sys, time, urllib.error, urllib.request
import os
import pandas as pd

UA = 'TradingEdge-research edgar-client'
PAUSE = 0.15                                          # SEC's fair-access limit is 10 requests/s


def get(url, ua):
    for attempt in range(4):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': ua}), timeout=60) as r:
                time.sleep(PAUSE)
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            if e.code in (403, 429, 500, 503) and attempt < 3:
                time.sleep(5 * (attempt + 1)); continue
            raise


def massive_cik(ticker, date, key):
    url = f'https://api.massive.com/v3/reference/tickers/{ticker}?' + (f'date={date}&' if date else '') + f'apiKey={key}'
    try:
        with urllib.request.urlopen(url, timeout=30) as r:
            cik = json.load(r).get('results', {}).get('cik')
    except urllib.error.HTTPError:
        cik = None
    if not cik:                                       # delisted / renamed (SQ, ATVI, X, WBD): the point-in-time lookup
        for active in ('false', 'true'):              # 404s; the ticker SEARCH finds them
            url = f'https://api.massive.com/v3/reference/tickers?ticker={ticker}&active={active}&apiKey={key}'
            with urllib.request.urlopen(url, timeout=30) as r:
                res = [x for x in json.load(r).get('results', []) if x.get('cik')]
            if res:
                cik = res[0]['cik']; break
    return int(cik) if cik else None


def item202_rows(block):
    out = []
    for i, form in enumerate(block['form']):
        if form in ('8-K', '8-K/A') and '2.02' in block['items'][i].split(','):
            out.append(dict(form=form, accession=block['accessionNumber'][i], filing_date=block['filingDate'][i],
                            accept_utc=block['acceptanceDateTime'][i], items=block['items'][i]))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--tickers-file', required=True, help='one ticker per line')
    ap.add_argument('--out', required=True)
    ap.add_argument('--since', default='2015-06-01', help='drop filings before this date')
    ap.add_argument('--user-agent', default=UA)
    a = ap.parse_args()
    tickers = sorted({tuple(l.strip().split('\t')) for l in open(a.tickers_file) if l.strip()})
    mkey = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']
    rows, missing = [], []
    for n, (t, *d) in enumerate(tickers, 1):
        t = t.upper()
        cik = massive_cik(t, d[0] if d else None, mkey)
        if cik is None:
            missing.append(t); continue
        s = get(f'https://data.sec.gov/submissions/CIK{cik:010d}.json', a.user_agent)
        got = item202_rows(s['filings']['recent'])
        for f in s['filings'].get('files', []):
            if f['filingTo'] >= a.since:
                got += item202_rows(get(f"https://data.sec.gov/submissions/{f['name']}", a.user_agent))
        rows += [dict(ticker=t, cik=cik, **r) for r in got]
        if n % 25 == 0 or n == len(tickers):
            print(f"{n}/{len(tickers)} tickers, {len(rows):,} Item 2.02 filings, {len(missing)} without a CIK", flush=True)
    df = pd.DataFrame(rows)
    df = df[df.filing_date >= a.since].copy()
    df['accept_et'] = (pd.to_datetime(df.accept_utc, utc=True).dt.tz_convert('America/New_York').dt.tz_localize(None))
    hm = df.accept_et.dt.hour * 60 + df.accept_et.dt.minute
    df['session'] = pd.cut(hm, [-1, 9 * 60 + 29, 15 * 60 + 59, 24 * 60], labels=['BMO', 'RTH', 'AMC']).astype(str)
    df['et_date'] = df.accept_et.dt.date
    df = df.drop_duplicates(['ticker', 'accession']).sort_values(['ticker', 'accept_et'])   # per TICKER: GOOG/GOOGL share a CIK
    df.to_parquet(a.out, index=False)
    print(f"wrote {len(df):,} filings on {df.ticker.nunique()} tickers -> {a.out}")
    print('session mix:', df.session.value_counts().to_dict())
    if missing:
        print(f"{len(missing)} tickers WITHOUT a CIK (Massive): {' '.join(missing[:60])}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
