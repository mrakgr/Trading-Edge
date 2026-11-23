"""GH-26 — the 10:00 HALT filter with the S40x detector, and its validation against the QUOTES (user 2026-11-23: "Yes" to: apply
the S40x detector at G = 30 as the 10:00 halt flag; validate it, and GH-16's bare proxy, with the quotes).

Flags at 10:00:00 for every U1000 / D1000 stock-day (from the 1s bars of [09:30, 10:00), nothing later):
  bare      GH-16's proxy: the silence running into 10:00 >= 60 s
  s40x_G    FlushFader's S40x detector (GH-25) on the run IN PROGRESS at 10:00: run so far >= 58 s · the pre-hole ln range over the
            last 300 present bars >= 4 % · the pre-hole adjusted gap60 < G (earlier runs since 09:30 classified first, for the
            recursion); G = 4, 20, 30
The truth (--quotes): Massive REST v3 quotes, the LAST NBBO before 10:00:00 (one call per flagged stock-day; cached) — the Oms's
halt rule (Executor.haltOf): no eligible print for >= 5 s (every flag here has >= 58 s) AND that quote locked (bid = ask),
crossed, empty or one-sided.
The book (--show): the 10:00 defaults — U1000 to 15:30 + the 30m bar VWAP above V30 at a mark from 11:00, D1000 to 15:30 + the
1s break of M2 from 13:00; both with the one volatility-stop EWMA from 09:30; the universe z > −2 and the 09:30 stop not fired
before 10:00.
    cd research && python3 scripts/equity/gaphv_halt10.py > logs/gaphv_halt10.log 2>&1
    cd research && python3 scripts/equity/gaphv_halt10.py --quotes > logs/gaphv_halt10_quotes.log 2>&1
    cd research && python3 scripts/equity/gaphv_halt10.py --show > data/equity/gaphv/halt10.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time, json, urllib.request, datetime as dt, zoneinfo
from multiprocessing import Pool

BARS = 'data/intraday_1s_slim'
O, T10 = 34200, 36000
GS = (4, 20, 30)
OUT = 'data/equity/gaphv/halt10.parquet'
QOUT = 'data/equity/gaphv/halt10_quotes.parquet'

def flags(sec, px):
    r = {}
    m = (sec >= O) & (sec < T10); s, p = sec[m], px[m]
    if not len(s):
        r['bare'] = True
        for g in GS: r[f's40x_{g}'] = False
        r['run10'] = 1800; return r
    run10 = T10 - s[-1] - 1
    r['run10'] = int(run10); r['bare'] = run10 >= 60
    for g in GS:
        halts = []
        def classify(i, run):
            lo = max(0, i - 299); w = p[lo:i + 1]
            rng = np.log(w.max() / w.min())
            win0 = max(O, s[i] - 59)
            raw = (s[i] - win0 + 1) - ((s >= win0) & (s <= s[i])).sum()
            ov = sum(max(0, min(b, s[i]) - max(a, win0) + 1) for a, b in halts)
            return run >= 58 and rng >= 0.04 and (raw - ov) < g
        for i in range(len(s) - 1):
            run = s[i + 1] - s[i] - 1
            if run >= 58 and classify(i, run): halts.append((s[i] + 1, s[i + 1] - 1))
        r[f's40x_{g}'] = bool(run10 >= 58 and classify(len(s) - 1, run10))
    return r

def day(args):
    d, rows = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= {O} AND bucket < {T10} AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [list(rows.ticker)]).df()
    G = {t: g for t, g in B.groupby('ticker')}
    out = []
    for row in rows.itertuples():
        g = G.get(row.ticker)
        r = flags(g.bucket.values.astype(int), g.vwap.values.astype(float)) if g is not None else flags(np.array([], int), np.array([]))
        r['ticker'], r['d'] = row.ticker, d
        out.append(r)
    return out

NY = zoneinfo.ZoneInfo('America/New_York')
def last_quote(tk, d, key):
    y, m, dd = map(int, str(d).split('-'))
    t10 = int(dt.datetime(y, m, dd, 10, 0, tzinfo=NY).timestamp() * 1e9)
    url = (f'https://api.massive.com/v3/quotes/{tk}?timestamp.lt={t10}&timestamp.gte={t10 - 3600 * 10**9}'
           f'&limit=1&sort=timestamp&order=desc')
    for k in range(4):
        try:
            j = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'Authorization': f'Bearer {key}'}), timeout=60))
            res = j.get('results') or []
            if not res: return dict(q_ok=True, bid=np.nan, ask=np.nan, q_age=np.nan)
            q = res[0]
            return dict(q_ok=True, bid=q.get('bid_price', 0.0) or 0.0, ask=q.get('ask_price', 0.0) or 0.0,
                        q_age=(t10 - q['sip_timestamp']) / 1e9)
        except Exception as e:
            time.sleep(2 * (k + 1))
    return dict(q_ok=False, bid=np.nan, ask=np.nan, q_age=np.nan)

if '--show' not in sys.argv and '--quotes' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'u1000', 'd1000'])
        E = E[E.u1000 | E.d1000]
        jobs = [(str(pd.Timestamp(d).date()), g) for d, g in E.groupby('d')]
        res = []
        with Pool(6) as p:
            for i, r in enumerate(p.imap_unordered(day, jobs, chunksize=4)):
                res.extend(r)
                if i % 500 == 0: print(f'{i}/{len(jobs)} {time.time() - t0:.0f}s', flush=True)
        R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
        R.to_parquet(OUT, index=False); print(f'wrote {len(R):,}, {time.time() - t0:.0f}s')
        print(R[['bare'] + [f's40x_{g}' for g in GS]].sum().to_string())
elif '--quotes' in sys.argv:
    R = pd.read_parquet(OUT)
    F = R[R.bare | R.s40x_4 | R.s40x_20 | R.s40x_30][['ticker', 'd']].drop_duplicates()
    done = pd.read_parquet(QOUT) if os.path.exists(QOUT) else pd.DataFrame(columns=['ticker', 'd'])
    have = set(zip(done.ticker, done.d.astype(str)))
    todo = [(t, d) for t, d in zip(F.ticker, F.d.astype(str)) if (t, d) not in have]
    key = json.load(open(os.path.expanduser('~/.config/massive/credentials')))['massive_api_key']
    print(f'flagged {len(F):,}; cached {len(have):,}; to fetch {len(todo):,}', flush=True)
    rows = []
    for i, (t, d) in enumerate(todo):
        rows.append(dict(ticker=t, d=d, **last_quote(t, d, key)))
        if i % 50 == 0: print(f'{i}/{len(todo)}', flush=True)
        time.sleep(0.05)
    if rows:
        N = pd.DataFrame(rows); N['d'] = pd.to_datetime(N.d).dt.date
        A = pd.concat([done, N], ignore_index=True) if len(done) else N
        A.to_parquet(QOUT, index=False)
    print(f'fetched {len(rows)}; failed {sum(not r["q_ok"] for r in rows)}')
else:
    sys.path.insert(0, 'scripts/equity')
    with contextlib.redirect_stdout(io.StringIO()):
        from gaphv_exits import X, pf, f2, CHK
        from gaphv_volstop import hm2s
    Q = pd.read_parquet(QOUT)
    Q['halt_q'] = Q.q_ok & ~((Q.bid > 0) & (Q.ask > 0) & (Q.bid < Q.ask))      # locked / crossed / one-sided / empty
    st = pd.read_parquet('data/equity/gaphv/stops.parquet')
    X = (X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
          .merge(Q[['ticker', 'd', 'q_ok', 'halt_q', 'bid', 'ask', 'q_age']], on=['ticker', 'd'], how='left')
          .merge(st[['ticker', 'd'] + [c for c in st.columns if c.startswith('bv')]], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/entry10.parquet')[['ticker', 'd', 'z']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet')[['ticker', 'd', 'pinf_a700_b500_t', 'pinf_a700_b500_x']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/d1000.parquet')[['ticker', 'd', 'brk_M2_1300_t', 'brk_M2_1300_x']], on=['ticker', 'd'], how='left'))
    print('# GH-26 — the 10:00 halt filter: the S40x detector vs the bare proxy, checked against the quotes\n')
    print('Flags at 10:00 from the 1s bars before it. "real" = the last NBBO before 10:00 (Massive REST) locked / crossed / one-sided / '
          'empty — the Oms\'s halt rule (every flag already has >= 58 s without a print). The book: the 10:00 defaults (U1000: '
          'to 15:30 + the 30m bar VWAP > V30 from 11:00; D1000: to 15:30 + the M2 1s break from 13:00; the one EWMA stop), the '
          'universe z > −2 and the 09:30 stop not fired before 10:00. Σ = Σ % (the short, gross).\n')
    FL = [('bare (GH-16)', 'bare')] + [(f'S40x G = {g}', f's40x_{g}') for g in GS]
    for name, flag in (('U1000', 'u1000'), ('D1000', 'd1000')):
        A = X[X[flag] & ~(X.z < -2.0) & ~((X.pinf_a700_b500_t > 0) & (X.pinf_a700_b500_t < 36000))
              & np.isfinite(X.x1000) & (X.x1000 > 0)].copy().reset_index(drop=True)
        st_t = A.pinf_a700_b500_t.values.astype(float); st_x = A.pinf_a700_b500_x.values
        vs = (st_t > 0) & np.isfinite(st_x) & (st_t < 55800)
        ex_t = np.where(vs, st_t, 55800.0); ex_x = np.where(vs, st_x, A.x1530.values); ex_x = np.where(np.isfinite(ex_x), ex_x, A.close.values)
        r0 = 100 * (1 - ex_x / A.x1000.values)
        if flag == 'u1000':
            mt = np.full(len(A), -1.0); mx = np.full(len(A), np.nan)
            for m in CHK:
                if hm2s(m) < 39600: continue
                go = (mt < 0) & np.isfinite(A['bv' + m].values) & (A['bv' + m].values > A.bv1000.values) & np.isfinite(A[f'x{m}'].values)
                mt[go] = hm2s(m); mx[go] = A[f'x{m}'].values[go]
            f = (mt > 0) & (mt < ex_t); r = np.where(f, 100 * (1 - mx / A.x1000.values), r0)
        else:
            bt = A.brk_M2_1300_t.values.astype(float); bx = A.brk_M2_1300_x.values
            f = (bt > 0) & np.isfinite(bx) & (bt < ex_t); r = np.where(f, 100 * (1 - bx / A.x1000.values), r0)
        h1 = (A.yr <= 2020).values; real = A.halt_q.astype('boolean').fillna(False).to_numpy(bool)
        print(f'## {name}: {len(A):,} trades · the book Σ {r.sum():,.0f} · PF {f2(pf(r))} · worst {r.min():+.0f} %\n')
        print('| flag | flagged | really halted (quotes) | not halted | flagged trades: mean · PF | skip them: Σ after · worst |')
        print('|---|---|---|---|---|---|')
        nb = None
        for lab, c in FL:
            m = A[c].values.astype(bool)
            rq = (m & real).sum(); nq = m.sum() - rq
            keep = ~m
            print(f'| {lab} | {m.sum():,} | {rq:,} ({rq / max(1, m.sum()):.0%}) | {nq:,} | {r[m].mean():+.2f} % · {f2(pf(r[m]))} | '
                  f'{r[keep].sum():,.0f} ({100 * (r[keep].sum() / r.sum() - 1):+.1f} %) · {r[keep].min():+.0f} % |')
        print()
        for lab, m in (('really halted at 10:00 (quotes), any flag', real), ('flagged by the bare proxy but NOT halted', A.bare.values.astype(bool) & ~real)):
            g = r[m]
            if len(g): print(f'- {lab}: {len(g)} trades · mean {g.mean():+.2f} % · PF {f2(pf(g))} · worst {g.min():+.0f} % · halves {g[h1[m]].mean():+.2f} / {g[~h1[m]].mean():+.2f} %')
        print()
