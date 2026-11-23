"""GH-13e — the EWMA disaster stop ARMED FROM A CLOCK TIME (user 2026-11-22: "We'll want to consider whether only activating
the stop after 9:40 ET makes things better"). GH-13c / GH-13d's stop — ew · any (both sides), the 4-slot warm-up (W = 3) —
computed from the entry as ever, but it may FIRE only from the gate: 09:30 (= GH-13d's W = 3, the control), 09:35, 09:40,
09:45, 10:00. The 10:00 variants are entered at 10:00: the gate only moves the 09:30 ones (U0930 / D0930). The $5k fill, no
time cap; on top of each variant's GH-6 exit.
    cd research && python3 scripts/equity/gaphv_volstop4.py > logs/gaphv_volstop4.log 2>&1
    cd research && python3 scripts/equity/gaphv_volstop4.py --table > data/equity/gaphv/volstop4.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, ew_series, hms, BARS, O, CLOSE, LAST

TH = (300, 350, 400)
W = 3
GATES = {'0930': 34200, '0935': 34500, '0940': 34800, '0945': 35100, '1000': 36000}
OUT = 'data/equity/gaphv/volstop4.parquet'

def one(sec, px, vol, close, entries):
    r = {}
    for tag, e0, _ in entries:
        t, ew, k = ew_series(sec, px, vol, e0)
        for g, gs in GATES.items():
            for th in TH:
                hit = (ew >= th) & (k >= W) & (t >= gs) & (t < LAST) if len(t) else np.array([], bool)
                if len(hit) and hit.any():
                    i = np.argmax(hit); tt = int(t[i]) + 1
                    r[f'{tag}_g{g}_{th}_t'] = tt; r[f'{tag}_g{g}_{th}_x'] = fill(sec, px, vol, tt, close)
                else:
                    r[f'{tag}_g{g}_{th}_t'] = -1; r[f'{tag}_g{g}_{th}_x'] = np.nan
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

if '--table' not in sys.argv:
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
    V = [('U0930', 'u0930', 'open', 'LOSER', 'o'), ('D0930', 'd0930', 'open', 'T1530', 'o')]
    print('# GH-13e — the EWMA stop armed from a clock time (ew · any, W = 3 = 4 slots)\n')
    print('The stop computed from the 09:30 entry, allowed to fire from the gate on. 09:30 = GH-13d W = 3 (the control). On top '
          'of each variant\'s GH-6 exit; the $5k fill, no time cap. sum = Σ % · worst · CVaR 1 % · PF (halves) · fired / yr · '
          'the fires the gate delays: their result under this gate vs at 09:30.\n')
    for name, flag, ent, rule, tag in V:
        K = X[X[flag] & np.isfinite(X[ent]) & (X[ent] > 0)].copy().reset_index(drop=True); K['entry'] = K[ent]
        px, at = simulate(K, rule); base = 1 - px / K.entry.values
        bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at])
        k1 = max(1, len(K) // 100)
        def res(g, th):
            t = K[f'{tag}_g{g}_{th}_t'].values; x = K[f'{tag}_g{g}_{th}_x'].values
            fire = (t > 0) & np.isfinite(x) & (t < bsec)
            return fire, t, 100 * np.where(fire, 1 - x / K.entry.values, base)
        print(f'## {name} ({rule}), n = {len(K):,}\n')
        print('| gate | bp | sum | worst | CVaR 1 % | PF | PF halves | fired / yr | 09:30 fires before the gate: n / yr · mean now / at 09:30 |')
        print('|---|---|---|---|---|---|---|---|---|')
        r0 = 100 * base; s0 = np.sort(r0)
        print(f'| — | none | {r0.sum():,.0f} | {s0[0]:+.0f} % | {s0[:k1].mean():+.1f} % | {f2(pf(r0))} | | | |')
        for th in TH:
            f0, t0_, r30 = res('0930', th)
            for g, gs in GATES.items():
                fire, t, r = res(g, th); s = np.sort(r)
                moved = f0 & (t0_ < gs)
                mv = f'{moved.sum() / 10.1:.1f} · {r[moved].mean():+.1f} / {r30[moved].mean():+.1f} %' if moved.any() and g != '0930' else ''
                b = '**' if (g, th) in (('0930', 350), ('0940', 350)) else ''
                print(f'| {b}{g[:2]}:{g[2:]}{b} | {b}{th}{b} | {r.sum():,.0f} | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {f2(pf(r))} | '
                      f'{f2(pf(r[(K.yr <= 2020).values]))} / {f2(pf(r[(K.yr >= 2021).values]))} | {fire.sum() / 10.1:.0f} | {mv} |')
        print()
        f0, t0_, r30 = res('0930', 350); _, _, r40 = res('0940', 350)
        d = r40 - r30; i = np.argsort(d)
        print(f'{name} at 350 bp, 09:40 vs 09:30 — the five trades it hurts most: ' +
              '; '.join(f'{K.ticker[j]} {K.d[j]} {r30[j]:+.0f} → {r40[j]:+.0f} %' for j in i[:5]) +
              ' · helps most: ' + '; '.join(f'{K.ticker[j]} {K.d[j]} {r30[j]:+.0f} → {r40[j]:+.0f} %' for j in i[::-1][:5]) + '\n')
        for tk, dd in (('PGHL', '2024-11-26'), ('ZJYL', '2023-12-18')):
            z = K[(K.ticker == tk) & (K.d.astype(str) == dd)]
            if len(z):
                cells = []
                for g in GATES:
                    t = z[f'{tag}_g{g}_350_t'].iloc[0]; x = z[f'{tag}_g{g}_350_x'].iloc[0]
                    cells.append(f'{g[:2]}:{g[2:]}: ' + (f'{hms(t)} @ \\${x:.2f} ({100 * (1 - x / z.entry.iloc[0]):+.0f} %)' if t > 0 else 'never'))
                print(f'{tk} {dd} at 350 bp — ' + ' · '.join(cells) + '\n')
