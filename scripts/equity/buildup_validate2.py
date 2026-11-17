r"""BUILDUP STUDY, step 0b (user 2026-11-17: "what if we derived the signed order flow using both tick and quote data ... better at
reconstruction" and "what if we used the 1s bar as the tick instead"). The trade-classification algorithms against the TRUE
aggressor side (the Databento MBO T records, lit venues) on every MBO name-day on disk, per minute and over 15 / 60 min:

  bar tick     the tick rule on the 1 s bars' vwap (the current proxy, all prints)            data/intraday_1s_slim
  trade tick   the tick rule trade by trade (an unchanged price keeps the last sign)          data/bulk/trades
  Lee–Ready    above the prevailing NBBO mid = buy, below = sell, AT the mid = the tick rule  + the Massive NBBO
  EMO          at the ask (or above) = buy, at the bid (or below) = sell, inside = the tick rule
  quote only   Lee–Ready without the tick fallback (a mid trade = unsigned)
The "prevailing" quote = the last valid NBBO (two-sided, not crossed) strictly before the trade's SIP timestamp.
Each algorithm is scored on LIT prints (exchange <> 4: the same population as the truth) and on ALL prints (+ TRF, what
the 1 s bars carry). The NBBO is fetched once per name-day (RTH) into --quotes; the key is read from the credentials
file and never printed.

  cd research && python -u scripts/equity/buildup_validate2.py > data/equity/buildup/validate2.md 2> logs/buildup_validate2.log; echo exit $?
"""
import argparse, datetime, glob, json, os, time, urllib.request, zoneinfo
import duckdb, numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument('--mbo', default='/mnt/c/Users/mrakg/.tapesim/data/databento/mbo')
ap.add_argument('--quotes', default='data/equity/realized_spread/quotes')
a = ap.parse_args()
NY = zoneinfo.ZoneInfo('America/New_York')
EXCL = "[2, 7, 10, 13, 20, 21, 22, 29, 32, 52, 53, 8, 15, 19]::UTINYINT[]"
def ns_at(d, sec):
    y, m, dd = map(int, d.split('-')); return int(datetime.datetime(y, m, dd, tzinfo=NY).timestamp() * 1e9) + int(sec * 1e9)


def fetch(t, d):
    path = f'{a.quotes}/{t}_{d}.parquet'
    if os.path.exists(path): return pd.read_parquet(path)
    key = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']
    url = (f'https://api.massive.com/v3/quotes/{t}?timestamp.gte={ns_at(d, 9.5 * 3600 - 60)}&timestamp.lt={ns_at(d, 16 * 3600)}'
           f'&limit=50000&sort=timestamp&order=asc')
    rows = []
    while url:
        for attempt in range(5):
            try:
                dd = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'Authorization': f'Bearer {key}'}), timeout=120)); break
            except Exception as e:
                if attempt == 4: raise RuntimeError(f'{t} {d}: {type(e).__name__}')
                time.sleep(2 * (attempt + 1))
        rows += [(q.get('sip_timestamp'), q.get('bid_price'), q.get('bid_size'), q.get('ask_price'), q.get('ask_size')) for q in dd.get('results', [])]
        url = dd.get('next_url')
    Q = pd.DataFrame(rows, columns=['ts', 'bid', 'bid_size', 'ask', 'ask_size'])
    Q.to_parquet(path + '.tmp', index=False); os.replace(path + '.tmp', path)
    print(f'  fetched {t} {d}: {len(Q):,} quotes', file=__import__('sys').stderr, flush=True)
    return Q


def tick_sign(p):
    d = np.diff(p, prepend=np.nan); s = np.sign(d); s[~np.isfinite(s)] = 0
    return pd.Series(np.where(s == 0, np.nan, s)).ffill().fillna(0).to_numpy()


con = duckdb.connect(); con.execute("SET memory_limit='3GB'; SET threads=4")
ALG = ['bar tick', 'trade tick', 'Lee–Ready', 'EMO', 'quote only']
res = []
for dd_ in sorted(glob.glob(f'{a.mbo}/*/*/')):
    files = glob.glob(f'{dd_}*.dbn.zst')
    if len(files) < 5: continue
    t = dd_.rstrip('/').split('/')[-2]; day = dd_.rstrip('/').split('/')[-1]
    import databento as db
    parts = []
    for f in files:
        x = db.DBNStore.from_file(f).to_df(map_symbols=False, pretty_ts=False); x = x[x.action == 'T']
        parts.append(pd.DataFrame(dict(ts=x.index.to_numpy(np.int64), side=x.side.to_numpy(), size=x['size'].to_numpy(float))))
    T = pd.concat(parts)
    base = ns_at(day, 0); t0 = ns_at(day, 9.5 * 3600); t1 = ns_at(day, 16 * 3600)
    T = T[(T.ts >= t0) & (T.ts < t1)]
    T['m'] = (T.ts - t0) // 60_000_000_000
    T['sv'] = np.where(T.side == 'B', T['size'], np.where(T.side == 'A', -T['size'], 0.0))
    truth = T.groupby('m').sv.sum()
    # the bar tick (current)
    B = con.execute(f"""SELECT bucket, vwap, volume FROM read_parquet('data/intraday_1s_slim/{day}.parquet')
                        WHERE ticker = '{t}' AND bucket >= 34200 AND bucket < 57600 AND volume > 0 ORDER BY bucket""").df()
    B['s'] = tick_sign(B.vwap.to_numpy()); B['m'] = (B.bucket - 34200) // 60
    est = {'bar tick|all': (B.s * B.volume).groupby(B.m).sum()}
    # the trade-level algorithms
    X = con.execute(f"""SELECT sip_timestamp ts, price, size, exchange <> 4 lit FROM read_parquet('data/bulk/trades/{day}.parquet')
                        WHERE ticker = '{t}' AND size > 0 AND NOT list_has_any(conditions, {EXCL})
                        AND sip_timestamp >= {t0} AND sip_timestamp < {t1} ORDER BY ts""").df()
    Q = fetch(t, day).dropna().sort_values('ts', kind='stable')
    qb = Q.bid.to_numpy(); qa = Q.ask.to_numpy(); v = (qb > 0) & (qa > qb)
    qt = Q.ts.to_numpy(np.int64)[v]; qb = qb[v]; qa = qa[v]
    p = X.price.to_numpy(); ts = X.ts.to_numpy(np.int64); sz = X['size'].to_numpy(float); lit = X.lit.to_numpy()
    j = np.searchsorted(qt, ts, 'left') - 1; okq = j >= 0
    bid = np.where(okq, qb[np.clip(j, 0, None)], np.nan); ask = np.where(okq, qa[np.clip(j, 0, None)], np.nan); mid = (bid + ask) / 2
    m = (ts - t0) // 60_000_000_000
    for pop, sel in (('lit', lit), ('all', np.ones(len(p), bool))):
        tk = tick_sign(p[sel])                       # the tick rule within the population (the lit tape alone for 'lit')
        mi = mid[sel]; bi = bid[sel]; ai = ask[sel]; pp = p[sel]
        lr = np.where(pp > mi + 1e-9, 1.0, np.where(pp < mi - 1e-9, -1.0, tk)); lr = np.where(np.isfinite(mi), lr, tk)
        emo = np.where(pp >= ai - 1e-9, 1.0, np.where(pp <= bi + 1e-9, -1.0, tk)); emo = np.where(np.isfinite(mi), emo, tk)
        qo = np.where(pp > mi + 1e-9, 1.0, np.where(pp < mi - 1e-9, -1.0, 0.0)); qo = np.where(np.isfinite(mi), qo, 0.0)
        for name, s in (('trade tick', tk), ('Lee–Ready', lr), ('EMO', emo), ('quote only', qo)):
            est[f'{name}|{pop}'] = pd.Series(s * sz[sel]).groupby(m[sel]).sum()
        # the share of LIT volume where the algorithm agrees with the truth is not computable trade by trade (the MBO and
        # the SIP prints are not matched one to one) — the per-minute correlations are the score
    for k, e in est.items():
        J = pd.concat([truth.rename('y'), e.rename('x')], axis=1).fillna(0).sort_index()
        J = J[(J.index >= 1) & (J.index <= 388)]   # without the opening / closing crosses (the truth leaves them unsigned: side N)
        r = [J.y.corr(J.x)] + [J.y.rolling(w).sum().corr(J.x.rolling(w).sum()) for w in (15, 60)]
        res.append(dict(nd=f'{t} {day}', alg=k.split('|')[0], pop=k.split('|')[1], c1=r[0], c15=r[1], c60=r[2], s1=J.y.corr(J.x, method='spearman')))
    print(f'{t} {day} done', file=__import__('sys').stderr, flush=True)

R = pd.DataFrame(res)
print('# Trade classification vs the TRUE aggressor side (Databento MBO, lit) — 16 name-days\n')
print('Pearson correlation of the per-minute signed volume with the truth; median over the name-days (min in brackets).\n')
print('Minutes 1–388 (the opening / closing crosses out). Spearman = rank correlation per minute (robust to block prints).\n')
print('| algorithm | prints | per minute | per minute, Spearman | 15 min rolling | 60 min rolling |\n|---|---|---|---|---|---|')
for (alg, pop), g in R.groupby(['alg', 'pop'], sort=False):
    print(f'| {alg} | {pop} | {g.c1.median():.2f} ({g.c1.min():.2f}) | {g.s1.median():.2f} ({g.s1.min():.2f}) | {g.c15.median():.2f} ({g.c15.min():.2f}) | {g.c60.median():.2f} ({g.c60.min():.2f}) |')
print('\n## Per name-day, per minute (lit prints; the bar tick on all)\n')
P = R[(R['pop'] == 'lit') | (R.alg == 'bar tick')].pivot_table(index='nd', columns='alg', values='c1')
print(P[[c for c in ALG if c in P]].round(2).to_markdown())
print('\n## Per name-day, 60 min rolling\n')
P = R[(R['pop'] == 'lit') | (R.alg == 'bar tick')].pivot_table(index='nd', columns='alg', values='c60')
print(P[[c for c in ALG if c in P]].round(2).to_markdown())
