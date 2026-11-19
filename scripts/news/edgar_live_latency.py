#!/usr/bin/env python3
"""News pilot — how fast does a filing reach the public? (2026-11-19)

Records, per filing, the FIRST time each public EDGAR source showed it; compared with the acceptance time
(`updated` in the getcurrent feed, ET to the second; data.sec.gov's acceptanceDateTime afterwards) this is the floor
any EDGAR-driven trade starts from. Two sources, polled in parallel:

  getcurrent  www.sec.gov/cgi-bin/browse-edgar?action=getcurrent (Atom, newest 100 entries) — three feeds: all forms,
              8-K only, 6-K only (in the 16:00 ET burst 100 entries of the all-forms feed scroll by in under a minute).
              ⚠ measured 2026-11-19: a response takes 0.2–27 s (the endpoint itself, sequential requests) — so every
              feed runs in its OWN thread, and each sighting keeps t_send AND t_recv: the content is at least as new as
              t_send and was in hand at t_recv.
  efts        efts.sec.gov/LATEST/search-index (full-text search; 0.1–0.5 s) — today's 8-K / 6-K, paged 100 at a time;
              one hit per DOCUMENT, keyed here by accession (adsh). Its indexing lag is what is measured.

Output: <out>/sightings_<ET date>.csv — one row per (source, accession, cik) first sighting; <out>/polls_<ET date>.csv.
www.sec.gov needs $SEC_USER_AGENT (set by the user; never printed). The SEC's limit is 10 requests/s; this makes ~3.

    cd research && python3 scripts/news/edgar_live_latency.py --until 20:05 > logs/news_edgar_live_latency.log 2>&1
"""
import argparse, csv, json, os, queue, re, sys, threading, time, urllib.error, urllib.request
from datetime import datetime
from zoneinfo import ZoneInfo

UA = os.environ.get('SEC_USER_AGENT')
ET = ZoneInfo('America/New_York')
GC = 'https://www.sec.gov/cgi-bin/browse-edgar?action=getcurrent&company=&dateb=&owner=include&start=0&count=100&output=atom&type='
EFTS = 'https://efts.sec.gov/LATEST/search-index?forms={forms}&startdt={d}&enddt={d}&from={k}'
# ⚠ 2026-11-19: efts mangles a long `forms` list (17 forms → 5 hits, or HTTP 500); a short one works and '8-K,6-K' also
# returns their amendments. The other news forms are covered by the getcurrent all-forms feed.
NEWS = '8-K,6-K'
ENTRY = re.compile(r'<entry>(.*?)</entry>', re.S)
TITLE = re.compile(r'<title>(.*?)</title>', re.S)
UPD = re.compile(r'<updated>(.*?)</updated>')
ACC = re.compile(r'accession-number=([0-9-]+)')
FORM = re.compile(r'label="form type" term="([^"]*)"')
CIKR = re.compile(r'\((\d{10})\) \((\w[\w ]*)\)\s*$')
out_q = queue.Queue()                                 # every thread → the one writer


def get(url, timeout=60):
    t_send = time.time()
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=timeout) as r:
            return t_send, time.time(), r.status, r.headers.get('Date', ''), r.read().decode('utf8', 'replace')
    except urllib.error.HTTPError as ex:
        return t_send, time.time(), ex.code, '', None
    except Exception as ex:
        return t_send, time.time(), type(ex).__name__, '', None


def backoff(status, src):
    if status in (403, 429):
        out_q.put(('log', f'{src}: HTTP {status} — backing off 10 min')); time.sleep(600)


def getcurrent(name, typ, stop, pause):
    while time.time() < stop:
        ts, tr, st, sd, b = get(GC + typ)
        n = 0
        if b:
            for e in ENTRY.findall(b):
                acc, upd, ti, fm = ACC.search(e), UPD.search(e), TITLE.search(e), FORM.search(e)
                if not (acc and upd and ti): continue
                m = CIKR.search(ti.group(1).strip())
                out_q.put(('s', [f'gc_{name}', acc.group(1), m.group(1) if m else '', m.group(2) if m else '',
                                 fm.group(1) if fm else '', upd.group(1), f'{ts:.3f}', f'{tr:.3f}'])); n += 1
        out_q.put(('p', [f'gc_{name}', f'{ts:.3f}', f'{tr:.3f}', sd, st, n]))
        backoff(st, name); time.sleep(pause)


def efts(stop, pause):
    while time.time() < stop:
        d = datetime.now(ET).strftime('%Y-%m-%d'); k, total = 0, 1
        while k < total and time.time() < stop:
            ts, tr, st, sd, b = get(EFTS.format(forms=NEWS.replace(' ', '%20'), d=d, k=k))
            n = 0
            if b:
                try:
                    hits = json.loads(b)['hits']; total = hits['total']['value']
                    for h in hits['hits']:
                        s = h['_source']
                        for cik in s.get('ciks') or ['']:
                            out_q.put(('s', ['efts', s.get('adsh', ''), cik, '', s.get('form', ''), s.get('file_date', ''),
                                             f'{ts:.3f}', f'{tr:.3f}'])); n += 1
                except (ValueError, KeyError) as ex:
                    out_q.put(('log', f'efts parse: {ex}')); total = 0
            else: total = 0
            out_q.put(('p', ['efts', f'{ts:.3f}', f'{tr:.3f}', sd, st, n]))
            backoff(st, 'efts'); k += 100
            time.sleep(0.2)
        time.sleep(pause)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default='data/news/live')
    ap.add_argument('--until', default='20:05', help='ET wall-clock time to stop')
    ap.add_argument('--pause', type=float, default=1.0, help='seconds between one thread\'s requests')
    a = ap.parse_args()
    if not UA: print('SEC_USER_AGENT is not set'); sys.exit(2)
    os.makedirs(a.out, exist_ok=True)
    day = datetime.now(ET).strftime('%Y-%m-%d')
    stop = datetime.strptime(f'{day} {a.until}', '%Y-%m-%d %H:%M').replace(tzinfo=ET).timestamp()
    fs, fp = os.path.join(a.out, f'sightings_{day}.csv'), os.path.join(a.out, f'polls_{day}.csv')
    seen = set()
    if os.path.exists(fs):
        with open(fs) as f: seen = {(r['source'], r['accession'], r['cik']) for r in csv.DictReader(f)}
    new_s, new_p = not os.path.exists(fs), not os.path.exists(fp)
    ths = [threading.Thread(target=getcurrent, args=(n, t, stop, a.pause), daemon=True)
           for n, t in (('all', ''), ('8k', '8-K'), ('6k', '6-K'))]
    ths.append(threading.Thread(target=efts, args=(stop, a.pause), daemon=True))
    for t in ths: t.start()
    print(f'polling until {a.until} ET; {len(seen)} sightings already recorded'); sys.stdout.flush()
    t_log, n_new, n_poll = time.time(), 0, 0
    with open(fs, 'a', newline='') as fs_, open(fp, 'a', newline='') as fp_:
        ws, wp = csv.writer(fs_), csv.writer(fp_)
        if new_s: ws.writerow(['source', 'accession', 'cik', 'role', 'form', 'accepted', 't_send', 't_recv'])
        if new_p: wp.writerow(['source', 't_send', 't_recv', 'server_date', 'status', 'n_items'])
        while any(t.is_alive() for t in ths) or not out_q.empty():
            try: kind, row = out_q.get(timeout=1)
            except queue.Empty: continue
            if kind == 's':
                k = (row[0], row[1], row[2])
                if k in seen: continue
                seen.add(k); ws.writerow(row); n_new += 1
            elif kind == 'p':
                wp.writerow(row); n_poll += 1
            else:
                print(row); sys.stdout.flush()
            if time.time() - t_log > 60:
                fs_.flush(); fp_.flush(); t_log = time.time()
                print(f'{datetime.now(ET):%H:%M:%S} ET: {n_poll} polls, {n_new} new sightings'); sys.stdout.flush()
    print('done')


if __name__ == '__main__':
    main()
