"""MARKET-MAKING SIM, step 0: the NBBO for every trigger window of the opportunity scan (user 2026-11-17: "let's see if we
can get it to work on the cheap data" — trades + NBBO, not the Databento MBO).

For each trigger in --triggers (mm_opportunity_report's triggers_B3.csv: date, ticker, window_start_et, trigger_et):
the Massive /v3/quotes NBBO from window_start − --pre min (the warmup for the volatility / fill-intensity estimates) to the
trigger + --post min (the trading window), clipped to 09:30–16:00. Cached one file per trigger:
    <out>/<ticker>_<date>_<HHMM trigger>.parquet   ts, bid, bid_size, ask, ask_size, bid_ex, ask_ex
The key is read from ~/.config/massive/credentials, never printed. Resumable (a trigger with a file is skipped).

  cd research && python -u scripts/equity/mm_quotes_fetch.py > logs/mm_quotes_fetch.log 2>&1; echo exit $?
"""
import argparse, datetime, json, os, time, urllib.request, zoneinfo
from concurrent.futures import ThreadPoolExecutor, as_completed
import pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--triggers', default='data/equity/mm_opportunity/triggers_B3.csv')
ap.add_argument('--out', default='data/equity/mm_sim/quotes')
ap.add_argument('--pre', type=float, default=15.0, help='minutes before the window start')
ap.add_argument('--post', type=float, default=35.0, help='minutes after the trigger')
ap.add_argument('--workers', type=int, default=8)
a = ap.parse_args()
NY = zoneinfo.ZoneInfo('America/New_York')
os.makedirs(a.out, exist_ok=True)
KEY = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']
T0 = time.time()


def ns_at(d, sec):
    y, m, dd = map(int, d.split('-'))
    return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)


def sec_of(hhmm): h, m = map(int, hhmm.split(':')); return h * 3600 + m * 60


def fetch(r):
    path = f'{a.out}/{r.ticker}_{r.date}_{r.trigger_et.replace(":", "")}.parquet'
    if os.path.exists(path): return path, -1, 0
    s0 = max(9.5 * 3600, sec_of(r.window_start_et) - 60 * a.pre)
    s1 = min(16 * 3600, sec_of(r.trigger_et) + 60 * a.post)
    url = (f'https://api.massive.com/v3/quotes/{r.ticker}?timestamp.gte={ns_at(r.date, s0)}&timestamp.lt={ns_at(r.date, s1)}'
           f'&limit=50000&sort=timestamp&order=asc')
    rows, pages = [], 0
    while url:
        for attempt in range(5):
            try:
                req = urllib.request.Request(url, headers={'Authorization': f'Bearer {KEY}'})
                dd = json.load(urllib.request.urlopen(req, timeout=120)); break
            except Exception as e:
                if attempt == 4: raise RuntimeError(f'{r.ticker} {r.date}: {type(e).__name__}')
                time.sleep(2 * (attempt + 1))
        rows += [(q.get('sip_timestamp'), q.get('bid_price'), q.get('bid_size'), q.get('ask_price'), q.get('ask_size'),
                  q.get('bid_exchange'), q.get('ask_exchange')) for q in dd.get('results', [])]
        url = dd.get('next_url'); pages += 1
    Q = pd.DataFrame(rows, columns=['ts', 'bid', 'bid_size', 'ask', 'ask_size', 'bid_ex', 'ask_ex'])
    Q.to_parquet(path + '.tmp', index=False); os.replace(path + '.tmp', path)
    return path, len(Q), pages


R = list(pd.read_csv(a.triggers).itertuples())
print(f'{len(R)} triggers', flush=True)
done = 0; nq = 0
with ThreadPoolExecutor(a.workers) as ex:
    for f in as_completed([ex.submit(fetch, r) for r in R]):
        path, n, pages = f.result(); done += 1
        if n >= 0: nq += n
        if done % 25 == 0 or done == len(R):
            el = time.time() - T0
            print(f'{done}/{len(R)} | {nq:,} quotes fetched | {el:.0f}s, ETA {el / done * (len(R) - done):.0f}s', flush=True)
print(f'done in {(time.time() - T0) / 60:.1f} min', flush=True)
