"""GH-24 — level stops for the 09:30 variants (user 2026-11-23: "Let's move on to researching the stops for these variants next.
Maybe the same stops we have now will work well on them").

The 10:00 variants' defaults (GH-22 / GH-23): U1000 = the 30m bar VWAP above V30 (the 09:30–10:00 bar's VWAP) at a mark from
11:00; D1000 = the 1s break of M2 (max of the 09:30 and 10:00 bars' VWAPs) from 13:00. Here the same family on U0930 / D0930:
levels H30 (the 09:30–10:00 high), V30, M2; triggers BRK (a 1s break; the $5k fill from the next second), LAST (the last price
at a half-hour mark), BAR (the 30m bar's VWAP at a mark; the mark's $5k fill); armed 10:30 / 11:00 (H30, V30) and 12:00 / 13:00
(M2). The book: every U0930 / D0930 trade (09:30 entry at the open), the ONE volatility-stop EWMA from 09:30 (off · 700 · 500 ·
350, GH-13g) — held to 15:30 (as the 10:00 variants now) or with the variant's GH-6 rule (U0930 LOSER, D0930 T1530) — plus
one level stop; the first exit wins.
    cd research && python3 scripts/equity/gaphv_stops0930.py > logs/gaphv_stops0930.log 2>&1
    cd research && python3 scripts/equity/gaphv_stops0930.py --show > data/equity/gaphv/stops0930.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, io, contextlib, time
from multiprocessing import Pool
sys.path.insert(0, 'scripts/equity')
from gaphv_volstop3 import fill, BARS, O, CLOSE

ARMS = {'H30': ('1030', '1100'), 'V30': ('1030', '1100'), 'M2': ('1200', '1300')}
OUT = 'data/equity/gaphv/stops0930.parquet'
def s_(m): return int(m[:2]) * 3600 + int(m[2:]) * 60

def bvw(sec, px, vol, a, b):
    m = (sec >= a) & (sec < b)
    return (px[m] * vol[m]).sum() / vol[m].sum() if m.any() else np.nan

def one(sec, px, vol, close):
    w1 = (sec >= O) & (sec < 36000)
    if not w1.any(): return {}
    v1, v2 = bvw(sec, px, vol, 34200, 36000), bvw(sec, px, vol, 36000, 37800)
    lv = {'H30': px[w1].max(), 'V30': v1, 'M2': np.nanmax([v1, v2]) if np.isfinite([v1, v2]).any() else np.nan}
    r = {k: v for k, v in lv.items()}
    for L, arms in ARMS.items():
        for a in arms:
            hit = np.where((sec >= s_(a)) & (sec < 55800) & (px > lv[L]))[0] if np.isfinite(lv[L]) else []
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
        E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd', 'u0930', 'd0930', 'close'])
        E = E[E.u0930 | E.d0930]
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
        from gaphv_exits import X, simulate, pf, f2, CHK
        from gaphv_volstop import hm2s
    X = (X.merge(pd.read_parquet(OUT), on=['ticker', 'd'])
          .merge(pd.read_parquet('data/equity/gaphv/stops.parquet')[['ticker', 'd'] + [c for c in pd.read_parquet('data/equity/gaphv/stops.parquet').columns if c.startswith('bv')]], on=['ticker', 'd'], how='left')
          .merge(pd.read_parquet('data/equity/gaphv/volstop6.parquet')[['ticker', 'd', 'pinf_a700_b500_t', 'pinf_a700_b500_x']], on=['ticker', 'd'], how='left'))
    def lab(m): return f'{m[:2]}:{m[2:]}'
    print('# GH-24 — level stops for the 09:30 variants\n')
    print('The 09:30 short at the open; the ONE volatility-stop EWMA from 09:30 (off · 700 · 500 · 350); held to 15:30, or with the '
          'variant\'s GH-6 rule. H30 = the 09:30–10:00 high · V30 = the 09:30–10:00 bar\'s VWAP · M2 = max(the 09:30 and 10:00 bars\' '
          'VWAPs). BRK = a 1s break · LAST = the last price at a mark · BAR = the 30m bar\'s VWAP at a mark. Σ = Σ % (the short, '
          'gross); PF halves = 2016–2020 / 2021–2026. ⭐ = the 10:00 variants\' defaults.\n')
    for name, flag, rule in (('U0930', 'u0930', 'LOSER'), ('D0930', 'd0930', 'T1530')):
        A = X[X[flag] & np.isfinite(X.open) & (X.open > 0)].copy().reset_index(drop=True); A['entry'] = A.open
        h1 = (A.yr <= 2020).values; k1 = max(1, len(A) // 100)
        st = A.pinf_a700_b500_t.values.astype(float); sx = A.pinf_a700_b500_x.values
        def base(use_rule):
            if use_rule:
                px, at = simulate(A, rule); bsec = np.array([57600 if a == 'close' else hm2s(a) for a in at]).astype(float)
            else:
                px, bsec = A.x1530.values, np.full(len(A), 55800.0)
            v = (st > 0) & np.isfinite(sx) & (st < bsec)
            ex_t = np.where(v, st, bsec); ex_x = np.where(v, sx, px); ex_x = np.where(np.isfinite(ex_x), ex_x, A.close.values)
            return ex_t, 100 * (1 - ex_x / A.entry.values)
        print(f'## {name}, n = {len(A):,}\n')
        for use_rule, title in ((False, 'held to 15:30'), (True, f'with the GH-6 rule ({rule})')):
            ex_t, r0 = base(use_rule)
            print(f'**{title}**\n')
            print('| book | Σ | PF (halves) | mean | worst | CVaR 1 % | fired · covered: now / baseline |')
            print('|---|---|---|---|---|---|---|')
            def line(lab_, r, fired=None, bold=False):
                s = np.sort(r); b = '**' if bold else ''
                cov = f'{fired.mean():.0%} · {r[fired].mean():+.2f} / {r0[fired].mean():+.2f} %' if fired is not None and fired.any() else ''
                return (f'| {b}{lab_}{b} | {r.sum():,.0f} ({100 * (r.sum() / r0.sum() - 1):+.1f} %) | {f2(pf(r))} ({f2(pf(r[h1]))} / {f2(pf(r[~h1]))}) | '
                        f'{r.mean():+.2f} % | {s[0]:+.0f} % | {s[:k1].mean():+.1f} % | {cov} |')
            print(line('baseline', r0, bold=True))
            for L, arms in ARMS.items():
                lvl = A[L].values
                for a in arms:
                    asec = hm2s(a)
                    bt = A[f'brk_{L}_{a}_t'].values.astype(float); bx = A[f'brk_{L}_{a}_x'].values
                    fired = (bt > 0) & np.isfinite(bx) & (bt < ex_t)
                    star = ' ⭐' if (L, a) == ('M2', '1300') else ''
                    print(line(f'{L} · BRK · from {lab(a)}{star}', np.where(fired, 100 * (1 - bx / A.entry.values), r0), fired))
                    for trig in ('LAST', 'BAR'):
                        mt = np.full(len(A), -1.0); mx = np.full(len(A), np.nan)
                        for m in CHK:
                            ms = hm2s(m)
                            if ms < asec: continue
                            val = A[f'p_{m}'].values if trig == 'LAST' else A['bv' + m].values
                            go = (mt < 0) & np.isfinite(val) & (val > lvl) & np.isfinite(A[f'x{m}'].values)
                            mt[go] = ms; mx[go] = A[f'x{m}'].values[go]
                        fired = (mt > 0) & (mt < ex_t)
                        star = ' ⭐' if (L, trig, a) == ('V30', 'BAR', '1100') else ''
                        print(line(f'{L} · {trig} · from {lab(a)}{star}', np.where(fired, 100 * (1 - mx / A.entry.values), r0), fired))
            print()
