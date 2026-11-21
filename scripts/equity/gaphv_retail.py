"""GH-5 — the RETAIL SHARE (BJZZ: Σ retail_volume / Σ total_volume, FlushFader's V2-54 feature) on the high-volume variants
(user 2026-11-21: "Does the new retail share of volume feature make any difference to any of the setups?").
FlushFader's own window = the last 1200 PRESENT 1s bars before the decision (rs1200_0925: bars before 09:25, premarket;
rs1200_1000: bars before 10:00, the premarket included when the session has fewer). Plus features.parquet's pm_retail
(04:00–09:25) and retail30 (09:30–10:00).
    cd research && python3 scripts/equity/gaphv_retail.py > logs/gaphv_retail.log 2>&1   (pass 1: data/equity/gaphv/retail.parquet)
    cd research && python3 scripts/equity/gaphv_retail.py --report > data/equity/gaphv/retail.md
"""
import duckdb, numpy as np, pandas as pd, os, sys, time
from multiprocessing import Pool
BARS = 'data/intraday_1s_slim'

def day(args):
    d, tk = args
    f = f'{BARS}/{d}.parquet'
    if not os.path.exists(f): return []
    con = duckdb.connect(); con.execute("SET memory_limit='1GB'"); con.execute("SET threads=2")
    B = con.execute(f"""SELECT ticker, bucket, total_volume, retail_volume FROM '{f}' WHERE ticker IN (SELECT unnest(?))
                        AND bucket >= 14400 AND bucket < 36000 AND volume > 0 ORDER BY ticker, bucket""", [tk]).df()
    out = []
    for t, g in B.groupby('ticker'):
        s, tot, ret = g.bucket.values, g.total_volume.values.astype(float), g.retail_volume.values.astype(float)
        r = {'ticker': t, 'd': d}
        for lab, cut in (('rs1200_0925', 33900), ('rs1200_1000', 36000)):
            k = np.searchsorted(s, cut); a = max(k - 1200, 0)
            T = tot[a:k].sum(); r[lab] = ret[a:k].sum() / T if T > 0 else np.nan
        out.append(r)
    return out

def pf(x):
    x = x[np.isfinite(x)]; g, l = x[x > 0].sum(), -x[x < 0].sum()
    return np.inf if l == 0 else g / l
def f2(x): return 'inf' if np.isinf(x) else f'{x:.2f}'

if __name__ == '__main__' and '--report' not in sys.argv:
    t0 = time.time()
    E = pd.read_parquet('data/equity/gaphv/events.parquet', columns=['ticker', 'd'])
    jobs = [(str(pd.Timestamp(d).date()), list(g.ticker)) for d, g in E.groupby('d')]
    res = []
    with Pool(8) as p:
        for r in p.imap_unordered(day, jobs, chunksize=4): res.extend(r)
    R = pd.DataFrame(res); R['d'] = pd.to_datetime(R.d).dt.date
    R.to_parquet('data/equity/gaphv/retail.parquet', index=False)
    print(f'wrote {len(R):,}, {time.time() - t0:.0f}s')
elif __name__ == '__main__':
    X = (pd.read_parquet('data/equity/gaphv/events.parquet').merge(pd.read_parquet('data/equity/gaphv/features.parquet'), on=['ticker', 'd'])
         .merge(pd.read_parquet('data/equity/gaphv/path.parquet'), on=['ticker', 'd']).merge(pd.read_parquet('data/equity/gaphv/retail.parquet'), on=['ticker', 'd']))
    X['yr'] = pd.to_datetime(X.d).dt.year
    print('# GH-5 — the retail share on the high-volume variants\n')
    print('## Coverage — the median share by year (all events)\n')
    print('| year | n | pm_retail | rs1200_0925 | retail30 | rs1200_1000 | share = 0 (rs1200_1000) |')
    print('|---|---|---|---|---|---|---|')
    for y, g in X.groupby('yr'):
        print(f'| {y} | {len(g):,} | {g.pm_retail.median():.3f} | {g.rs1200_0925.median():.3f} | {g.retail30.median():.3f} | '
              f'{g.rs1200_1000.median():.3f} | {(g.rs1200_1000 == 0).mean():.0%} |')
    print()
    SETS = [('U0930 short', X.u0930, 'open', -1, ['pm_retail', 'rs1200_0925']),
            ('D0930 short', X.d0930, 'open', -1, ['pm_retail', 'rs1200_0925']),
            ('U1000 short', X.u1000, 'x1000', -1, ['pm_retail', 'retail30', 'rs1200_1000']),
            ('D1000 short', X.d1000, 'x1000', -1, ['pm_retail', 'retail30', 'rs1200_1000']),
            ('U1000 calm · held LONG (GF-42)', X.u1000 & (X.volat30 < 40) & (X.ret30 >= 0), 'x1000', 1, ['pm_retail', 'retail30', 'rs1200_1000']),
            ('U0930 names calm · held LONG at 10:00', X.u0930 & (X.volat30 < 40) & (X.ret30 >= 0), 'x1000', 1, ['pm_retail', 'retail30', 'rs1200_1000'])]
    print('## The trade by retail-share quintile (within the set) — PF · mean, and PF 2016–20 / 2021–26\n')
    print('Shorts gross (as GH-1..3); the longs net of 0.7c / share (as GH-4). Q1 = the lowest share; (the quintile\'s median share).\n')
    for name, m, e, sgn, feats in SETS:
        K = X[m & np.isfinite(X[e]) & (X[e] > 0)].copy()
        K['r'] = sgn * (K.close / K[e] - 1) - (0.007 / K[e] if sgn > 0 else 0)
        print(f'### {name} (n {len(K):,}, PF {f2(pf(K.r.values))})\n')
        print('| feature | Q1 | Q2 | Q3 | Q4 | Q5 | ρ(·, r) |')
        print('|---|---|---|---|---|---|---|')
        for f in feats:
            ok = np.isfinite(K[f]).values; s = K[f][ok]
            q = pd.qcut(s.rank(method='first'), 5, labels=False).values
            cs = []
            for i in range(5):
                g = K[ok][q == i]
                cs.append(f'{f2(pf(g.r.values))} · {100 * g.r.mean():+.2f} % ({f2(pf(g.r[g.yr <= 2020].values))} / {f2(pf(g.r[g.yr >= 2021].values))}) [{g[f].median():.3f}]')
            print(f'| {f} | ' + ' | '.join(cs) + f' | {s.rank().corr(K.r[ok].rank()):+.2f} |')
        # FlushFader's gate value
        f = 'rs1200_1000' if e == 'x1000' else 'rs1200_0925'
        a, b = K[K[f] >= 0.153], K[K[f] < 0.153]
        print(f'\nFlushFader\'s gate ({f} ≥ 0.153): pass {len(a):,} · PF {f2(pf(a.r.values))} · {100 * a.r.mean():+.2f} %  /  fail {len(b):,} · PF {f2(pf(b.r.values))} · {100 * b.r.mean():+.2f} %\n')
