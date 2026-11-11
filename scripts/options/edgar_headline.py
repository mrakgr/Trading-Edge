#!/usr/bin/env python3
"""Audit helper: the opening text of an 8-K's press-release exhibit (EX-99.x), to tell a scheduled quarterly results
release from a preliminary result / update.  usage: edgar_headline.py CIK ACCESSION [CIK ACCESSION ...]"""
import html, json, re, sys, time, urllib.request
UA = 'TradingEdge-research mrakgr@gmail.com'   # www.sec.gov/Archives requires a contact email (user 2026-11-11)


def get(url):
    time.sleep(0.2)
    with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=30) as r:
        return r.read()


def headline(cik, acc, n=350):
    base = f'https://www.sec.gov/Archives/edgar/data/{int(cik)}/{acc.replace("-", "")}'
    items = json.loads(get(f'{base}/index.json'))['directory']['item']
    docs = [i['name'] for i in items if i['name'].lower().endswith(('.htm', '.html', '.txt'))]
    ex = [d for d in docs if re.search(r'ex[-_]?99|exhibit99|ex99|dex99', d.lower())] or \
         [d for d in docs if 'index' not in d.lower() and not d.lower().startswith(acc[:10])]
    if not ex: return '(no exhibit) ' + ','.join(docs[:6])
    t = get(f'{base}/{ex[0]}').decode('utf-8', 'ignore')
    t = re.sub(r'(?is)<(script|style).*?</\1>', ' ', t); t = html.unescape(re.sub(r'<[^>]+>', ' ', t))
    return ex[0] + ' | ' + re.sub(r'\s+', ' ', t).strip()[:n]


if __name__ == '__main__':
    xs = sys.argv[1:]
    for c, a in zip(xs[::2], xs[1::2]):
        try: print(f'{c} {a}: {headline(c, a)}', flush=True)
        except Exception as ex: print(f'{c} {a}: ERROR {ex!r}', flush=True)
