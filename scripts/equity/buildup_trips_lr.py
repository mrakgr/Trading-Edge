r"""BUILDUP STUDY, step 4d (user 2026-11-17): the FlushFader trips' pre-signal order flow by LEE–READY — the best reconstruction
against the MBO truth (buildup_validate2.py: 0.92 per minute / 0.95 over 60 min on lit prints). Per lit print: above the
prevailing NBBO mid = a buy, below = a sell, AT the mid = the tick rule (the lit tape, 300-print warmup); the prevailing
quote = the last valid NBBO (two-sided, not crossed) strictly before the print's SIP timestamp. Also the QUOTE-ONLY rule
(mid prints unsigned). The windows = buildup_trips.py's clock spans (secs_N, ending at the signal second inclusive).

The NBBO comes from Massive REST per (ticker, date) for the UNION of that ticker-day's trip windows only (overlapping
windows merged; the key is read from the credentials file, never printed). Quotes are not stored — each worker
classifies the day's prints right after the fetch. Resumable: the days are processed in parts of --part days, one parquet
per part in --out.
  cd research && python -u scripts/equity/buildup_trips_lr.py > logs/buildup_trips_lr.log 2>&1; echo exit $?
"""
import argparse, datetime, glob, json, os, time, urllib.request, zoneinfo
from multiprocessing import Pool
import duckdb, numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--tag', default='ff')
ap.add_argument('--out', default='data/equity/buildup/lr_parts')
ap.add_argument('--part', type=int, default=100)
ap.add_argument('--workers', type=int, default=8)
a = ap.parse_args()
TR = 'data/bulk/trades'
NS = (60, 300, 600, 1200)
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
NY = zoneinfo.ZoneInfo('America/New_York')
KEY = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']


def quotes(t, g0, g1):
    url = f'https://api.massive.com/v3/quotes/{t}?timestamp.gte={g0}&timestamp.lt={g1}&limit=50000&sort=timestamp&order=asc'
    rows = []
    while url:
        for attempt in range(6):
            try:
                dd = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'Authorization': f'Bearer {KEY}'}), timeout=120)); break
            except Exception as e:
                if attempt == 5: raise RuntimeError(f'{t}: {type(e).__name__}')
                time.sleep(2 * (attempt + 1))
        rows += [(q.get('sip_timestamp'), q.get('bid_price'), q.get('ask_price')) for q in dd.get('results', [])]
        url = dd.get('next_url')
    return np.array(rows, dtype=float).reshape(-1, 3)


def tick_sign(p):
    d = np.diff(p, prepend=np.nan); s = np.sign(d); s[~np.isfinite(s)] = 0
    return pd.Series(np.where(s == 0, np.nan, s)).ffill().fillna(0).to_numpy()


def day(args):
    d, trips = args
    y, m, dd = map(int, d.split('-')); base = int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9)
    syms = sorted(set(t[0] for t in trips))
    lo = min(t[1] - t[3][-1] for t in trips) - 3600; hi = max(t[1] for t in trips) + 1
    con = duckdb.connect(); con.execute("SET threads=1; SET memory_limit='1GB'")
    lst = ', '.join(f"'{s}'" for s in syms)
    X = con.execute(f"""SELECT ticker, sip_timestamp ts, price, size FROM read_parquet('{TR}/{d}.parquet')
                        WHERE ticker IN ({lst}) AND exchange <> 4 AND size > 0 AND NOT list_has_any(conditions, {EXCL})
                        AND sip_timestamp >= {base + int(max(lo, 4 * 3600) * 1e9)} AND sip_timestamp < {base + int(hi * 1e9)}
                        ORDER BY ticker, sip_timestamp""").df()
    con.close()
    G = {s: g for s, g in X.groupby('ticker')}
    out = []
    for sym in syms:
        tt = [t for t in trips if t[0] == sym]
        g = G.get(sym)
        if g is None:
            out += [dict(key=t[2]) for t in tt]; continue
        # the union of this ticker-day's windows (+ 60 s before, so the first print has a prevailing quote)
        iv = sorted((base + int((t[1] - t[3][-1] - 60) * 1e9), base + int((t[1] + 1) * 1e9)) for t in tt)
        merged = []
        for s0, s1 in iv:
            if merged and s0 <= merged[-1][1]: merged[-1][1] = max(merged[-1][1], s1)
            else: merged.append([s0, s1])
        Q = np.concatenate([quotes(sym, s0, s1) for s0, s1 in merged]) if merged else np.zeros((0, 3))
        if len(Q):
            ok = np.isfinite(Q).all(1) & (Q[:, 1] > 0) & (Q[:, 2] > Q[:, 1]); Q = Q[ok]; Q = Q[np.argsort(Q[:, 0], kind='stable')]
        ts = g.ts.to_numpy(np.int64); p = g.price.to_numpy(float); q = g['size'].to_numpy(float)
        tk = tick_sign(p)
        if len(Q):
            j = np.searchsorted(Q[:, 0], ts.astype(float), 'left') - 1
            okq = j >= 0
            jj = np.clip(j, 0, None)
            mid = np.where(okq, 0.5 * (Q[jj, 1] + Q[jj, 2]), np.nan)
            stale = ~okq | (ts.astype(float) - Q[jj, 0] > 60e9)        # no quote within 60 s: outside the fetched windows
            mid[stale] = np.nan
        else:
            mid = np.full(len(p), np.nan)
        has = np.isfinite(mid)
        lr = np.where(has & (p > mid + 1e-9), 1.0, np.where(has & (p < mid - 1e-9), -1.0, tk))
        qo = np.where(has & (p > mid + 1e-9), 1.0, np.where(has & (p < mid - 1e-9), -1.0, 0.0))
        for sym_, sec, key, spans in tt:
            r = dict(key=key)
            end = np.searchsorted(ts, base + int((sec + 1) * 1e9), 'left')
            for N, span in zip(NS, spans):
                st = np.searchsorted(ts, base + int((sec - span) * 1e9), 'left')
                vol = q[st:end].sum()
                r[f'lr_{N}'] = (lr[st:end] * q[st:end]).sum() / vol if vol > 0 else np.nan
                r[f'qo_{N}'] = (qo[st:end] * q[st:end]).sum() / vol if vol > 0 else np.nan
                r[f'quoted_{N}'] = float(has[st:end].mean()) if end > st else np.nan
            out.append(r)
    return out


if __name__ == '__main__':
    T0 = time.time()
    os.makedirs(a.out, exist_ok=True)
    B = pd.read_parquet(f'data/equity/buildup/trips_{a.tag}.parquet')
    days = sorted(d for d in B.trade_date.unique() if os.path.exists(f'{TR}/{d}.parquet'))
    parts = [days[i:i + a.part] for i in range(0, len(days), a.part)]
    for k, pdays in enumerate(parts):
        path = f'{a.out}/{a.tag}_{k:03d}.parquet'
        if os.path.exists(path): continue
        sub = B[B.trade_date.isin(pdays)]
        jobs = [(d, [(r.symbol, int(r.signal_sec), r.key, [getattr(r, f'secs_{N}') for N in NS]) for r in g.itertuples()])
                for d, g in sub.groupby('trade_date')]
        rows = []
        with Pool(a.workers) as pool:
            for r in pool.imap_unordered(day, jobs, chunksize=1): rows += r
        pd.DataFrame(rows).to_parquet(path + '.tmp', index=False); os.replace(path + '.tmp', path)
        el = time.time() - T0
        print(f'part {k + 1}/{len(parts)} ({pdays[0]} … {pdays[-1]}): {len(rows):,} trips, {el / 60:.1f} min', flush=True)
    R = pd.concat([pd.read_parquet(f) for f in sorted(glob.glob(f'{a.out}/{a.tag}_*.parquet'))])
    out = B.merge(R, on='key', how='left')
    out.to_parquet(f'data/equity/buildup/trips_{a.tag}_lr.parquet', index=False)
    print(f'done: {len(out):,} trips; lr_1200 missing {out.lr_1200.isna().sum()}; median quoted share {out.quoted_1200.median():.1%}; '
          f'{(time.time() - T0) / 60:.1f} min', flush=True)
