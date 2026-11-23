"""GH-23 — D1000: a stop above the MAX of the first half-hour bars' VWAPs (user 2026-11-23: "Let's focus on D1000 for now. What if
we used the max of 9:30 ET and 10 ET bars' VWAPs as the reference? Or even added 10:30 ET to that? Does absolutely no stop at
least turn pf1 neutral").

Levels: M2 = max(the 09:30–10:00 bar's VWAP, the 10:00–10:30 bar's) — known at 10:30; M3 = max(the same, the 10:30–11:00 bar's) —
known at 11:00. Triggers: BRK (a 1s break; the $5k fill from the next second), LAST (the last price at a half-hour mark), BAR
(the VWAP of the 30-minute bar ending at the mark); the mark triggers fill at the mark's $5k fill. Armed from 10:30 (M2 only),
11:00, 12:00, 13:00 (GH-21: above H30 a gap-down short stays good until ~13:00 and turns neutral ~14:00).
THE BOOK: GH-20's eligible D1000 universe (z > −2, the 09:30 stepped stop not fired before 10:00); the 10:00 short held to 15:30
with the ONE volatility-stop EWMA from 09:30 (GH-22's ruling); each level × trigger × arming adds a cover; the first exit wins.
    cd research && python3 scripts/equity/gaphv_d1000.py > logs/gaphv_d1000.log 2>&1
    cd research && python3 scripts/equity/gaphv_d1000.py --show > data/equity/gaphv/d1000.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, BARS, O, CLOSE

ARMS = {'1030': 37800, '1100': 39600, '1200': 43200, '1300': 46800}
OUT = 'data/equity/gaphv/d1000.parquet'

def bvw(sec, px, vol, a, b):
    m = (sec >= a) & (sec < b)
    return (px[m] * vol[m]).sum() / vol[m].sum() if m.any() else np.nan

def one(sec, px, vol, close):
    v1, v2, v3 = bvw(sec, px, vol, 34200, 36000), bvw(sec, px, vol, 36000, 37800), bvw(sec, px, vol, 37800, 39600)
    lv = {'M2': np.nanmax([v1, v2]) if np.isfinite([v1, v2]).any() else np.nan,
          'M3': np.nanmax([v1, v2, v3]) if np.isfinite([v1, v2, v3]).any() else np.nan}
    r = dict(v1=v1, v2=v2, v3=v3, M2=lv['M2'], M3=lv['M3'])
    for L, level in lv.items():
        for a, asec in ARMS.items():
            if L == 'M3' and asec < 39600: continue
            hit = np.where((sec >= asec) & (sec < 55800) & (px > level))[0] if np.isfinite(level) else []
            k = f'brk_{L}_{a}'
            if len(hit):
                t = int(sec[hit[0]]); r[k + '_t'] = t; r[k + '_x'] = fill(sec, px, vol, t + 1, close)
            else:
                r[k + '_t'] = -1; r[k + '_x'] = np.nan
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
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'd1000', 'close'])
        E = E[E.d1000]
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
        from gaphv_exits import X, pf, f2, CHK
        from gaphv_volstop import hm2s
    X = (X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
          .merge(pd.read_parquet('data/equity/gaphv/stops.parquet').drop(columns=['close'], errors='ignore'), on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/entry10.parquet')[['ticker', 'd', 'z']], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet')[['ticker', 'd', 'pinf_a700_b500_t', 'pinf_a700_b500_x']], on=['ticker', 'd'], how='left'))
    def lab(m): return f'{m[:2]}:{m[2:]}'
    A = X[X.d1000 & ~(X.z < -2.0) & ~((X.pinf_a700_b500_t > 0) & (X.pinf_a700_b500_t < 36000))
          & np.isfinite(X.x1000) & (X.x1000 > 0)].copy().reset_index(drop=True)
    h1 = (A.yr <= 2020).values
    t = A.pinf_a700_b500_t.values.astype(float); x = A.pinf_a700_b500_x.values
    vs = (t > 0) & np.isfinite(x) & (t < 55800)
    ex_t = np.where(vs, t, 55800.0); ex_x = np.where(vs, x, A.x1530.values)
    ex_x = np.where(np.isfinite(ex_x), ex_x, A.close.values)
    r0 = 100 * (1 - ex_x / A.x1000.values); k1 = max(1, len(A) // 100)
    print('# GH-23 — D1000: a stop above the max of the first half-hour bars\' VWAPs\n')
    print(f'The eligible D1000 universe (z > −2, the 09:30 stepped stop not fired before 10:00): {len(A):,}. The 10:00 short to 15:30 + '
          'the ONE volatility-stop EWMA from 09:30. M2 = max(the 09:30 and 10:00 bars\' VWAPs), M3 = with the 10:30 bar. BRK = a 1s break; '
          'LAST = the last price at a mark; BAR = the 30m bar\'s VWAP at a mark. Σ = Σ % (the short, gross).\n')
    rows = []
    def add(lab_, r, fired=None):
        s = np.sort(r)
        rows.append((lab_, r.sum(), pf(r), pf(r[h1]), pf(r[~h1]), s[0], s[:k1].mean(),
                     (fired.mean() if fired is not None else np.nan), (r[fired].mean() if fired is not None and fired.any() else np.nan),
                     (r0[fired].mean() if fired is not None and fired.any() else np.nan)))
    add('**baseline (no level stop)**', r0)
    for L in ('M2', 'M3'):
        for a, asec in ARMS.items():
            if L == 'M3' and asec < 39600: continue
            bt = A[f'brk_{L}_{a}_t'].values.astype(float); bx = A[f'brk_{L}_{a}_x'].values
            fired = (bt > 0) & np.isfinite(bx) & (bt < ex_t)
            add(f'{L} · BRK · from {lab(a)}', np.where(fired, 100 * (1 - bx / A.x1000.values), r0), fired)
            for trig in ('LAST', 'BAR'):
                mt = np.full(len(A), -1.0); mx = np.full(len(A), np.nan)
                for m in CHK:
                    ms = hm2s(m)
                    if ms < asec: continue
                    val = A[f'p_{m}'].values if trig == 'LAST' else A['bv' + m].values
                    go = (mt < 0) & np.isfinite(val) & (val > A[L].values) & np.isfinite(A[f'x{m}'].values)
                    mt[go] = ms; mx[go] = A[f'x{m}'].values[go]
                fired = (mt > 0) & (mt < ex_t)
                add(f'{L} · {trig} · from {lab(a)}', np.where(fired, 100 * (1 - mx / A.x1000.values), r0), fired)
    print('| book | Σ | PF (halves) | worst | CVaR 1 % | fired · covered: now / baseline |')
    print('|---|---|---|---|---|---|')
    b0 = rows[0][1]
    for (lab_, s, p, p1, p2, w, cv, fr, cn, cb) in rows:
        cov = f'{fr:.0%} · {cn:+.2f} / {cb:+.2f} %' if np.isfinite(fr) else ''
        print(f'| {lab_} | {s:,.0f} ({100 * (s / b0 - 1):+.1f} %) | {f2(p)} ({f2(p1)} / {f2(p2)}) | {w:+.0f} % | {cv:+.1f} % | {cov} |')
    print()
    # the forward view: above M2 / M3 at a mark → the short from the mark to 15:30
    print('**Above the level at a mark (the last price) → the short from the mark to 15:30**: share · mean · PF (halves\' means)\n')
    print('| mark | above M2 | above M3 |')
    print('|---|---|---|')
    for m in CHK:
        ms = hm2s(m)
        x0 = A[f'x{m}'].values; x1 = A.x1530.values; ok = np.isfinite(x0) & np.isfinite(x1) & (x0 > 0)
        r = 100 * (1 - x1 / x0); cells = []
        for L, frm in (('M2', 37800), ('M3', 39600)):
            if ms < frm: cells.append('—'); continue
            ab = ok & np.isfinite(A[f'p_{m}'].values) & (A[f'p_{m}'].values > A[L].values)
            cells.append(f'{ab.sum() / ok.sum():.0%} · {r[ab].mean():+.2f} % · {f2(pf(r[ab]))} ({r[ab & h1].mean():+.2f} / {r[ab & ~h1].mean():+.2f})')
        print(f'| {lab(m)} | ' + ' | '.join(cells) + ' |')
