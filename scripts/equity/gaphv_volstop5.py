"""GH-13f — a TWO-LEVEL threshold for the EWMA disaster stop (user 2026-11-23: "Good idea, let's give it a try"). After GH-13e:
arming the stop only from 09:40 adds +5.2 % (the open's chop no longer fires it) but lets the early disasters through (CNST,
PHUN, YELL, ENTA). Here the stop may fire BEFORE the gate too, but only on an EXTREME value (E bp), and from the gate at the
usual 350 bp — the ew · any stop of GH-13c, W = 3 (4 slots), computed from the entry as ever.
E = 500, 600, 700, 800, 1000, 1500 and ∞ (= GH-13e's gate); the gate = 09:35, 09:40, 09:45; 350 bp everywhere = the
control (= today's stop). Only U0930 / D0930 change (the 10:00 entries are past every gate). The $5k fill, no time cap; on
top of each variant's GH-6 exit.
    cd research && python3 scripts/equity/gaphv_volstop5.py > logs/gaphv_volstop5.log 2>&1
    cd research && python3 scripts/equity/gaphv_volstop5.py --show > data/equity/gaphv/volstop5.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, ew_series, hms, BARS, O, CLOSE, LAST

W, LATE = 3, 350
EARLY = (500, 600, 700, 800, 1000, 1500, 10 ** 9)          # 10 ** 9 = never before the gate (GH-13e)
GATES = {'0935': 34500, '0940': 34800, '0945': 35100}
OUT = 'data/equity/gaphv/volstop5.parquet'

def key(tag, g, e): return f'{tag}_g{g}_e{"inf" if e >= 10 ** 9 else e}'

def one(sec, px, vol, close, entries):
    r = {}
    for tag, e0, _ in entries:
        t, ew, k = ew_series(sec, px, vol, e0)
        if tag == 'o':
            cells = [(g, gs, e) for g, gs in GATES.items() for e in EARLY] + [('0930', 34200, LATE)]
        else:
            cells = [('0930', 34200, LATE)]                      # the 10:00 entries: the control only
        for g, gs, e in cells:
            hit = (k >= W) & (t < LAST) & (((t < gs) & (ew >= e)) | ((t >= gs) & (ew >= LATE))) if len(t) else np.array([], bool)
            kk = key(tag, g, e if g != '0930' else LATE)
            if len(hit) and hit.any():
                i = np.argmax(hit); tt = int(t[i]) + 1
                r[kk + '_t'] = tt; r[kk + '_x'] = fill(sec, px, vol, tt, close)
            else:
                r[kk + '_t'] = -1; r[kk + '_x'] = np.nan
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
        ent = [('o', O, row.open)] + ([('e', 36000, row.x1000)] if np.isfinite(row.x1000) else [])
        r = one(g.bucket.values, g.vwap.values.astype(float), g.volume.values.astype(float), row.close, ent)
        r['ticker'], r['d'] = row.ticker, d
        out.append(r)
    return out

if '--show' not in sys.argv:
    if __name__ == '__main__':
        t0 = time.time()
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'open', 'close']).merge(
            pd.read_parquet('data/equity/gaphv/path.parquet', columns=['ticker', 'd', 'x1000']), on=['ticker', 'd'])
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
    X = X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
    V = [('U0930', 'u0930', 'open', 'LOSER', 'o'), ('D0930', 'd0930', 'open', 'T1530', 'o'),
         ('U1000', 'u1000', 'x1000', 'VWAP', 'e'), ('D1000', 'd1000', 'x1000', 'T1530', 'e')]
    WATCH = [('CNST', '2019-11-06'), ('PHUN', '2021-10-22'), ('YELL', '2023-08-01'), ('ENTA', '2025-09-29'), ('PGHL', '2024-11-26'), ('ZJYL', '2023-12-18')]
    print('# GH-13f — a two-level threshold: E bp before the gate, 350 bp from it (ew · any, W = 3 = 4 slots)\n')
    print('"E = ∞" = GH-13e\'s gate (never before it); the 350 / 09:30 row = today\'s stop (the control). sum = Σ % · worst · '
          'CVaR 1 % · PF (halves) · fired / yr, of which before the gate. On top of each variant\'s GH-6 exit; the $5k fill.\n')
    books = {}
    for name, flag, ent, rule, tag in V:
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule); base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        def res(kk, K=K, base=base, bsec=bsec):      # bound now (a closure over the loop's K would see the last variant)
            t = K[kk + '_t'].values; x = K[kk + '_x'].values
            fire = (t > 0) & np.isfinite(x) & (t < bsec)
            return fire, t, 100 * np.where(fire, 1 - x / K.entry.values, base)
        books[name] = (K, res)
    k1 = lambda n: max(1, n // 100)
    for name in ('U0930', 'D0930'):
        K, res = books[name]
        print(f'## {name}, n = {len(K):,}\n')
        print('| gate | E | sum | worst | CVaR 1 % | PF | PF halves | fired / yr (before the gate) |')
        print('|---|---|---|---|---|---|---|---|')
        cells = [('0930', LATE, 34200)] + [(g, e, gs) for g, gs in GATES.items() for e in EARLY]
        for g, e, gs in cells:
            fire, t, r = res(key('o', g, e)); s = np.sort(r)
            el = 'none (350 all day)' if g == '0930' else ('∞' if e >= 10 ** 9 else str(e))
            b = '**' if g == '0930' else ''
            print(f'| {b}{g[:2]}:{g[2:]}{b} | {b}{el}{b} | {r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1(len(s))].mean():+.1f} % | {f2(pf(r))} | '
                  f'{f2(pf(r[(K.yr <= 2020).values]))} / {f2(pf(r[(K.yr >= 2021).values]))} | {fire.sum() / 10.1:.0f} ({(fire & (t < gs)).sum() / 10.1:.0f}) |')
        print()
    # all four
    print('## All four (the 10:00 variants at today\'s stop) — sum · the worst of the four · the watched trades\n')
    rest = sum(books[n][1](key('e', '0930', LATE))[2].sum() for n in ('U1000', 'D1000'))
    restw = min(np.min(books[n][1](key('e', '0930', LATE))[2]) for n in ('U1000', 'D1000'))
    print('| gate | E | all four | worst | ' + ' | '.join(f'{t} {d[:4]}' for t, d in WATCH) + ' |')
    print('|---|---|---|---|' + '---|' * len(WATCH))
    cells = [('0930', LATE)] + [(g, e) for g in GATES for e in EARLY]
    for g, e in cells:
        tot, worst, watch = rest, restw, []
        for name in ('U0930', 'D0930'):
            K, res = books[name]; _, _, r = res(key('o', g, e)); tot += r.sum(); worst = min(worst, r.min())
        for tk, dd in WATCH:
            cell = '—'
            for name in ('U0930', 'D0930'):
                K, res = books[name]; z = np.where((K.ticker == tk).values & (K.d.astype(str) == dd).values)[0]
                if len(z): cell = f'{res(key("o", g, e))[2][z[0]]:+.0f}'
            watch.append(cell)
        el = 'none' if g == '0930' else ('∞' if e >= 10 ** 9 else str(e))
        print(f'| {g[:2]}:{g[2:]} | {el} | {tot:,.0f} | {worst:+.0f} % | ' + ' | '.join(watch) + ' |')
