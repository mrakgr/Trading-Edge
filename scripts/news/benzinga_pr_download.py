#!/usr/bin/env python3
"""Benzinga PR Feed — download the press-release archive, one file per calendar day (2026-11-24).

`api.benzinga.com/api/v2/news?channels=Press Releases&displayOutput=full`, 100 items/page, paged per day
(dateFrom = dateTo = D). Benzinga's limit is 1 request/s — every request start is spaced >= --interval s.

⚠ 2026-11-24, the trial key: history is capped at a ROLLING 1 year — any dateFrom before (now − 365 d) is silently
clamped to it (2010 / 2018 / 2024 queries returned the identical page). Hence the default order is OLDEST FIRST: the
oldest days are the ones about to fall out of the window. A day whose items all fall outside [D, D+1] is flagged
`clamped` in the manifest, not hidden.
⚠ `created` carries "-0400" in November (EST is -0500) — verify against the tape before trusting the hour.

Output (gitignored): <out>/<D>.jsonl.zst — the raw API items, one JSON object per line, as received;
<out>/manifest.csv — day, n_items, n_pages, bytes, first_created, last_created, flag. A day is written atomically
(tmp + rename) and a day already in the manifest is skipped, so a killed run resumes where it stopped.
A later run with --start <day> extends the archive (the trial window rolls forward).

The key is read from ~/.config/benziga-trial/credentials (KEY=); it is never printed or logged.

    cd research && python3 scripts/news/benzinga_pr_download.py > logs/news_benzinga_pr_download.log 2>&1; echo "exit $?" >> logs/news_benzinga_pr_download.log
"""
import argparse, csv, json, os, sys, time, urllib.error, urllib.parse, urllib.request
from datetime import date, datetime, timedelta
from email.utils import parsedate_to_datetime
from zoneinfo import ZoneInfo

import zstandard

API = 'https://api.benzinga.com/api/v2/news'
ET = ZoneInfo('America/New_York')
PAGE = 100


def read_key(path):
    with open(os.path.expanduser(path)) as f:
        for line in f:
            if line.startswith('KEY='): return line.strip().split('=', 1)[1]
    sys.exit(f'no KEY= in {path}')


class Client:
    def __init__(self, key, interval):
        self.key, self.interval, self.t_last, self.n = key, interval, 0.0, 0

    def page(self, d, k):
        q = dict(token=self.key, pageSize=PAGE, page=k, displayOutput='full', channels='Press Releases',
                 dateFrom=d, dateTo=d)
        url = f'{API}?{urllib.parse.urlencode(q)}'
        for attempt in range(6):
            wait = self.t_last + self.interval - time.time()
            if wait > 0: time.sleep(wait)
            self.t_last = time.time(); self.n += 1
            try:
                req = urllib.request.Request(url, headers={'accept': 'application/json', 'User-Agent': 'news-research'})
                with urllib.request.urlopen(req, timeout=120) as r:
                    return json.loads(r.read())
            except urllib.error.HTTPError as ex:
                if ex.code in (401, 403): sys.exit(f'{d} page {k}: HTTP {ex.code} — key refused, stopping')
                err = f'HTTP {ex.code}'
            except Exception as ex:                          # timeouts, resets, bad JSON
                err = type(ex).__name__
            back = min(300, 5 * 2 ** attempt)
            print(f'  {d} page {k}: {err} — retry {attempt + 1} in {back} s', flush=True)
            time.sleep(back)
        raise RuntimeError(f'{d} page {k}: gave up after 6 tries')


def parse_created(x):
    try: return parsedate_to_datetime(x['created'])
    except Exception: return None


def created_day(x):
    t = parse_created(x)
    return t.astimezone(ET).date() if t else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default='data/news/benzinga/pr')
    ap.add_argument('--cred', default='~/.config/benziga-trial/credentials')
    ap.add_argument('--start', help='first day (default: today − 365, ET)')
    ap.add_argument('--end', help='last day (default: yesterday, ET)')
    ap.add_argument('--newest-first', action='store_true')
    ap.add_argument('--interval', type=float, default=1.05, help='seconds between request starts')
    ap.add_argument('--max-pages', type=int, default=300, help='per day; a day hitting it is flagged')
    a = ap.parse_args()
    today = datetime.now(ET).date()
    start = date.fromisoformat(a.start) if a.start else today - timedelta(days=365)
    end = date.fromisoformat(a.end) if a.end else today - timedelta(days=1)
    days = [start + timedelta(days=i) for i in range((end - start).days + 1)]
    if a.newest_first: days.reverse()
    os.makedirs(a.out, exist_ok=True)
    man = os.path.join(a.out, 'manifest.csv')
    done = set()
    if os.path.exists(man):
        with open(man) as f: done = {r['day'] for r in csv.DictReader(f)}
    todo = [d for d in days if d.isoformat() not in done]
    print(f'{len(days)} days {days[0]} .. {days[-1]}; {len(done)} already in the manifest; {len(todo)} to fetch', flush=True)
    cl = Client(read_key(a.cred), a.interval)
    cctx = zstandard.ZstdCompressor(level=9)
    t0, n_items_all = time.time(), 0
    new_man = not os.path.exists(man)
    with open(man, 'a', newline='') as mf:
        mw = csv.writer(mf)
        if new_man: mw.writerow(['day', 'n_items', 'n_pages', 'bytes', 'first_created', 'last_created', 'flag'])
        for i, d in enumerate(todo):
            ds, items, ids, flag, k = d.isoformat(), [], set(), '', 0
            while True:
                got = cl.page(ds, k)
                if not isinstance(got, list):
                    flag = f'non-list response: {str(got)[:80]}'; break
                fresh = [x for x in got if x.get('id') not in ids]
                if got and not fresh:                         # the API repeated a page: a depth limit
                    flag = f'repeated page {k}'; break
                for x in fresh: ids.add(x.get('id'))
                items += fresh; k += 1
                if len(got) < PAGE: break
                if k >= a.max_pages:
                    flag = f'max pages {k}'; break
            cd = [c for c in map(created_day, items) if c]
            if items and cd and not any(c in (d, d + timedelta(days=1), d - timedelta(days=1)) for c in cd):
                flag = (flag + ' ' if flag else '') + 'clamped'
            raw = '\n'.join(json.dumps(x, ensure_ascii=False) for x in items).encode()
            path = os.path.join(a.out, f'{ds}.jsonl.zst')
            with open(path + '.tmp', 'wb') as f: f.write(cctx.compress(raw))
            os.replace(path + '.tmp', path)
            ts = sorted(t for t in (parse_created(x) for x in items) if t)     # the API does not return items in time order
            mw.writerow([ds, len(items), k, len(raw), ts[0].isoformat() if ts else '',
                         ts[-1].isoformat() if ts else '', flag]); mf.flush()
            n_items_all += len(items)
            el = time.time() - t0
            eta = el / (i + 1) * (len(todo) - i - 1)
            print(f'{ds}: {len(items):5d} items, {k:3d} pages, {len(raw) / 1e6:6.1f} MB raw {flag} | '
                  f'{i + 1}/{len(todo)} days, {n_items_all} items, {cl.n} requests, {el / 60:.0f} min, '
                  f'ETA {eta / 60:.0f} min', flush=True)
    print('done', flush=True)


if __name__ == '__main__':
    main()
