"""GH-18 — a STOP AT THE SESSION HIGH for the 10:00 shorts (user 2026-11-23: "If it has a low or a negative PF we could use
that justification to put a stop at the session high past 10:30 ET". GH-17: the first-new-high shorts entered after 10:30 —
the path a 10:00 short holds through a break — PF 0.78 on U1000 (both halves < 1), 1.01 on D1000.)

The 10:00 book as today (U1000 / D1000: the 10:00 entry, the GH-6 rule, today's 350 bp stop) plus a COVER when a 1s bar at or
after the arming time A trades above the RUNNING session high (the highest 1s vwap since 09:30, before that bar) by more than a
buffer b; the fill = the $5k VWAP from the next second. The first exit wins. A = 10:00, 10:30 (the user's), 11:00; b = 0 and
1 %. Knowable as it happens (the high is the bars before).
    cd research && python3 scripts/equity/gaphv_histop.py > logs/gaphv_histop.log 2>&1
    cd research && python3 scripts/equity/gaphv_histop.py --show > data/equity/gaphv/histop.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, BARS, O, CLOSE

ARM = {'1000': 36000, '1030': 37800, '1100': 39600}
BUF = (0.0, 0.01)
OUT = 'data/equity/gaphv/histop.parquet'
def key(a, b): return f'h{a}_b{int(round(b * 1000))}'

def one(sec, px, vol, close):
    r = {}
    run = np.maximum.accumulate(px)
    prior = np.concatenate([[np.inf], run[:-1]])           # the session high BEFORE each bar
    for a, asec in ARM.items():
        for b in BUF:
            hit = np.where((sec >= asec) & (sec < 55800) & (px > prior * (1 + b)))[0]
            if len(hit):
                t = int(sec[hit[0]]); r[key(a, b) + '_t'] = t; r[key(a, b) + '_x'] = fill(sec, px, vol, t + 1, close)
            else:
                r[key(a, b) + '_t'] = -1; r[key(a, b) + '_x'] = np.nan
    return r

def day(args):
    d, rows = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, vwap, volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= {O} AND bucket < {CLOSE} AND volume > 0 AND vwap > 0 ORDER BY ticker, bucket""", [list(rows.ticker)]).df()
    G = {t: g for t, g in B.groupby('ticker')}
    out = []
    for row in rows.itertuples():
        g = G.get(row.ticker)
        if g is None or len(g) < 10: continue
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float), row.close)
        r['ticker'], r['d'] = row.ticker, d
        out.append(r)
    return out

if '--show' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'u1000', 'd1000', 'close'])
        E = E[E.u1000 | E.d1000]
        jobs = [(str(pd.Timestamp(d).date()), g) for d, g in E.groupby('d')]
        res = []
        with Pool(6) as p:
            for i, r in enumerate(p.imap_unordered(day, jobs, chunksize=4)):
                res.extend(r)
                if i % 500 == 0: print(f'{i}/{len(jobs)} {time.time() - t0:.0f}s', flush=True)
        R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
        R.to_parquet(OUT, index=False); print(f'wrote {len(R):,}, {time.time() - t0:.0f}s')
else:
    with contextlib.redirect_stdout(io.StringIO()):
        from gaphv_exits import X, simulate, pf, f2
        from gaphv_volstop import hm2s
    X = X.merge(pd.read_parquet(OUT), on=['ticker', 'd']).merge(
        pd.read_parquet('data/equity/gaphv/volstop3.parquet')[['ticker', 'd', 'e_w3_350_t', 'e_w3_350_x']], on=['ticker', 'd'])
    print('# GH-18 — the 10:00 shorts with a stop at the running session high\n')
    print('Today\'s 10:00 book (GH-6 rule + the 350 bp stop) + a cover when a 1s bar from the arming time trades above the session '
          'high so far by more than the buffer; the $5k fill from the next second; the first exit wins. sum = Σ % · PF (halves '
          '2016–2020 / 2021–2026) · worst · CVaR 1 % · the high-stop fired on · the covered trades: their result now / without '
          'the stop.\n')
    WATCH = [('LIFW', '2023-11-06'), ('COSM', '2022-12-16'), ('XELA', '2023-05-16'), ('CLDX', '2020-06-11'), ('ENTA', '2025-09-29')]
    for name, flag, rule in (('U1000', 'u1000', 'VWAP'), ('D1000', 'd1000', 'T1530')):
        K = X[X[flag] & np.isfinite(X.x1000) & (X.x1000 > 0)].copy().reset_index(drop=True); K['entry'] = K.x1000
        px, at = simulate(K, rule)
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at]).astype(float)
        t = K.e_w3_350_t.values; x = K.e_w3_350_x.values; vs = (t > 0) & np.isfinite(x) & (t < bsec)
        ex_t = np.where(vs, t, bsec); ex_x = np.where(vs, x, px)
        r0 = 100 * (1 - ex_x / K.entry.values)
        h1 = (K.yr <= 2020).values; k1 = max(1, len(K) // 100)
        def line(lab, r, fired=None):
            s = np.sort(r)
            cov = f'{fired.mean():.0%} · {r[fired].mean():+.2f} / {r0[fired].mean():+.2f} %' if fired is not None and fired.any() else ''
            return f'| {lab} | {r.sum():,.0f} ({100 * (r.sum() / r0.sum() - 1):+.1f} %) | {f2(pf(r))} ({f2(pf(r[h1]))} / {f2(pf(r[~h1]))}) | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {cov} |'
        print(f'## {name} ({rule}), n = {len(K):,}\n')
        print('| book | sum | PF (halves) | worst | CVaR 1 % | high-stop fired · covered: now / without |')
        print('|---|---|---|---|---|---|')
        print(line('today (no high-stop)', r0))
        cells = {}
        for a in ARM:
            for b in BUF:
                ht = K[key(a, b) + '_t'].values.astype(float); hx = K[key(a, b) + '_x'].values
                fired = (ht > 0) & np.isfinite(hx) & (ht < ex_t)
                r = np.where(fired, 100 * (1 - hx / K.entry.values), r0)
                cells[(a, b)] = r
                bb = '**' if (a, b) == ('1030', 0.0) else ''
                print(line(f'{bb}armed {a[:2]}:{a[2:]}, buffer {100 * b:.0f} %{bb}', r, fired))
        print()
        z = [np.where((K.ticker == tk).values & (K.d.astype(str) == dd).values)[0] for tk, dd in WATCH]
        print('Watched: ' + '; '.join(f'{tk} {dd}: {r0[i[0]]:+.0f} → {cells[("1030", 0.0)][i[0]]:+.0f} %' for (tk, dd), i in zip(WATCH, z) if len(i)) + '\n')
